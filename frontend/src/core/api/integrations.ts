import { api, apiRequest } from './http'

export interface Webhook {
  id: string
  name: string
  url: string
  events: string[]
  /** Only events of this space; null = the whole workspace. */
  spaceId: string | null
  enabled: boolean
  consecutiveFailures: number
  disabledReason: string | null
  lastSuccessAt: string | null
  createdAt: string
  revision: number
}

/** Returned once, when a webhook is created or its secret rotated. */
export interface WebhookWithSecret {
  webhook: Webhook
  secret: string
}

export interface WebhookInput {
  name: string
  url: string
  events: string[]
  spaceId: string | null
  enabled?: boolean
}

export interface WebhookDelivery {
  id: string
  eventId: string
  eventType: string
  status: 'Succeeded' | 'Failed'
  httpStatus: number | null
  attempts: number
  durationMs: number
  error: string | null
  at: string
}

export interface WebhookTestResult {
  success: boolean
  httpStatus: number | null
  error: string | null
  durationMs: number
}

const enc = encodeURIComponent
const base = '/integrations/webhooks'

export const webhooksApi = {
  catalog: () => api<{ events: string[]; hostPolicyConfigured: boolean }>('GET', `${base}/catalog`),
  list: () => api<Webhook[]>('GET', base),
  create: (body: WebhookInput) => api<WebhookWithSecret>('POST', base, body),
  update: (w: Webhook, body: WebhookInput) => apiRequest<Webhook>('PUT', `${base}/${enc(w.id)}`, body, { ifMatch: `"r${w.revision}"` }).then((r) => r.data),
  rotateSecret: (id: string) => api<WebhookWithSecret>('POST', `${base}/${enc(id)}/rotate-secret`, {}),
  test: (id: string) => api<WebhookTestResult>('POST', `${base}/${enc(id)}/test`, {}),
  deliveries: (id: string) => api<WebhookDelivery[]>('GET', `${base}/${enc(id)}/deliveries?take=50`),
  remove: (id: string) => api<void>('DELETE', `${base}/${enc(id)}`),
}
