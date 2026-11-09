/** The answer of `GET /health/status` (see PlatformStatus.cs). */
export type Health = 'Healthy' | 'Degraded' | 'Unhealthy'

export interface StatusComponent {
  name: string
  status: Health
  durationMs: number
  description: string | null
  data?: Record<string, number | string> | null
}

export interface StatusPoint {
  at: string
  requests: number
  errors: number
  averageMs: number
}

export interface StatusReport {
  status: Health
  checkedAt: string
  durationMs: number
  version: string
  environment: string
  startedAt: string
  uptimeSeconds: number
  components: StatusComponent[]
  process: { memoryMb: number; threads: number; gcGen2Collections: number; cpuSeconds: number }
  traffic: { requests: number; serverErrors: number; clientErrors: number; errorRatePercent: number; averageMs: number; p95Ms: number; maxMs: number; series: StatusPoint[] }
}

/**
 * Why the page could not get an answer. Told apart because the cure differs:
 *  - `network`:   the browser could not reach the web server at all (offline, DNS, firewall, site stopped);
 *  - `gateway`:   the web server answered but cannot reach the API (502/503/504: the API pool/process is down or starting);
 *  - `notApi`:    something answered that is not the API (an HTML page: proxy rule missing, wrong site);
 *  - `disabled`:  the API runs but the public status is switched off (404);
 *  - `timeout`:   no answer within the time limit.
 */
export type Unreachable = 'network' | 'gateway' | 'notApi' | 'disabled' | 'timeout'

export type Probe = { ok: true; report: StatusReport; roundTripMs: number } | { ok: false; reason: Unreachable; httpStatus: number | null; roundTripMs: number }

export const STATUS_URL = '/health/status'

/** One request to the status endpoint, classified. Never throws. */
export async function probe(fetchImpl: typeof fetch = fetch, timeoutMs = 8000): Promise<Probe> {
  const started = performance.now()
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  const took = () => Math.round(performance.now() - started)
  try {
    const r = await fetchImpl(STATUS_URL, { cache: 'no-store', headers: { Accept: 'application/json' }, signal: controller.signal })
    if (r.status === 404) return { ok: false, reason: 'disabled', httpStatus: 404, roundTripMs: took() }
    if (r.status === 502 || r.status === 503 || r.status === 504) return { ok: false, reason: 'gateway', httpStatus: r.status, roundTripMs: took() }
    const type = r.headers.get('content-type') ?? ''
    if (!type.includes('json')) return { ok: false, reason: 'notApi', httpStatus: r.status, roundTripMs: took() }
    const report = (await r.json()) as StatusReport
    if (!report || typeof report.status !== 'string' || !Array.isArray(report.components)) return { ok: false, reason: 'notApi', httpStatus: r.status, roundTripMs: took() }
    return { ok: true, report, roundTripMs: took() }
  } catch (e) {
    const aborted = e instanceof DOMException && e.name === 'AbortError'
    return { ok: false, reason: aborted ? 'timeout' : 'network', httpStatus: null, roundTripMs: took() }
  } finally {
    clearTimeout(timer)
  }
}

/** Cheap liveness ping used by the connection banner (the real `/health/live` probe, no body). */
export async function ping(fetchImpl: typeof fetch = fetch, timeoutMs = 4000): Promise<boolean> {
  const controller = new AbortController()
  const timer = setTimeout(() => controller.abort(), timeoutMs)
  try {
    const r = await fetchImpl('/health/live', { cache: 'no-store', signal: controller.signal })
    return r.ok
  } catch {
    return false
  } finally {
    clearTimeout(timer)
  }
}

export function formatDuration(seconds: number, units: { d: string; h: string; m: string; s: string }): string {
  const d = Math.floor(seconds / 86400)
  const h = Math.floor((seconds % 86400) / 3600)
  const m = Math.floor((seconds % 3600) / 60)
  const s = Math.floor(seconds % 60)
  const parts = [d && `${d}${units.d}`, (d || h) && `${h}${units.h}`, (d || h || m) && `${m}${units.m}`, !d && !h && `${s}${units.s}`].filter(Boolean)
  return parts.slice(0, 3).join(' ')
}

/** Plain-text report people can paste into a ticket. */
export function reportText(p: Probe, at: Date): string {
  const lines = [`Raavi status ${at.toISOString()}`]
  if (!p.ok) {
    lines.push(`UNREACHABLE: ${p.reason}${p.httpStatus ? ` (HTTP ${p.httpStatus})` : ''}, after ${p.roundTripMs} ms`)
    return lines.join('\n')
  }
  const r = p.report
  lines.push(`Overall: ${r.status}  version ${r.version}  uptime ${r.uptimeSeconds}s  round trip ${p.roundTripMs} ms`)
  for (const c of r.components) lines.push(`- ${c.name}: ${c.status} (${c.durationMs} ms)${c.description ? ' ' + c.description : ''}`)
  lines.push(`Traffic (60 min): ${r.traffic.requests} requests, ${r.traffic.serverErrors} server errors, avg ${r.traffic.averageMs} ms, p95 ${r.traffic.p95Ms} ms`)
  lines.push(`Process: ${r.process.memoryMb} MB, ${r.process.threads} threads`)
  return lines.join('\n')
}
