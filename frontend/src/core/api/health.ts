export type ServiceStatus = 'checking' | 'healthy' | 'unavailable'

/** Reads the API readiness endpoint. Same-origin only; no credentials beyond the session cookie are sent. */
export async function fetchServiceStatus(fetchImpl: typeof fetch = fetch, signal?: AbortSignal): Promise<ServiceStatus> {
  try {
    const response = await fetchImpl('/health/ready', { signal, headers: { Accept: 'text/plain' } })
    return response.ok ? 'healthy' : 'unavailable'
  } catch {
    return 'unavailable'
  }
}
