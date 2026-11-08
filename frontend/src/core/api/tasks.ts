import { api, apiRequest } from './http'

export interface Task {
  id: string
  spaceKey: string
  spaceName: string
  contentId: string | null
  title: string
  notes: string | null
  status: 'Open' | 'Done'
  assigneeId: string | null
  assigneeName: string | null
  /** yyyy-MM-dd (a calendar date, no time zone). */
  dueDate: string | null
  isOverdue: boolean
  completedAt: string | null
  createdAt: string
  createdByName: string | null
  revision: number
  rights: { canEdit: boolean; canComplete: boolean; canDelete: boolean }
}

export interface TaskInput {
  title: string
  notes: string | null
  assigneeId: string | null
  dueDate: string | null
  contentId: string | null
}

const enc = encodeURIComponent

export const tasksApi = {
  mine: (status?: 'Open' | 'Done') => api<Task[]>('GET', `/tasks/mine${status ? `?status=${status}` : ''}`),
  list: (spaceKey: string, status?: 'Open' | 'Done') => api<Task[]>('GET', `/spaces/${enc(spaceKey)}/tasks${status ? `?status=${status}` : ''}`),
  create: (spaceKey: string, body: TaskInput) => api<Task>('POST', `/spaces/${enc(spaceKey)}/tasks`, body),
  update: (task: Task, body: TaskInput) => apiRequest<Task>('PUT', `/tasks/${enc(task.id)}`, body, { ifMatch: `"r${task.revision}"` }).then((r) => r.data),
  complete: (id: string) => api<Task>('POST', `/tasks/${enc(id)}/complete`),
  reopen: (id: string) => api<Task>('POST', `/tasks/${enc(id)}/reopen`),
  remove: (id: string) => api<void>('DELETE', `/tasks/${enc(id)}`),
  exportUrl: (spaceKey: string) => `/api/v1/spaces/${enc(spaceKey)}/tasks/export`,
}
