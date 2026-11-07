import { sign, SIGNATURE_HEADER } from './signature.ts'

/** Client for the API's internal collaboration endpoints — the server's only path to persistent state. */
export interface DocumentState {
  snapshot: string | null
  updates: string[]
  seq: number
  seed: { schema: string; schemaVersion: number; doc: Record<string, unknown> } | null
}

export class ApiError extends Error {
  readonly status: number

  constructor(status: number, message: string) {
    super(message)
    this.status = status
  }
}

export class InternalApi {
  private readonly baseUrl: string
  private readonly serviceKey: string
  private readonly fetchImpl: typeof fetch

  constructor(baseUrl: string, serviceKey: string, fetchImpl: typeof fetch = fetch) {
    this.baseUrl = baseUrl
    this.serviceKey = serviceKey
    this.fetchImpl = fetchImpl
  }

  private async call<T>(method: 'GET' | 'POST', path: string, body?: unknown): Promise<T> {
    const bytes = body === undefined ? new Uint8Array() : new TextEncoder().encode(JSON.stringify(body))
    const response = await this.fetchImpl(new URL(path, this.baseUrl), {
      method,
      headers: {
        [SIGNATURE_HEADER]: sign(this.serviceKey, method, path, bytes),
        ...(body === undefined ? {} : { 'Content-Type': 'application/json' }),
      },
      ...(body === undefined ? {} : { body: bytes }),
    })
    if (!response.ok) throw new ApiError(response.status, `${method} ${path} → ${response.status}`)
    const text = await response.text()
    return (text ? JSON.parse(text) : undefined) as T
  }

  private doc(name: string) {
    // Names are validated addresses (hex, dashes, colons): sent unencoded so both sides sign the same path.
    return `/internal/collaboration/documents/${name}`
  }

  jwks(): Promise<{ keys: JsonWebKey[] }> {
    return this.call('GET', '/internal/collaboration/jwks')
  }

  state(name: string): Promise<DocumentState> {
    return this.call('GET', `${this.doc(name)}/state`)
  }

  storeUpdate(name: string, update: Uint8Array, json: unknown, contributors: string[]): Promise<{ seq: number }> {
    return this.call('POST', `${this.doc(name)}/updates`, { update: Buffer.from(update).toString('base64'), json, contributors })
  }

  storeSnapshot(name: string, state: Uint8Array, seq: number): Promise<void> {
    return this.call('POST', `${this.doc(name)}/snapshot`, { state: Buffer.from(state).toString('base64'), seq })
  }

  check(documentName: string, userId: string, sessionId: string): Promise<{ permission: 'edit' | 'view' | 'none' }> {
    return this.call('POST', '/internal/collaboration/check', { documentName, userId, sessionId })
  }
}
