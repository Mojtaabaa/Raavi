import { createHash, createHmac, timingSafeEqual } from 'node:crypto'

/**
 * Service request signatures, identical to the API's `ServiceSignature`:
 * `X-Raavi-Signature: t=<unix>,s=hex(HMAC-SHA256(key, t \n METHOD \n path?query \n hex(sha256(body))))`, 60 s window.
 */
export const SIGNATURE_HEADER = 'x-raavi-signature'
const WINDOW_SECONDS = 60

function compute(key: string, t: string, method: string, pathAndQuery: string, body: Uint8Array): string {
  const bodyHash = createHash('sha256').update(body).digest('hex')
  return createHmac('sha256', key).update(`${t}\n${method.toUpperCase()}\n${pathAndQuery}\n${bodyHash}`).digest('hex')
}

export function sign(key: string, method: string, pathAndQuery: string, body: Uint8Array, now = Date.now()): string {
  const t = Math.floor(now / 1000).toString()
  return `t=${t},s=${compute(key, t, method, pathAndQuery, body)}`
}

export function verify(key: string, header: string | undefined, method: string, pathAndQuery: string, body: Uint8Array, now = Date.now()): boolean {
  if (!key || !header) return false
  const parts = Object.fromEntries(header.split(',').map((p) => p.split('=', 2) as [string, string]))
  const t = parts.t
  const s = parts.s
  if (!t || !s || !/^\d+$/.test(t)) return false
  if (Math.abs(now / 1000 - Number(t)) > WINDOW_SECONDS) return false
  const expected = Buffer.from(compute(key, t, method, pathAndQuery, body))
  const actual = Buffer.from(s)
  return expected.length === actual.length && timingSafeEqual(expected, actual)
}
