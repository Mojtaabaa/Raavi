/** Mirrors the backend HostContextDto (`GET /api/v1/host/context`). Contains no internal ids. */
export interface TenantBranding {
  primaryColor: string | null
  secondaryColor: string | null
  welcomeMessageFa: string | null
  welcomeMessageEn: string | null
}

export interface TenantPublicContext {
  slug: string
  displayName: { fa: string; en: string }
  defaultLocale: string
  timeZone: string
  calendar: 'persian' | 'gregorian'
  branding: TenantBranding
}

export type HostContext =
  | { kind: 'tenant'; tenant: TenantPublicContext }
  | { kind: 'system' }
  | { kind: 'not-found' }
  | { kind: 'unavailable' }

/**
 * Asks the API which host the SPA is running on. The tenant is always determined by the server from the host name;
 * the SPA never sends a tenant identifier (ADR-004).
 */
export async function fetchHostContext(fetchImpl: typeof fetch = fetch, signal?: AbortSignal): Promise<HostContext> {
  try {
    const response = await fetchImpl('/api/v1/host/context', { signal, headers: { Accept: 'application/json' } })
    if (response.status === 404) return { kind: 'not-found' }
    if (!response.ok) return { kind: 'unavailable' }
    const body = (await response.json()) as { kind: string; tenant: TenantPublicContext | null }
    if (body.kind === 'tenant' && body.tenant) return { kind: 'tenant', tenant: body.tenant }
    if (body.kind === 'system') return { kind: 'system' }
    return { kind: 'unavailable' }
  } catch {
    return { kind: 'unavailable' }
  }
}
