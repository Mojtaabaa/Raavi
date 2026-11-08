import { test, after, before } from 'node:test'
import assert from 'node:assert/strict'
import { fileURLToPath } from 'node:url'
import { WebSocket } from 'ws'
import * as Y from 'yjs'
import { HocuspocusProvider, HocuspocusProviderWebsocket } from '@hocuspocus/provider'
import { buildSchema, loadManifests } from '../src/schema.ts'
import { createCollaborationServer } from '../src/server.ts'
import { sign } from '../src/signature.ts'
import { startMockApi } from './support/mockApi.ts'
import { boardToJson, ELEMENTS, ORDER } from '../src/board.ts'

const KEY = 'test-service-key-0123456789abcdef'
const TENANT = '11111111-1111-4111-8111-111111111111'
const schema = buildSchema(loadManifests(fileURLToPath(new URL('../../schema/editor/extensions', import.meta.url))))
const boardName = (n: number) => `${TENANT}:board:00000000-0000-4000-8000-${String(n).padStart(12, '0')}:live`
const docName = (n: number) => `${TENANT}:page:00000000-0000-4000-8000-${String(n).padStart(12, '0')}:draft`

let api: Awaited<ReturnType<typeof startMockApi>>
let collab: ReturnType<typeof createCollaborationServer>
let port = 0

async function startServer() {
  collab = createCollaborationServer({ apiUrl: api.url, serviceKey: KEY, port: 0, recheckSeconds: 3600, snapshotEvery: 3, debounce: 50, maxDebounce: 200, allowedOrigins: [] }, schema, { logger: { info() {}, warn() {} } })
  await collab.listen()
  port = collab.server.address.port
}

before(async () => {
  api = await startMockApi(KEY)
  await startServer()
})
after(async () => {
  await collab.destroy()
  await api.close()
})

export async function waitFor(cond: () => boolean, ms = 5000, label = 'condition') {
  const start = Date.now()
  while (!cond()) {
    if (Date.now() - start > ms) throw new Error(`timeout waiting for ${label}`)
    await new Promise((r) => setTimeout(r, 20))
  }
}

interface Client {
  doc: Y.Doc
  provider: HocuspocusProvider
  socket: HocuspocusProviderWebsocket
  closeReasons: string[]
  authFailed: boolean
}

export async function connect(name: string, userId: string, opts: { perm?: string; token?: string } = {}): Promise<Client> {
  const doc = new Y.Doc()
  const socket = new HocuspocusProviderWebsocket({ url: `ws://127.0.0.1:${port}`, WebSocketPolyfill: WebSocket, maxAttempts: 1 })
  const client: Client = { doc, socket, provider: undefined as unknown as HocuspocusProvider, closeReasons: [], authFailed: false }
  client.provider = new HocuspocusProvider({
    websocketProvider: socket,
    name,
    document: doc,
    token: opts.token ?? (await api.ticket({ userId, tenantId: TENANT, documentName: name, perm: opts.perm ?? 'edit' })),
    onClose: ({ event }) => client.closeReasons.push(String(event.reason ?? '')),
    onAuthenticationFailed: () => (client.authFailed = true),
  })
  client.provider.attach?.()
  return client
}

const text = (doc: Y.Doc) => doc.getXmlFragment('default').toString()

function addParagraph(doc: Y.Doc, value: string) {
  const p = new Y.XmlElement('paragraph')
  p.insert(0, [new Y.XmlText(value)])
  doc.getXmlFragment('default').push([p])
}

async function internal(path: string, body: unknown) {
  const bytes = new TextEncoder().encode(JSON.stringify(body))
  return fetch(`http://127.0.0.1:${port}${path}`, { method: 'POST', body: bytes, headers: { 'x-raavi-signature': sign(KEY, 'POST', path, bytes), 'Content-Type': 'application/json' } })
}

function close(...clients: Client[]) {
  for (const c of clients) {
    c.provider.destroy()
    c.socket.destroy()
  }
}

test('concurrent editors converge, every change is persisted through the API, and the document reloads from storage', async () => {
  const name = docName(1)
  const a = await connect(name, 'user-a')
  const b = await connect(name, 'user-b')
  await waitFor(() => a.provider.isSynced && b.provider.isSynced, 5000, 'sync')
  assert.match(text(a.doc), /seeded/) // first load is seeded from the draft JSON

  for (let i = 0; i < 20; i++) {
    addParagraph(a.doc, `a${i}`)
    addParagraph(b.doc, `b${i}`)
  }
  await waitFor(() => text(a.doc) === text(b.doc) && text(a.doc).includes('a19') && text(a.doc).includes('b19'), 5000, 'convergence')

  const flush = await internal('/internal/flush', { documentName: name })
  assert.equal(flush.status, 204)
  const stored = api.docs.get(name)!
  const json = JSON.stringify(stored.json)
  assert.ok(json.includes('a19') && json.includes('b19'), 'converted JSON sent to the API')
  assert.deepEqual([...stored.contributors].sort(), ['user-a', 'user-b'])

  // Persisted state alone (snapshot + updates) reproduces the document.
  const replay = new Y.Doc()
  if (stored.snapshot) Y.applyUpdate(replay, Buffer.from(stored.snapshot, 'base64'))
  for (const u of stored.updates.filter((x) => x.seq > stored.snapshotSeq)) Y.applyUpdate(replay, Buffer.from(u.update, 'base64'))
  assert.equal(text(replay), text(a.doc))

  // Restart the server: the document is loaded from storage, not seeded again.
  close(a, b)
  await collab.destroy()
  await startServer()
  const c = await connect(name, 'user-c')
  await waitFor(() => c.provider.isSynced, 5000, 'resync')
  assert.equal(text(c.doc), text(replay))
  assert.equal(text(c.doc).match(/seeded/g)?.length, 1)
  close(c)
})

test('revocation closes the user’s connections immediately; others keep editing', async () => {
  const name = docName(2)
  const keep = await connect(name, 'keep')
  const revoked = await connect(name, 'revoked')
  await waitFor(() => keep.provider.isSynced && revoked.provider.isSynced)
  api.permissions.set('revoked', 'none')
  const r = await internal('/internal/recheck', { tenantId: TENANT, userId: 'revoked' })
  assert.equal(r.status, 204)
  await waitFor(() => revoked.closeReasons.includes('raavi:revoked'), 5000, 'revocation')
  addParagraph(revoked.doc, 'after-revocation')
  addParagraph(keep.doc, 'still-editing')
  await new Promise((res) => setTimeout(res, 300))
  const server = collab.hocuspocus.documents.get(name)!
  assert.ok(server.getXmlFragment('default').toString().includes('still-editing'))
  assert.ok(!server.getXmlFragment('default').toString().includes('after-revocation'))
  api.permissions.delete('revoked')
  close(keep, revoked)
})

test('read-only tickets cannot change the document', async () => {
  const name = docName(3)
  const editor = await connect(name, 'editor')
  const viewer = await connect(name, 'viewer', { perm: 'view' })
  await waitFor(() => editor.provider.isSynced && viewer.provider.isSynced)
  addParagraph(viewer.doc, 'viewer-edit')
  addParagraph(editor.doc, 'editor-edit')
  await waitFor(() => text(viewer.doc).includes('editor-edit'))
  await new Promise((res) => setTimeout(res, 200))
  assert.ok(!collab.hocuspocus.documents.get(name)!.getXmlFragment('default').toString().includes('viewer-edit'))
  close(editor, viewer)
})

test('invalid, replayed or foreign tickets are refused', async () => {
  const name = docName(4)
  const token = await api.ticket({ userId: 'u', tenantId: TENANT, documentName: name })
  const first = await connect(name, 'u', { token })
  await waitFor(() => first.provider.isSynced)
  const replay = await connect(name, 'u', { token })
  await waitFor(() => replay.authFailed, 5000, 'replay refused')
  const foreign = await connect(name, 'u', { token: await api.ticket({ userId: 'u', tenantId: TENANT, documentName: docName(99) }) })
  await waitFor(() => foreign.authFailed, 5000, 'foreign refused')
  close(first, replay, foreign)
})

test('reset drops the live state without storing it; unsigned internal calls are refused', async () => {
  const name = docName(5)
  const a = await connect(name, 'a')
  await waitFor(() => a.provider.isSynced)
  addParagraph(a.doc, 'discard-me')
  await waitFor(() => collab.hocuspocus.documents.get(name)!.getXmlFragment('default').toString().includes('discard-me'))
  assert.equal((await fetch(`http://127.0.0.1:${port}/internal/reset`, { method: 'POST', body: JSON.stringify({ documentName: name }) })).status, 401)
  const r = await internal('/internal/reset', { documentName: name })
  assert.equal(r.status, 204)
  await waitFor(() => a.closeReasons.includes('raavi:reset'), 5000, 'reset close')
  // A debounced store of the edit may legitimately be in flight when the reset arrives (known, narrow race, see ADR-035); let it land, then
  // require that nothing is stored any more once the document is gone.
  await new Promise((res) => setTimeout(res, 300))
  const settled = api.docs.get(name)?.updates.length ?? 0
  await new Promise((res) => setTimeout(res, 300))
  assert.equal(api.docs.get(name)?.updates.length ?? 0, settled, 'nothing is stored after the reset has settled')
  assert.equal(collab.hocuspocus.documents.has(name), false)
  close(a)
})

test('content refused by the API is not kept: editors are reset', async () => {
  const name = docName(6)
  const a = await connect(name, 'a')
  await waitFor(() => a.provider.isSynced)
  api.rejectNextStore()
  addParagraph(a.doc, 'refused')
  await waitFor(() => a.closeReasons.includes('raavi:reset'), 5000, 'reset after refusal')
  close(a)
})

// ── Whiteboards (Phase 11c) ──────────────────────────────────────────────────────────────────

const ids = (doc: Y.Doc) => boardToJson(doc).map((e) => e.id)

function setField(doc: Y.Doc, id: string, field: string, value: unknown) {
  doc.getMap<Y.Map<unknown>>(ELEMENTS).get(id)!.set(field, value)
}

function addElement(doc: Y.Doc, id: string, fields: Record<string, unknown>) {
  doc.transact(() => {
    const inner = new Y.Map<unknown>()
    for (const [k, v] of Object.entries(fields)) inner.set(k, v)
    doc.getMap<Y.Map<unknown>>(ELEMENTS).set(id, inner)
    doc.getArray<string>(ORDER).push([id])
  })
}

test('whiteboards: the first session is seeded, editors converge field by field, the API gets the full state with the converted elements, and a restart resumes from it', async () => {
  const name = boardName(1)
  const a = await connect(name, 'user-a')
  const b = await connect(name, 'user-b')
  await waitFor(() => a.provider.isSynced && b.provider.isSynced, 5000, 'sync')
  assert.deepEqual(ids(a.doc), ['r1', 't1']) // seeded from the stored elements
  assert.equal(a.doc.getXmlFragment('default').length, 0) // boards do not use the page fragment

  // The same element: one moves it, the other recolours it — both changes survive. Different elements: both are added.
  setField(a.doc, 'r1', 'x', 300)
  setField(b.doc, 'r1', 'stroke', '#ef4444')
  addElement(a.doc, 'a1', { type: 'Arrow', x: 5, y: 5, points: [0, 0, 40, 10] })
  addElement(b.doc, 'b1', { type: 'Sticky', x: 9, y: 9, w: 170, h: 130, text: 'from b' })
  await waitFor(() => JSON.stringify(boardToJson(a.doc)) === JSON.stringify(boardToJson(b.doc)) && ids(a.doc).length === 4, 5000, 'convergence')
  const r1 = boardToJson(a.doc).find((e) => e.id === 'r1')!
  assert.deepEqual([r1.x, r1.stroke], [300, '#ef4444'])

  assert.equal((await internal('/internal/flush', { documentName: name })).status, 204)
  const stored = api.docs.get(name)!
  const sent = stored.json as { elements: { id: string; x?: number; stroke?: string }[] }
  assert.deepEqual(sent.elements.map((e) => e.id).sort(), ['a1', 'b1', 'r1', 't1'])
  assert.equal(sent.elements.find((e) => e.id === 'r1')!.x, 300)
  assert.deepEqual([...stored.contributors].sort(), ['user-a', 'user-b'])
  assert.equal(stored.snapshot, null, 'boards never use the snapshot/compaction path')
  assert.ok(!api.calls.some((c) => c.endsWith('/snapshot') && c.includes('board')))

  // The latest stored update is the full state: it alone reproduces the board, and a restarted server resumes from it (no second seeding).
  const replay = new Y.Doc()
  Y.applyUpdate(replay, Buffer.from(stored.updates.at(-1)!.update, 'base64'))
  assert.deepEqual(boardToJson(replay), boardToJson(a.doc))
  close(a, b)
  await collab.destroy()
  await startServer()
  const c = await connect(name, 'user-c')
  await waitFor(() => c.provider.isSynced, 5000, 'resync')
  assert.deepEqual(boardToJson(c.doc), boardToJson(replay))
  assert.equal(ids(c.doc).filter((id) => id === 'r1').length, 1)
  close(c)
})

test('whiteboards: concurrent deletes and reorders never lose or duplicate elements in the converted list', async () => {
  const name = boardName(2)
  const a = await connect(name, 'user-a')
  const b = await connect(name, 'user-b')
  await waitFor(() => a.provider.isSynced && b.provider.isSynced, 5000, 'sync')

  // a deletes t1; b edits it at the same time and also appends r1 a second time to the order.
  a.doc.transact(() => {
    a.doc.getMap(ELEMENTS).delete('t1')
    const order = a.doc.getArray<string>(ORDER)
    order.delete(order.toArray().indexOf('t1'), 1)
  })
  b.doc.getArray<string>(ORDER).push(['r1'])
  await waitFor(() => JSON.stringify(a.doc.getArray(ORDER).toArray()) === JSON.stringify(b.doc.getArray(ORDER).toArray()), 5000, 'order convergence')
  assert.deepEqual(ids(a.doc), ['r1']) // duplicate order entry ignored, deleted element gone
  assert.deepEqual(ids(b.doc), ['r1'])

  // An element that is in the map but missing from the order (lost-update shape) is still delivered, not dropped.
  a.doc.getMap<Y.Map<unknown>>(ELEMENTS).set('z9', (() => { const m = new Y.Map<unknown>(); m.set('type', 'Rect'); return m })())
  await waitFor(() => ids(b.doc).includes('z9'), 5000, 'orphan element')
  assert.deepEqual(ids(b.doc), ['r1', 'z9'])
  close(a, b)
})

test('whiteboards: content the API refuses resets the document for everyone; read-only tickets cannot change a board', async () => {
  const name = boardName(3)
  const a = await connect(name, 'user-a')
  const viewer = await connect(name, 'viewer', { perm: 'view' })
  await waitFor(() => a.provider.isSynced && viewer.provider.isSynced, 5000, 'sync')

  addElement(viewer.doc, 'v1', { type: 'Rect', x: 1, y: 1, w: 5, h: 5 })
  setField(a.doc, 'r1', 'x', 77)
  await new Promise((res) => setTimeout(res, 300))
  const server = collab.hocuspocus.documents.get(name)!
  assert.ok(!boardToJson(server).some((e) => e.id === 'v1'), 'viewer edits are ignored')
  assert.equal(boardToJson(server).find((e) => e.id === 'r1')!.x, 77)

  api.rejectNextStore()
  setField(a.doc, 'r1', 'x', 78)
  await waitFor(() => a.closeReasons.includes('raavi:reset') && viewer.closeReasons.includes('raavi:reset'), 5000, 'reset after refusal')
  close(a, viewer)
})

test('whiteboards: a board address needs a board ticket, and page tickets do not open boards', async () => {
  const board = boardName(4)
  const page = docName(4)
  const wrong = await connect(board, 'user-a', { token: await api.ticket({ userId: 'user-a', tenantId: TENANT, documentName: page }) })
  await waitFor(() => wrong.authFailed, 5000, 'rejected')
  assert.equal(wrong.provider.isSynced, false)
  close(wrong)
  const malformed = await connect(`${TENANT}:board:not-a-guid:live`, 'user-a')
  await waitFor(() => malformed.authFailed, 5000, 'invalid address rejected')
  close(malformed)
})
