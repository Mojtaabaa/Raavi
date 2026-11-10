import { api, apiUpload } from './http'
import { prefixed } from '@/core/runtime/base'

/** Operations helpers (ADR-051): worker status, accounts without a workspace, moving a single space. */

export interface WorkerServer {
  name: string
  startedAt: string | null
  lastHeartbeat: string
  workers: number
  alive: boolean
}

export interface WorkerReport {
  state: 'Running' | 'Stopped' | 'NeverStarted' | 'Unknown'
  checkedAt: string
  lastHeartbeat: string | null
  servers: WorkerServer[]
  jobs: Record<string, number>
}

export interface SystemUser {
  id: string
  displayName: string
  email: string | null
  status: string
  workspaces: number
  isSystemAdministrator: boolean
  createdAt: string
  lastLoginAt: string | null
}

export interface SystemUsersPage {
  items: SystemUser[]
  total: number
  skip: number
  take: number
  withoutWorkspace: number
}

export interface SpaceImportResult {
  key: string
  name: string
  pages: number
  attachments: number
  comments: number
  warnings: string[]
  notImported: Record<string, number>
}

export const opsApi = {
  workers: (system: boolean) => api<WorkerReport>('GET', system ? '/system/workers' : '/tenant/workers'),
  users: (q: string, withoutWorkspace: boolean, take = 200) =>
    api<SystemUsersPage>('GET', `/system/users?take=${take}&withoutWorkspace=${withoutWorkspace}${q ? `&q=${encodeURIComponent(q)}` : ''}`),
  deleteUsers: (body: { ids?: string[]; allWithoutWorkspace?: boolean }) => api<{ deleted: number; skipped: number }>('POST', '/system/users/delete', body),
  importSpace: (file: File, key: string, name: string) => {
    const form = new FormData()
    form.append('file', file)
    if (key.trim()) form.append('key', key.trim())
    if (name.trim()) form.append('name', name.trim())
    return apiUpload<SpaceImportResult>('/spaces/import', form)
  },
}

/** Downloads a file produced by the API (export archives). Throws the HTTP status as a number when the server refuses. */
export async function downloadFrom(path: string, fallbackName: string): Promise<void> {
  const response = await fetch(prefixed(`/api/v1${path}`), { credentials: 'same-origin' })
  if (!response.ok) throw response.status
  const blob = await response.blob()
  const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(response.headers.get('Content-Disposition') ?? '')?.[1] ?? fallbackName
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = decodeURIComponent(name)
  a.click()
  setTimeout(() => URL.revokeObjectURL(url), 10_000)
}
