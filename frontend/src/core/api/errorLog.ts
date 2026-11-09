import { api } from './http'

export interface ErrorItem {
  id: string
  occurredAt: string
  source: 'server' | 'client'
  tenantCode: string | null
  userId: string | null
  method: string | null
  path: string | null
  statusCode: number | null
  exceptionType: string | null
  message: string
  correlationId: string | null
  fingerprint: string
}

export interface ErrorDetail {
  item: ErrorItem
  stackTrace: string | null
  userAgent: string | null
  sameCount: number
}

export interface ErrorSummary {
  total: number
  server: number
  client: number
  days: { date: string; server: number; client: number }[]
  groups: { fingerprint: string; count: number; lastSeen: string; message: string; exceptionType: string | null; path: string | null; source: string }[]
}

export interface ErrorQuery {
  q?: string
  source?: string
  from?: string
  to?: string
  fingerprint?: string
  skip?: number
  take?: number
}

const qs = (query: Record<string, string | number | undefined>) => {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(query)) if (v !== undefined && v !== '') p.set(k, String(v))
  const s = p.toString()
  return s ? `?${s}` : ''
}

export const errorLogApi = {
  list: (query: ErrorQuery) => api<{ total: number; items: ErrorItem[] }>('GET', `/system/errors${qs({ ...query })}`),
  summary: (days = 14) => api<ErrorSummary>('GET', `/system/errors/summary?days=${days}`),
  get: (id: string) => api<ErrorDetail>('GET', `/system/errors/${id}`),
  purge: (olderThanDays: number) => api<{ deleted: number }>('POST', '/system/errors/purge', { olderThanDays }),
}
