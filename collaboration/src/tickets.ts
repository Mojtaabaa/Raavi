import { importJWK, jwtVerify, type JWTPayload } from 'jose'

type VerifyKey = CryptoKey | Uint8Array

/** Verified ticket claims (COLLABORATION_ARCHITECTURE.md §3). */
export interface Ticket {
  userId: string
  tenantId: string
  documentName: string
  permission: 'edit' | 'view'
  sessionId: string
  name: string
}

export class TicketError extends Error {}

/**
 * Verifies ES256 collaboration tickets issued by the API: signature (public key from the API's JWKS), issuer,
 * audience, lifetime (≤ 70 s incl. skew), single use (jti replay cache) and binding to the requested document.
 */
export class TicketVerifier {
  private keys = new Map<string, VerifyKey>()
  private readonly used = new Map<string, number>()
  private readonly loadJwks: () => Promise<{ keys: (JsonWebKey & { kid?: string })[] }>

  constructor(loadJwks: () => Promise<{ keys: (JsonWebKey & { kid?: string })[] }>) {
    this.loadJwks = loadJwks
  }

  private async key(kid: string | undefined): Promise<VerifyKey> {
    const known = kid ? this.keys.get(kid) : undefined
    if (known) return known
    // Unknown kid: the API may have rotated its key (or restarted with an ephemeral development key).
    const jwks = await this.loadJwks()
    this.keys = new Map()
    for (const jwk of jwks.keys) {
      if (jwk.kid) this.keys.set(jwk.kid, await importJWK(jwk as Parameters<typeof importJWK>[0], 'ES256'))
    }
    const key = kid ? this.keys.get(kid) : undefined
    if (!key) throw new TicketError('unknown signing key')
    return key
  }

  async verify(token: string, documentName: string, now = Date.now()): Promise<Ticket> {
    if (!token || token.length > 4096) throw new TicketError('missing ticket')
    const header = JSON.parse(Buffer.from(token.split('.')[0] ?? '', 'base64url').toString('utf8') || '{}') as { kid?: string; alg?: string }
    if (header.alg !== 'ES256') throw new TicketError('wrong algorithm')
    let payload: JWTPayload
    try {
      ;({ payload } = await jwtVerify(token, await this.key(header.kid), {
        issuer: 'raavi-api',
        audience: 'raavi-collaboration',
        algorithms: ['ES256'],
        maxTokenAge: '70s',
        clockTolerance: 5,
        currentDate: new Date(now),
      }))
    } catch (e) {
      if (e instanceof TicketError) throw e
      throw new TicketError('invalid ticket')
    }
    const jti = String(payload.jti ?? '')
    if (!jti) throw new TicketError('missing jti')
    this.purge(now)
    if (this.used.has(jti)) throw new TicketError('ticket already used')
    this.used.set(jti, (payload.exp ?? 0) * 1000 + 10_000)
    if (payload.doc !== documentName) throw new TicketError('ticket is for another document')
    const perm = payload.perm === 'view' ? 'view' : payload.perm === 'edit' ? 'edit' : null
    if (!perm || typeof payload.sub !== 'string' || typeof payload.tid !== 'string' || typeof payload.sid !== 'string') throw new TicketError('incomplete ticket')
    if (!documentName.startsWith(`${payload.tid}:`)) throw new TicketError('tenant mismatch')
    return { userId: payload.sub, tenantId: payload.tid, documentName, permission: perm, sessionId: payload.sid, name: String(payload.name ?? '') }
  }

  private purge(now: number) {
    for (const [jti, until] of this.used) if (until < now) this.used.delete(jti)
  }
}
