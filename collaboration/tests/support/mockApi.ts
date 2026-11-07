import { createServer, type Server } from 'node:http'
import type { AddressInfo } from 'node:net'
import { randomUUID } from 'node:crypto'
import { SignJWT, exportJWK, generateKeyPair } from 'jose'
import { verify } from '../../src/signature.ts'

/** In-process stand-in for the API's internal collaboration endpoints (signature-checked, in-memory state). */
export interface StoredDoc {
  snapshot: string | null
  snapshotSeq: number
  updates: { seq: number; update: string }[]
  lastSeq: number
  json: unknown
  contributors: Set<string>
  seed: unknown
}

export async function startMockApi(serviceKey: string) {
  const { publicKey, privateKey } = await generateKeyPair('ES256')
  const jwk = { ...(await exportJWK(publicKey)), kid: 'test-key', alg: 'ES256', use: 'sig' }
  const docs = new Map<string, StoredDoc>()
  const permissions = new Map<string, 'edit' | 'view' | 'none'>() // key: userId
  const calls: string[] = []
  let rejectNextStore = false

  const docOf = (name: string) => {
    let d = docs.get(name)
    if (!d) {
      d = { snapshot: null, snapshotSeq: 0, updates: [], lastSeq: 0, json: null, contributors: new Set(), seed: { schema: 'raavi.doc', schemaVersion: 2, doc: { type: 'doc', content: [{ type: 'paragraph', attrs: { textAlign: null, dir: null }, content: [{ type: 'text', text: 'seeded' }] }] } } }
      docs.set(name, d)
    }
    return d
  }

  const server: Server = createServer(async (req, res) => {
    const chunks: Buffer[] = []
    for await (const c of req) chunks.push(c as Buffer)
    const body = Buffer.concat(chunks)
    const url = new URL(req.url ?? '/', 'http://api')
    calls.push(`${req.method} ${url.pathname}`)
    if (!verify(serviceKey, req.headers['x-raavi-signature'] as string, req.method ?? '', url.pathname + url.search, body)) {
      res.writeHead(401).end()
      return
    }
    const json = (o: unknown) => res.writeHead(200, { 'Content-Type': 'application/json' }).end(JSON.stringify(o))
    const m = url.pathname.match(/^\/internal\/collaboration\/documents\/([^/]+)\/(state|updates|snapshot)$/)
    if (url.pathname === '/internal/collaboration/jwks') return json({ keys: [jwk] })
    if (url.pathname === '/internal/collaboration/check') {
      const { userId } = JSON.parse(body.toString()) as { userId: string }
      return json({ permission: permissions.get(userId) ?? 'edit' })
    }
    if (!m) return void res.writeHead(404).end()
    const name = decodeURIComponent(m[1]!)
    const doc = docOf(name)
    if (m[2] === 'state') {
      const hasState = doc.snapshot !== null || doc.updates.length > 0
      return json({ snapshot: doc.snapshot, updates: doc.updates.filter((u) => u.seq > doc.snapshotSeq).map((u) => u.update), seq: doc.lastSeq, seed: hasState ? null : doc.seed })
    }
    const payload = JSON.parse(body.toString()) as { update?: string; json?: unknown; contributors?: string[]; state?: string; seq?: number }
    if (m[2] === 'updates') {
      if (rejectNextStore) {
        rejectNextStore = false
        return void res.writeHead(400).end()
      }
      doc.lastSeq++
      doc.updates.push({ seq: doc.lastSeq, update: payload.update! })
      doc.json = payload.json
      for (const c of payload.contributors ?? []) doc.contributors.add(c)
      return json({ seq: doc.lastSeq })
    }
    doc.snapshot = payload.state!
    doc.snapshotSeq = payload.seq!
    doc.updates = doc.updates.filter((u) => u.seq > payload.seq!)
    res.writeHead(204).end()
  })
  await new Promise<void>((r) => server.listen(0, '127.0.0.1', r))
  const url = `http://127.0.0.1:${(server.address() as AddressInfo).port}`

  async function ticket(claims: { userId: string; tenantId: string; documentName: string; perm?: string; sessionId?: string }, options: { expiresIn?: string; audience?: string; jti?: string } = {}) {
    return new SignJWT({ tid: claims.tenantId, doc: claims.documentName, perm: claims.perm ?? 'edit', sid: claims.sessionId ?? randomUUID(), name: 'Tester' })
      .setProtectedHeader({ alg: 'ES256', kid: 'test-key' })
      .setSubject(claims.userId)
      .setIssuer('raavi-api')
      .setAudience(options.audience ?? 'raavi-collaboration')
      .setIssuedAt()
      .setExpirationTime(options.expiresIn ?? '60s')
      .setJti(options.jti ?? randomUUID())
      .sign(privateKey)
  }

  return {
    url,
    docs,
    calls,
    permissions,
    ticket,
    rejectNextStore: () => (rejectNextStore = true),
    close: () => new Promise<void>((r) => server.close(() => r())),
  }
}
