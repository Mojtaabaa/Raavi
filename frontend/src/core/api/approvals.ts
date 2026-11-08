import { api } from './http'

export type ApprovalMode = 'All' | 'Any'
export type ApprovalStatus = 'Pending' | 'Approved' | 'Rejected' | 'Cancelled'
export type ApproverDecision = 'Pending' | 'Approved' | 'Rejected'

export interface Approver {
  userId: string
  name: string | null
  decision: ApproverDecision
  comment: string | null
  decidedAt: string | null
}

export interface Approval {
  id: string
  contentId: string
  contentTitle: string
  spaceKey: string
  note: string
  mode: ApprovalMode
  status: ApprovalStatus
  versionNumber: number
  currentVersionNumber: number
  pageChanged: boolean
  requestedBy: string
  requestedByName: string | null
  requestedAt: string
  dueAt: string | null
  overdue: boolean
  decidedAt: string | null
  approvers: Approver[]
  canDecide: boolean
  canCancel: boolean
}

export interface ApprovalRequestInput {
  note: string
  mode: ApprovalMode
  approverIds: string[]
  dueAt: string | null
}

const enc = encodeURIComponent

export const approvalsApi = {
  forPage: (contentId: string) => api<Approval[]>('GET', `/content/${enc(contentId)}/approvals`),
  request: (contentId: string, body: ApprovalRequestInput) => api<Approval>('POST', `/content/${enc(contentId)}/approvals`, body),
  list: (scope: 'inbox' | 'requested') => api<Approval[]>('GET', `/approvals?scope=${scope}`),
  decide: (id: string, decision: 'Approved' | 'Rejected', comment: string) => api<Approval>('POST', `/approvals/${enc(id)}/decision`, { decision, comment }),
  cancel: (id: string) => api<Approval>('POST', `/approvals/${enc(id)}/cancel`, {}),
}
