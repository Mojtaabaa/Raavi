/**
 * Where the single-page app is mounted (ADR-045).
 *
 *  - `/t/<slug>/…`  a path-addressed workspace: the router lives under `/t/<slug>` and so does the API (`/t/<slug>/api/v1/…`).
 *  - `/system/…`    the administration area of an installation whose root belongs to a default workspace: routes stay `/system/…`,
 *                   the API is `/system/api/v1/…` (the server accepts that prefix on the system host in every installation).
 *  - anything else  host-addressed workspace or system host: no prefix, exactly as before.
 *
 * The prefix is only ever derived from the address bar; it never selects a tenant by itself — the server resolves the tenant from the same path
 * and host, and the SPA still never sends a tenant identifier.
 */
export interface AppBase {
  /** Base of the router history. */
  routerBase: string
  /** Prefix of every API/hub/attachment URL. */
  apiPrefix: string
}

const WORKSPACE = /^\/t\/([a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?)(?=\/|$)/i
const SYSTEM = /^\/system(?=\/|$)/

export function detectBase(pathname: string): AppBase {
  const workspace = WORKSPACE.exec(pathname)
  if (workspace) {
    const prefix = `/t/${workspace[1]!.toLowerCase()}`
    return { routerBase: prefix, apiPrefix: prefix }
  }
  if (SYSTEM.test(pathname)) return { routerBase: '', apiPrefix: '/system' }
  return { routerBase: '', apiPrefix: '' }
}

const current = (): AppBase => detectBase(typeof window === 'undefined' ? '/' : window.location.pathname)

/** Prefix for the page being shown (evaluated per call, so a hard navigation to another area is always right). */
export const apiPrefix = (): string => current().apiPrefix

/** `/api/v1` + path, with the area prefix. */
export const apiUrl = (path: string): string => `${apiPrefix()}/api/v1${path}`

/** An absolute server path (e.g. `/api/v1/attachments/x`, `/hubs/events`) with the area prefix. */
export const prefixed = (absolutePath: string): string => `${apiPrefix()}${absolutePath}`

export const routerBase = (): string => current().routerBase
