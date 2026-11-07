import { fileURLToPath } from 'node:url'
import { WebSocket } from 'ws'
import * as Y from 'yjs'
import { HocuspocusProvider, HocuspocusProviderWebsocket } from '@hocuspocus/provider'
import { buildSchema, loadManifests } from '../../src/schema.ts'
import { createCollaborationServer } from '../../src/server.ts'
import { sign } from '../../src/signature.ts'
import { startMockApi } from '../support/mockApi.ts'

/**
 * Load test (ADR-011 acceptance): EDITORS clients edit one document at the same time; every client must converge to the
 * same state containing every edit, and the persisted state (via the internal API) must contain all of them too.
 * The API is the in-process mock (persistence path, signatures and conversion are real); network is loopback.
 *   node tests/load/run.ts [editors=100] [editsPerEditor=20]
 */
const EDITORS = Number(process.argv[2] ?? 100)
const EDITS = Number(process.argv[3] ?? 20)
const KEY = 'load-test-service-key-0123456789abcdef'
const TENANT = '11111111-1111-4111-8111-111111111111'
const NAME = `${TENANT}:page:00000000-0000-4000-8000-000000000100:draft`

const api = await startMockApi(KEY)
const schema = buildSchema(loadManifests(fileURLToPath(new URL('../../../schema/editor/extensions', import.meta.url))))
const collab = createCollaborationServer({ apiUrl: api.url, serviceKey: KEY, port: 0, recheckSeconds: 3600, snapshotEvery: 50, debounce: 2000, maxDebounce: 10000, allowedOrigins: [] }, schema, { logger: { info() {}, warn() {} } })
await collab.listen()
const port = collab.server.address.port

const wait = async (cond: () => boolean, ms: number, label: string) => {
  const start = Date.now()
  while (!cond()) {
    if (Date.now() - start > ms) throw new Error(`timeout: ${label}`)
    await new Promise((r) => setTimeout(r, 25))
  }
}

const t0 = Date.now()
const clients = await Promise.all(
  Array.from({ length: EDITORS }, async (_, i) => {
    const doc = new Y.Doc()
    const socket = new HocuspocusProviderWebsocket({ url: `ws://127.0.0.1:${port}`, WebSocketPolyfill: WebSocket })
    const provider = new HocuspocusProvider({ websocketProvider: socket, name: NAME, document: doc, token: await api.ticket({ userId: `user-${i}`, tenantId: TENANT, documentName: NAME }) })
    provider.attach?.()
    return { i, doc, socket, provider }
  }),
)
await wait(() => clients.every((c) => c.provider.isSynced), 30000, 'all synced')
const connectedMs = Date.now() - t0

const t1 = Date.now()
// Interleaved bursts: every editor adds one paragraph per round, all concurrently.
for (let round = 0; round < EDITS; round++) {
  for (const c of clients) {
    const p = new Y.XmlElement('paragraph')
    p.insert(0, [new Y.XmlText(`e${c.i}-${round}`)])
    c.doc.getXmlFragment('default').push([p])
  }
  await new Promise((r) => setTimeout(r, 10))
}
const expected = EDITORS * EDITS + 1 // + the seeded paragraph
const count = (d: Y.Doc) => d.getXmlFragment('default').length
await wait(() => clients.every((c) => count(c.doc) === expected), 60000, 'convergence')
const convergedMs = Date.now() - t1
const reference = clients[0]!.doc.getXmlFragment('default').toString()
const identical = clients.every((c) => c.doc.getXmlFragment('default').toString() === reference)

// Persist and verify the stored state contains every edit.
const body = new TextEncoder().encode(JSON.stringify({ documentName: NAME }))
await fetch(`http://127.0.0.1:${port}/internal/flush`, { method: 'POST', body, headers: { 'x-raavi-signature': sign(KEY, 'POST', '/internal/flush', body) } })
const stored = api.docs.get(NAME)!
const replay = new Y.Doc()
if (stored.snapshot) Y.applyUpdate(replay, Buffer.from(stored.snapshot, 'base64'))
for (const u of stored.updates.filter((x) => x.seq > stored.snapshotSeq)) Y.applyUpdate(replay, Buffer.from(u.update, 'base64'))
const storedOk = count(replay) === expected && replay.getXmlFragment('default').toString() === reference

for (const c of clients) {
  c.provider.destroy()
  c.socket.destroy()
}
await collab.destroy()
await api.close()

const result = { editors: EDITORS, editsPerEditor: EDITS, totalEdits: EDITORS * EDITS, connectedMs, convergedMs, identical, storedOk, storedUpdates: stored.lastSeq, snapshotSeq: stored.snapshotSeq }
console.log(JSON.stringify(result, null, 2))
if (!identical || !storedOk) process.exit(1)
process.exit(0)
