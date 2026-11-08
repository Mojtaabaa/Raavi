import { api, apiRequest } from './http'

export type WfField = 'Title' | 'ContentType' | 'Label'
export type WfOperator = 'Contains' | 'Equals' | 'StartsWith' | 'Has'
export type WfActionType = 'AddLabel' | 'Notify'
export type WfRecipients = 'Watchers' | 'Actor'

export interface WfCondition {
  field: WfField
  operator: WfOperator
  value: string
}

export interface WfAction {
  type: WfActionType
  label: string | null
  recipients: WfRecipients | null
  message: string | null
}

export interface Workflow {
  id: string
  spaceKey: string
  spaceName: string
  name: string
  enabled: boolean
  trigger: string
  /** Only for the `content.stale` trigger. */
  staleDays: number | null
  conditions: WfCondition[]
  actions: WfAction[]
  createdByName: string | null
  createdAt: string
  updatedAt: string
  revision: number
}

export interface WorkflowInput {
  name: string
  enabled: boolean
  trigger: string
  staleDays: number | null
  conditions: WfCondition[]
  actions: WfAction[]
}

export interface WorkflowRun {
  id: string
  eventId: string
  eventType: string
  contentId: string | null
  contentTitle: string | null
  status: 'Succeeded' | 'Skipped' | 'Failed'
  message: string
  attempts: number
  startedAt: string
  finishedAt: string
}

/** Operators that make sense for each field (mirrors the server rules). */
export const OPERATORS: Record<WfField, WfOperator[]> = {
  Title: ['Contains', 'Equals', 'StartsWith'],
  ContentType: ['Equals'],
  Label: ['Has'],
}

const enc = encodeURIComponent

export const workflowsApi = {
  catalog: () => api<{ triggers: string[] }>('GET', '/workflows/catalog'),
  list: (spaceKey: string) => api<Workflow[]>('GET', `/spaces/${enc(spaceKey)}/workflows`),
  create: (spaceKey: string, body: WorkflowInput) => api<Workflow>('POST', `/spaces/${enc(spaceKey)}/workflows`, body),
  update: (w: Workflow, body: WorkflowInput) => apiRequest<Workflow>('PUT', `/workflows/${enc(w.id)}`, body, { ifMatch: `"r${w.revision}"` }).then((r) => r.data),
  remove: (id: string) => api<void>('DELETE', `/workflows/${enc(id)}`),
  runs: (id: string, take = 50) => api<WorkflowRun[]>('GET', `/workflows/${enc(id)}/runs?take=${take}`),
}
