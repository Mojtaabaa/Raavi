import { api } from './http'

export interface ActivityItem {
  id: string
  kind: string
  spaceId: string
  spaceKey: string
  contentId: string | null
  title: string
  version: number | null
  actorId: string
  actorName: string | null
  occurredAt: string
}

export interface ActivityStats {
  total: number
  days: { date: string; byKind: Record<string, number> }[]
  byKind: Record<string, number>
  topActors: { actorId: string; name: string | null; count: number }[]
  byHour: number[]
}

export interface SecurityRow {
  occurredAt: string
  eventType: string
  userId: string | null
  userName: string | null
  actorId: string
  actorName: string | null
  ipAddress: string | null
  details: string | null
}

export interface LogQuery {
  q?: string
  kind?: string
  space?: string
  from?: string
  to?: string
  skip?: number
  take?: number
}

function qs(query: Record<string, string | number | undefined>): string {
  const p = new URLSearchParams()
  for (const [k, v] of Object.entries(query)) if (v !== undefined && v !== '') p.set(k, String(v))
  const s = p.toString()
  return s ? `?${s}` : ''
}

export const activityApi = {
  stats: (days = 30) => api<ActivityStats>('GET', `/activity/stats?days=${days}`),
  log: (query: LogQuery) => api<{ total: number; items: ActivityItem[]; kinds: string[] }>('GET', `/tenant/activity-log${qs({ ...query })}`),
  security: (query: Omit<LogQuery, 'kind' | 'space'> & { type?: string }) => api<{ total: number; items: SecurityRow[]; types: string[] }>('GET', `/tenant/security-events/search${qs({ ...query })}`),
}
