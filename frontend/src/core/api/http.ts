/**
 * Same-origin JSON client for the Raavi API. Cookies are host-only and HttpOnly; the client never sees the session.
 * State-changing requests carry the antiforgery token (X-XSRF-TOKEN), fetched lazily and refreshed after every
 * sign-in/out because tokens are bound to the authenticated identity. The client never sends a tenant identifier.
 */
import { apiUrl } from '@/core/runtime/base'

export class ApiError extends Error {
  constructor(
    readonly status: number,
    readonly code: string,
    message: string,
  ) {
    super(message)
    this.name = 'ApiError'
  }
}

type FetchLike = typeof fetch

let fetchImpl: FetchLike = (...args) => fetch(...args)
let xsrfToken: string | null = null
let xsrfPromise: Promise<string> | null = null

/** For tests. */
export function configureHttp(f: FetchLike): void {
  fetchImpl = f
  resetAntiforgery()
}

export function resetAntiforgery(): void {
  xsrfToken = null
  xsrfPromise = null
}

async function antiforgeryToken(): Promise<string> {
  if (xsrfToken) return xsrfToken
  xsrfPromise ??= (async () => {
    const r = await fetchImpl(apiUrl('/auth/antiforgery'), { credentials: 'same-origin', headers: { Accept: 'application/json' } })
    if (!r.ok) throw new ApiError(r.status, 'csrf.unavailable', 'Could not obtain an anti-forgery token')
    const body = (await r.json()) as { token: string }
    xsrfToken = body.token
    return body.token
  })()
  try {
    return await xsrfPromise
  } finally {
    xsrfPromise = null
  }
}

async function toError(r: Response): Promise<ApiError> {
  try {
    const p = (await r.json()) as { code?: string; title?: string }
    return new ApiError(r.status, p.code ?? `http.${r.status}`, p.title ?? r.statusText)
  } catch {
    return new ApiError(r.status, `http.${r.status}`, r.statusText)
  }
}

export type HttpMethod = 'GET' | 'POST' | 'PUT' | 'DELETE'

export interface ApiResponse<T> {
  data: T
  /** Strong ETag of versioned resources (send it back as If-Match on updates). */
  etag: string | null
}

export async function api<T>(method: HttpMethod, path: string, body?: unknown, retry = true): Promise<T> {
  return (await apiRequest<T>(method, path, body, {}, retry)).data
}

/** Like {@link api}, plus the response ETag and optional If-Match (optimistic concurrency, API_CONVENTIONS.md §3). */
export async function apiRequest<T>(method: HttpMethod, path: string, body?: unknown, options: { ifMatch?: string | null } = {}, retry = true): Promise<ApiResponse<T>> {
  const headers: Record<string, string> = { Accept: 'application/json' }
  if (body !== undefined) headers['Content-Type'] = 'application/json'
  if (method !== 'GET') headers['X-XSRF-TOKEN'] = await antiforgeryToken()
  if (options.ifMatch) headers['If-Match'] = options.ifMatch

  const r = await fetchImpl(apiUrl(path), {
    method,
    headers,
    credentials: 'same-origin',
    body: body === undefined ? undefined : JSON.stringify(body),
  })

  if (r.ok) {
    const etag = r.headers?.get?.('ETag') ?? null
    if (r.status === 204) return { data: undefined as T, etag }
    const text = await r.text()
    return { data: (text ? JSON.parse(text) : undefined) as T, etag }
  }

  const error = await toError(r)
  // A stale token (identity changed in another tab) is retried once with a fresh token.
  if (error.code === 'csrf.invalid' && retry) {
    resetAntiforgery()
    return apiRequest<T>(method, path, body, options, false)
  }
  throw error
}

/** Multipart upload (attachments). Same antiforgery and error handling as {@link api}. */
export async function apiUpload<T>(path: string, form: FormData, method: 'POST' | 'PUT' = 'POST'): Promise<T> {
  const r = await fetchImpl(apiUrl(path), {
    method,
    headers: { Accept: 'application/json', 'X-XSRF-TOKEN': await antiforgeryToken() },
    credentials: 'same-origin',
    body: form,
  })
  if (!r.ok) throw await toError(r)
  return (await r.json()) as T
}
