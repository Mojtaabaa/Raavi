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

export type ConnectorKind = 'GitHub' | 'GitLab' | 'Jira'

export interface Connector {
  id: string
  name: string
  kind: ConnectorKind
  baseUrl: string
  hasToken: boolean
  enabled: boolean
  createdAt: string
  revision: number
}

/** token: undefined/null keeps the stored one on update, '' removes it. */
export interface ConnectorInput {
  name: string
  kind: ConnectorKind
  baseUrl: string
  token?: string | null
  enabled?: boolean
}

export interface IssueInfo {
  reference: string
  title: string
  status: string
  url: string
  type: 'issue' | 'pull_request'
}

const enc = encodeURIComponent
const base = '/integrations/webhooks'
const connectorBase = '/integrations/connectors'

export interface JiraLinkContext {
  id: string
  exact: string
  prefix: string | null
  suffix: string | null
  createdAt: string
}

export interface PageJiraLink {
  id: string
  contentId: string
  connectorId: string
  connectorName: string
  issueId: string
  issueKey: string
  summary: string | null
  status: string | null
  issueType: string | null
  priority: string | null
  assignee: string | null
  projectKey: string | null
  issueUrl: string | null
  source: string
  syncState: string
  createdAt: string
  createdByName: string | null
  lastSyncedAt: string | null
  lastSyncError: string | null
  contexts: JiraLinkContext[]
}

export interface JiraProject {
  id: string
  key: string
  name: string
}

export interface JiraIssueType {
  id: string
  name: string
  subtask: boolean
}

export interface TextQuoteInput {
  exact: string
  prefix?: string | null
  suffix?: string | null
  anchorJson?: string | null
}

export const connectorsApi = {
  catalog: () => api<{ kinds: ConnectorKind[]; hostPolicyConfigured: boolean }>('GET', `${connectorBase}/catalog`),
  list: () => api<Connector[]>('GET', connectorBase),
  create: (body: ConnectorInput) => api<Connector>('POST', connectorBase, body),
  update: (c: Connector, body: ConnectorInput) => apiRequest<Connector>('PUT', `${connectorBase}/${enc(c.id)}`, body, { ifMatch: `"r${c.revision}"` }).then((r) => r.data),
  remove: (id: string) => api<void>('DELETE', `${connectorBase}/${enc(id)}`),
  /** Enabled connectors, names only (any member). */
  available: () => api<{ id: string; name: string; kind: ConnectorKind }[]>('GET', '/integrations/issues/connectors'),
  lookup: (connector: string, reference: string) => api<IssueInfo>('GET', `/integrations/issues/lookup?connector=${enc(connector)}&reference=${enc(reference)}`),
}

export const jiraLinksApi = {
  jiraConnectors: () => api<{ id: string; name: string }[]>('GET', '/integrations/jira/connectors'),
  projects: (connectorId: string) => api<JiraProject[]>('GET', `/integrations/jira/connectors/${enc(connectorId)}/projects`),
  issueTypes: (connectorId: string, project: string) =>
    api<JiraIssueType[]>('GET', `/integrations/jira/connectors/${enc(connectorId)}/issuetypes?project=${enc(project)}`),
  search: (connectorId: string, q: string) =>
    api<{ id: string; key: string; summary: string; status: string; issueType: string; priority: string | null; assignee: string | null; projectKey: string; url: string }[]>(
      'GET',
      `/integrations/jira/connectors/${enc(connectorId)}/search?q=${enc(q)}`,
    ),
  list: (contentId: string) => api<PageJiraLink[]>('GET', `/pages/${enc(contentId)}/jira-links`),
  create: (contentId: string, body: { connectorId: string; projectKey: string; issueTypeId: string; summary: string; description?: string; quote?: TextQuoteInput }) =>
    api<PageJiraLink>('POST', `/pages/${enc(contentId)}/jira-links/create`, body),
  link: (contentId: string, body: { connectorId: string; issueKeyOrId: string; quote?: TextQuoteInput }) =>
    api<PageJiraLink>('POST', `/pages/${enc(contentId)}/jira-links`, body),
  refresh: (contentId: string, linkId: string) => api<PageJiraLink>('POST', `/pages/${enc(contentId)}/jira-links/${enc(linkId)}/refresh`, {}),
  unlink: (contentId: string, linkId: string) => api<void>('DELETE', `/pages/${enc(contentId)}/jira-links/${enc(linkId)}`),
}

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
