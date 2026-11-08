import { test } from 'node:test'
import assert from 'node:assert/strict'
import { readFileSync, readdirSync } from 'node:fs'
import { fileURLToPath } from 'node:url'
import { join } from 'node:path'
import * as Y from 'yjs'
import { prosemirrorJSONToYXmlFragment, yXmlFragmentToProsemirrorJSON } from '@tiptap/y-tiptap'
import { buildSchema, loadManifests } from '../src/schema.ts'
import { sign, verify } from '../src/signature.ts'
import { TicketVerifier } from '../src/tickets.ts'
import { startMockApi } from './support/mockApi.ts'

const root = fileURLToPath(new URL('../../schema/editor/', import.meta.url))
const schema = buildSchema(loadManifests(join(root, 'extensions')))

test('every shared fixture survives JSON → Yjs → JSON on the collaboration server', () => {
  const files = readdirSync(join(root, 'fixtures')).filter((f) => f.endsWith('.json'))
  assert.ok(files.length >= 36)
  for (const file of files) {
    const fixture = JSON.parse(readFileSync(join(root, 'fixtures', file), 'utf8')) as { doc: Record<string, unknown> }
    const ydoc = new Y.Doc()
    prosemirrorJSONToYXmlFragment(schema, fixture.doc, ydoc.getXmlFragment('default'))
    // Through an encoded update, as persisted.
    const copy = new Y.Doc()
    Y.applyUpdate(copy, Y.encodeStateAsUpdate(ydoc))
    const out = yXmlFragmentToProsemirrorJSON(copy.getXmlFragment('default'))
    assert.deepEqual(schema.nodeFromJSON(out).toJSON(), schema.nodeFromJSON(fixture.doc).toJSON(), file)
  }
})

test('service signatures match the API implementation (shared test vector)', () => {
  const header = sign('test-service-key-0123456789abcdef', 'POST', '/internal/flush', new TextEncoder().encode('{}'), 1_700_000_000_000)
  // Same vector is asserted by the .NET test CollaborationTests.Service_signature_matches_the_collaboration_server.
  assert.equal(header, 't=1700000000,s=be8cabd7a939efc74d5ccb9670e92610e961d8dc62429476453b37992af20911')
  assert.ok(verify('test-service-key-0123456789abcdef', header, 'POST', '/internal/flush', new TextEncoder().encode('{}'), 1_700_000_010_000))
  assert.ok(!verify('test-service-key-0123456789abcdef', header, 'POST', '/internal/flush', new TextEncoder().encode('{"x":1}'), 1_700_000_010_000), 'body is signed')
  assert.ok(!verify('test-service-key-0123456789abcdef', header, 'POST', '/internal/reset', new TextEncoder().encode('{}'), 1_700_000_010_000), 'path is signed')
  assert.ok(!verify('test-service-key-0123456789abcdef', header, 'POST', '/internal/flush', new TextEncoder().encode('{}'), 1_700_000_120_000), 'old signatures expire')
  assert.ok(!verify('other-key', header, 'POST', '/internal/flush', new TextEncoder().encode('{}'), 1_700_000_010_000))
})

test('tickets: valid once, bound to the document and tenant, audience and lifetime enforced', async () => {
  const api = await startMockApi('k'.repeat(32))
  try {
    const verifier = new TicketVerifier(async () => {
      const r = await fetch(`${api.url}/internal/collaboration/jwks`, { headers: { 'x-raavi-signature': sign('k'.repeat(32), 'GET', '/internal/collaboration/jwks', new Uint8Array()) } })
      return (await r.json()) as { keys: JsonWebKey[] }
    })
    const tenant = '11111111-1111-4111-8111-111111111111'
    const doc = `${tenant}:page:22222222-2222-4222-8222-222222222222:draft`
    const token = await api.ticket({ userId: 'u1', tenantId: tenant, documentName: doc })
    const ok = await verifier.verify(token, doc)
    assert.equal(ok.userId, 'u1')
    assert.equal(ok.permission, 'edit')
    await assert.rejects(verifier.verify(token, doc), /already used/)
    await assert.rejects(verifier.verify(await api.ticket({ userId: 'u1', tenantId: tenant, documentName: doc }), `${tenant}:page:33333333-3333-4333-8333-333333333333:draft`), /another document/)
    await assert.rejects(verifier.verify(await api.ticket({ userId: 'u1', tenantId: tenant, documentName: doc }, { audience: 'other' }), doc), /invalid ticket/)
    await assert.rejects(verifier.verify(await api.ticket({ userId: 'u1', tenantId: tenant, documentName: doc }, { expiresIn: '-10s' }), doc), /invalid ticket/)
    const otherTenant = '99999999-9999-4999-8999-999999999999'
    await assert.rejects(verifier.verify(await api.ticket({ userId: 'u1', tenantId: otherTenant, documentName: doc }), doc), /tenant mismatch/)
    await assert.rejects(verifier.verify('a.b.c', doc), /wrong algorithm|invalid ticket/)
  } finally {
    await api.close()
  }
})

test('the server decodes the whiteboard state produced by the frontend binding (shared fixture)', async () => {
  const { readFileSync } = await import('node:fs')
  const Y = await import('yjs')
  const { boardToJson } = await import('../src/board.ts')
  const fixture = JSON.parse(readFileSync(new URL('../../schema/board/live-board.fixture.json', import.meta.url), 'utf8')) as { update: string; elements: Record<string, unknown>[] }
  const doc = new Y.Doc()
  Y.applyUpdate(doc, Buffer.from(fixture.update, 'base64'))
  assert.deepEqual(boardToJson(doc), fixture.elements, 'server conversion and frontend binding agree on the structure')
  assert.deepEqual(boardToJson(doc).map((e) => e.id), ['r1', 'a1', 's1']) // order = stacking order
})
