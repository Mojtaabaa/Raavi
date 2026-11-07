import { api } from './http'

export type MigrationStatus = 'Created' | 'Queued' | 'Running' | 'Paused' | 'Cancelled' | 'Completed' | 'CompletedWithErrors' | 'Failed'

export interface MigrationJob {
  id: string
  sourceUrl: string
  sourceVersion: string | null
  mode: 'DryRun' | 'Execute'
  status: MigrationStatus
  control: 'Pause' | 'Cancel' | null
  createdAt: string
  startedAt: string | null
  finishedAt: string | null
  currentOperation: string | null
  spacesTotal: number
  spacesDone: number
  pagesTotal: number
  pagesDone: number
  pagesFailed: number
  versionsDone: number
  warnings: number
  hasReport: boolean
  canResume: boolean
}

export interface MigrationEvent {
  at: string
  level: 'Info' | 'Warning' | 'Error'
  code: string
  sourceRef: string | null
  message: string
}

export interface SpaceReport {
  sourceKey: string
  name: string
  raaviKey: string | null
  pages: number
  versions: number
  pagesImported: number
  versionsImported: number
  pagesFailed: number
  keyConflict: boolean
}

export interface MigrationReport {
  mode: string
  sourceVersion: string | null
  spaces: SpaceReport[]
  unsupportedMacros: Record<string, number>
  warningsByCode: Record<string, number>
  attachmentReferences: number
  pageLinks: number
  unmappedAuthors: string[]
  linksResolved: number
  linksUnresolved: number
  validation: { spaceKey: string; check: string; expected: number; actual: number }[]
  attachments: { found: number; bytes: number; imported: number; tooLarge: number; failed: number; maxBytes: number }
  access: { restrictedPages: number; restrictionsApplied: number; unmappedSubjects: string[]; spacePermissions: string }
  authorMappings: { sourceKey: string; userName: string | null; displayName: string | null; email: string | null; suggestedUser: string | null; applied: boolean }[]
}

export interface CreateMigration {
  url: string
  token: string
  dryRun: boolean
  spaces: string[] | null
  keyMap: Record<string, string> | null
  reconvert: boolean
  mapUsersByEmail: boolean
  userMap: Record<string, string> | null
}

const base = '/migrations/confluence'
const enc = encodeURIComponent

export const migrationApi = {
  list: () => api<MigrationJob[]>('GET', base),
  get: (id: string) => api<MigrationJob>('GET', `${base}/${enc(id)}`),
  create: (body: CreateMigration) => api<MigrationJob>('POST', base, body),
  start: (id: string, token?: string) => api<MigrationJob>('POST', `${base}/${enc(id)}/start`, token ? { token } : {}),
  pause: (id: string) => api<MigrationJob>('POST', `${base}/${enc(id)}/pause`),
  cancel: (id: string) => api<MigrationJob>('POST', `${base}/${enc(id)}/cancel`),
  events: (id: string, level?: string) => api<MigrationEvent[]>('GET', `${base}/${enc(id)}/events?take=200${level ? `&level=${enc(level)}` : ''}`),
  report: (id: string) => api<MigrationReport>('GET', `${base}/${enc(id)}/report`),
}

/** Statuses in which the job is (or is about to be) running on the worker. */
export const isActive = (s: MigrationStatus) => s === 'Queued' || s === 'Running'

/** Statuses in which the stored source token has been erased: starting again needs a new token. */
export const needsToken = (s: MigrationStatus) => s === 'Cancelled' || s === 'Failed' || s === 'Completed' || s === 'CompletedWithErrors'

/** Parses "OLD=NEW" lines (also used for "sourceUser=e-mail") into a key map; ignores blank lines. Returns null when empty. */
export function parseKeyMap(text: string): Record<string, string> | null {
  const map: Record<string, string> = {}
  for (const line of text.split('\n')) {
    const [from, to] = line.split('=').map((p) => p.trim())
    if (from && to) map[from] = to
  }
  return Object.keys(map).length ? map : null
}

export function parseSpaces(text: string): string[] | null {
  const list = text.split(/[\s,]+/).map((s) => s.trim()).filter(Boolean)
  return list.length ? list : null
}
