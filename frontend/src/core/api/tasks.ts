import { api, apiRequest, apiUpload } from './http'
import { prefixed } from '@/core/runtime/base'
import type { RaaviDoc } from './wiki'

export type TaskPriority = 'Lowest' | 'Low' | 'Normal' | 'High' | 'Highest'

export type TaskAssigneeRole = 'Responsible' | 'Contributor' | 'Reviewer' | 'Approver' | 'Observer'

export interface TaskAssignee {
  userId: string
  name: string | null
  role: TaskAssigneeRole | string
  isPrimary: boolean
  assignedAt?: string | null
  removedAt?: string | null
  assignedBy?: string | null
  assignedByName?: string | null
}

export interface TaskChecklistItem {
  id: string
  title: string
  isDone: boolean
  isRequired: boolean
  sortOrder: number
  doneAt: string | null
  doneBy: string | null
  doneByName: string | null
}

export interface TaskChecklist {
  id: string
  title: string
  sortOrder: number
  items: TaskChecklistItem[]
}

export interface TaskComment {
  id: string
  threadId: string
  parentId: string | null
  body: RaaviDoc
  isDeleted: boolean
  authorId: string
  authorName: string | null
  createdAt: string
  editedAt: string | null
  revision: number
  canEdit: boolean
  canDelete: boolean
}

export interface TaskCommentsPage {
  items: TaskComment[]
  total: number
  canComment: boolean
  watching: boolean
  sort: string
}

export interface TaskAttachment {
  id: string
  taskId: string
  commentId: string | null
  fileName: string
  mediaType: string
  size: number
  isImage: boolean
  createdAt: string
  createdBy: string
  createdByName: string | null
}

export interface TaskWatcher {
  userId: string
  name: string | null
  state: string
  updatedAt: string
}

export interface TaskWatchers {
  items: TaskWatcher[]
  watching: boolean
  canManage: boolean
}

export interface TaskActivity {
  id: string
  kind: string
  actorId: string
  actorName: string | null
  title: string
  occurredAt: string
}

export interface TaskTimeEntry {
  id: string
  taskId: string
  userId: string
  userName: string | null
  workDate: string
  durationMinutes: number
  description: string | null
  isOpeningBalance: boolean
  createdAt: string
  updatedAt: string
  revision: number
  canEdit: boolean
  canDelete: boolean
}

export interface TaskTimeSummary {
  totalMinutes: number
  entryMinutes: number
  openingBalanceMinutes: number
  actualSpentMinutes: number | null
  originalEstimateMinutes: number | null
  remainingEstimateMinutes: number | null
  /** False when cached ActualSpentMinutes diverges from the sum of active time entries. */
  actualSpentMatchesEntries: boolean
  byUser: { userId: string; userName: string | null; durationMinutes: number }[]
  items: TaskTimeEntry[]
  canLog: boolean
  canManageOthers: boolean
}

export interface Task {
  id: string
  spaceKey: string
  spaceName: string
  contentId: string | null
  title: string
  notes: string | null
  status: 'Open' | 'Done'
  priority: TaskPriority
  assigneeId: string | null
  assigneeName: string | null
  assignees: TaskAssignee[]
  /** yyyy-MM-dd (a calendar date, no time zone). */
  dueDate: string | null
  startDate: string | null
  parentTaskId: string | null
  originalEstimateMinutes: number | null
  remainingEstimateMinutes: number | null
  actualSpentMinutes: number | null
  progressPercent: number | null
  blockCompleteWhenOpenChildren: boolean
  blockCompleteWhenMandatoryIncomplete: boolean
  childCount: number
  openChildCount: number
  checklistProgressPercent: number | null
  isOverdue: boolean
  completedAt: string | null
  archivedAt: string | null
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
  startDate?: string | null
  contentId: string | null
  priority?: TaskPriority
  parentTaskId?: string | null
  progressPercent?: number | null
  originalEstimateMinutes?: number | null
  remainingEstimateMinutes?: number | null
  blockCompleteWhenOpenChildren?: boolean | null
  blockCompleteWhenMandatoryIncomplete?: boolean | null
  assignees?: { userId: string; role: string; isPrimary: boolean }[] | null
}

const enc = encodeURIComponent

export const tasksApi = {
  mine: (status?: 'Open' | 'Done') => api<Task[]>('GET', `/tasks/mine${status ? `?status=${status}` : ''}`),
  assignees: (id: string, history = false) => api<TaskAssignee[]>('GET', `/tasks/${enc(id)}/assignees${history ? '?history=true' : ''}`),
  children: (id: string) => api<Task[]>('GET', `/tasks/${enc(id)}/children`),
  promote: (id: string) => api<Task>('POST', `/tasks/${enc(id)}/promote`),
  checklists: (id: string) => api<TaskChecklist[]>('GET', `/tasks/${enc(id)}/checklists`),
  createChecklist: (taskId: string, title: string) => api<TaskChecklist>('POST', `/tasks/${enc(taskId)}/checklists`, { title }),
  deleteChecklist: (id: string) => api<void>('DELETE', `/task-checklists/${enc(id)}`),
  addChecklistItem: (checklistId: string, body: { title: string; isRequired?: boolean }) =>
    api<TaskChecklistItem>('POST', `/task-checklists/${enc(checklistId)}/items`, body),
  updateChecklistItem: (id: string, body: { title?: string; isRequired?: boolean; isDone?: boolean; sortOrder?: number }) =>
    api<TaskChecklistItem>('PUT', `/task-checklist-items/${enc(id)}`, body),
  deleteChecklistItem: (id: string) => api<void>('DELETE', `/task-checklist-items/${enc(id)}`),
  reorderChecklistItems: (checklistId: string, orderedIds: string[]) =>
    api<void>('PUT', `/task-checklists/${enc(checklistId)}/reorder`, orderedIds),
  comments: (id: string, opts?: { skip?: number; take?: number; sort?: 'asc' | 'desc' }) => {
    const q = new URLSearchParams()
    if (opts?.skip != null) q.set('skip', String(opts.skip))
    if (opts?.take != null) q.set('take', String(opts.take))
    if (opts?.sort) q.set('sort', opts.sort)
    const qs = q.toString()
    return api<TaskCommentsPage>(`GET`, `/tasks/${enc(id)}/comments${qs ? `?${qs}` : ''}`)
  },
  createComment: (taskId: string, body: RaaviDoc, parentId?: string) =>
    api<TaskComment>('POST', `/tasks/${enc(taskId)}/comments${parentId ? `?parentId=${enc(parentId)}` : ''}`, { body }),
  editComment: (comment: TaskComment, body: RaaviDoc) =>
    apiRequest<TaskComment>('PUT', `/task-comments/${enc(comment.id)}`, { body }, { ifMatch: `"r${comment.revision}"` }).then((r) => r.data),
  deleteComment: (id: string) => api<void>('DELETE', `/task-comments/${enc(id)}`),
  attachments: (id: string) => api<TaskAttachment[]>('GET', `/tasks/${enc(id)}/attachments`),
  uploadAttachment: (taskId: string, file: File, commentId?: string) => {
    const q = commentId ? `?commentId=${enc(commentId)}` : ''
    const form = new FormData()
    form.append('file', file)
    return apiUpload<TaskAttachment>(`/tasks/${enc(taskId)}/attachments${q}`, form)
  },
  attachmentUrl: (id: string) => prefixed(`/api/v1/task-attachments/${enc(id)}`),
  deleteAttachment: (id: string) => api<void>('DELETE', `/task-attachments/${enc(id)}`),
  watchers: (id: string) => api<TaskWatchers>('GET', `/tasks/${enc(id)}/watchers`),
  setWatch: (id: string, watching: boolean) => api<TaskWatchers>('PUT', `/tasks/${enc(id)}/watch`, { watching }),
  addWatcher: (id: string, userId: string) => api<TaskWatchers>('POST', `/tasks/${enc(id)}/watchers`, { userId }),
  activity: (id: string, limit?: number) =>
    api<TaskActivity[]>('GET', `/tasks/${enc(id)}/activity${limit != null ? `?limit=${limit}` : ''}`),
  time: (id: string, opts?: { userId?: string; from?: string; to?: string }) => {
    const q = new URLSearchParams()
    if (opts?.userId) q.set('userId', opts.userId)
    if (opts?.from) q.set('from', opts.from)
    if (opts?.to) q.set('to', opts.to)
    const qs = q.toString()
    return api<TaskTimeSummary>('GET', `/tasks/${enc(id)}/time${qs ? `?${qs}` : ''}`)
  },
  logTime: (taskId: string, body: { userId?: string | null; workDate: string; durationMinutes: number; description?: string | null }) =>
    api<TaskTimeEntry>('POST', `/tasks/${enc(taskId)}/time`, body),
  updateTime: (entry: TaskTimeEntry, body: { workDate?: string; durationMinutes?: number; description?: string | null }) =>
    apiRequest<TaskTimeEntry>('PUT', `/task-time/${enc(entry.id)}`, body, { ifMatch: `"r${entry.revision}"` }).then((r) => r.data),
  deleteTime: (id: string) => api<void>('DELETE', `/task-time/${enc(id)}`),
  list: (spaceKey: string, status?: 'Open' | 'Done', opts?: { archived?: boolean }) => {
    const q = new URLSearchParams()
    if (status) q.set('status', status)
    if (opts?.archived) q.set('archived', 'true')
    const qs = q.toString()
    return api<Task[]>('GET', `/spaces/${enc(spaceKey)}/tasks${qs ? `?${qs}` : ''}`)
  },
  create: (spaceKey: string, body: TaskInput) => api<Task>('POST', `/spaces/${enc(spaceKey)}/tasks`, body),
  update: (task: Task, body: TaskInput) => apiRequest<Task>('PUT', `/tasks/${enc(task.id)}`, body, { ifMatch: `"r${task.revision}"` }).then((r) => r.data),
  complete: (id: string) => api<Task>('POST', `/tasks/${enc(id)}/complete`),
  reopen: (id: string) => api<Task>('POST', `/tasks/${enc(id)}/reopen`),
  archive: (id: string) => api<Task>('POST', `/tasks/${enc(id)}/archive`),
  restore: (id: string) => api<Task>('POST', `/tasks/${enc(id)}/restore`),
  remove: (id: string) => api<void>('DELETE', `/tasks/${enc(id)}`),
  exportUrl: (spaceKey: string) => prefixed(`/api/v1/spaces/${enc(spaceKey)}/tasks/export`),
}
