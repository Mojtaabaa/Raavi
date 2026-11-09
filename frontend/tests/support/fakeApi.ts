import { vi } from 'vitest'

export type Handler = (init: RequestInit & { url: string }) => Response | Promise<Response>

export function json(body: unknown, status = 200): Response {
  return new Response(body === undefined ? null : JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

export function problem(status: number, code: string): Response {
  return json({ status, code, title: code }, status)
}

/** Minimal routing fake for fetch: "METHOD /path" → handler. Records every call. */
export function fakeFetch(routes: Record<string, Handler>) {
  const calls: { method: string; url: string; headers: Record<string, string>; body: unknown }[] = []
  const fn = vi.fn(async (input: RequestInfo | URL, init: RequestInit = {}) => {
    const url = String(input)
    const method = (init.method ?? 'GET').toUpperCase()
    const headers = (init.headers ?? {}) as Record<string, string>
    calls.push({ method, url, headers, body: init.body instanceof FormData ? init.body : init.body ? JSON.parse(String(init.body)) : undefined })
    const path = url.split('?')[0]!
    const handler = routes[`${method} ${path}`] ?? routes[`${method} ${url}`]
    if (!handler) return problem(404, 'test.no_route')
    return handler({ ...init, url })
  })
  return { fn: fn as unknown as typeof fetch, calls }
}
