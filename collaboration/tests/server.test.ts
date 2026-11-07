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

const KEY = 'test-service-key-0123456789abcdef'
const TENANT = '11111111-1111-4111-8111-111111111111'
const schema = buildSchema(loadManifests(fileURLToPath(new URL('../../schema/editor/extensions', import.meta.url))))
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
  const before = api.docs.get(name)?.updates.length ?? 0
  assert.equal((await fetch(`http://127.0.0.1:${port}/internal/reset`, { method: 'POST', body: JSON.stringify({ documentName: name }) })).status, 401)
  const r = await internal('/internal/reset', { documentName: name })
  assert.equal(r.status, 204)
  await waitFor(() => a.closeReasons.includes('raavi:reset'), 5000, 'reset close')
  await new Promise((res) => setTimeout(res, 300))
  assert.equal(api.docs.get(name)?.updates.length ?? 0, before, 'discarded state was not stored')
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
