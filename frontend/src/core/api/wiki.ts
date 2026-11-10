import { api, apiRequest, apiUpload } from './http'

/** Canonical page body (EDITOR_ARCHITECTURE.md §2). Phase 3: paragraphs of plain text; Phase 4 adds the editor schema. */
export interface RaaviDoc {
  schema: 'raavi.doc'
  schemaVersion: number
  doc: { type: 'doc'; content?: DocBlock[] }
}
export interface DocBlock {
  type: string
  attrs?: { dir?: 'auto' | 'ltr' | 'rtl' }
  content?: { type: string; text?: string }[]
}

export type SpaceType = 'Team' | 'Project' | 'Documentation' | 'Personal' | 'KnowledgeBase'
export const SPACE_TYPES: SpaceType[] = ['Team', 'Project', 'Documentation', 'KnowledgeBase', 'Personal']
export type SpaceVisibility = 'Open' | 'Private'
export type SubjectType = 'User' | 'Group' | 'AllMembers'
export type PermissionAction = 'View' | 'Create' | 'Edit' | 'Move' | 'Archive' | 'Delete' | 'Comment' | 'ManageTemplates' | 'ManagePermissions'
export type ContentStatus = 'Current' | 'Archived' | 'Trashed' | 'Draft'

export interface SpaceSummary {
  id: string
  key: string
  name: string
  description: string | null
  type: SpaceType
  status: 'Active' | 'Archived'
  homePageId: string | null
  updatedAt: string
}
export interface Space extends SpaceSummary {
  createdBy: string
  createdByName: string | null
  createdAt: string
  revision: number
  rights: { canView: boolean; canCreate: boolean; canAdminister: boolean; canManageTemplates: boolean }
  requirePublishApproval: boolean
}
export interface Grant {
  subjectType: SubjectType
  subjectId: string | null
  subjectName?: string | null
  action: PermissionAction
  effect: 'Allow' | 'Deny'
}
export interface TreeNode {
  id: string
  parentId: string | null
  contentType: 'page' | 'folder'
  title: string
  slug: string
  position: number
  hasChildren: boolean
}
export interface Breadcrumb {
  id: string
  title: string
  contentType: string
}
export interface ContentRights {
  canEdit: boolean
  canCreateChild: boolean
  canMove: boolean
  canArchive: boolean
  canDelete: boolean
  canRestrict: boolean
  canPublish: boolean
  canApprove: boolean
  canComment: boolean
  canModerateComments: boolean
}
export interface Page {
  id: string
  spaceId: string
  spaceKey: string
  spaceName: string
  parentId: string | null
  contentType: 'page' | 'folder'
  title: string
  slug: string
  status: ContentStatus
  body: RaaviDoc | null
  version: number
  labels: string[]
  breadcrumbs: Breadcrumb[]
  isRestricted: boolean
  createdBy: string
  createdByName: string | null
  createdAt: string
  updatedBy: string
  updatedByName: string | null
  updatedAt: string
  revision: number
  rights: ContentRights
  publishedAt: string | null
  hasDraft: boolean
  draftUpdatedAt: string | null
  scheduledPublishAt: string | null
  pendingApproval: boolean
  requirePublishApproval: boolean
}
export type VersionType = 'Published' | 'Restore' | 'Import' | 'Draft'
export interface VersionSummary {
  version: number
  title: string
  changeSummary: string | null
  createdByName: string | null
  createdAt: string
  type: VersionType
  restoredFrom: number | null
  contributors: string[]
}
export interface Version extends VersionSummary {
  body: RaaviDoc
}
export interface Draft {
  contentId: string
  exists: boolean
  title: string
  body: RaaviDoc
  baseVersion: number
  publishedVersion: number
  isStale: boolean
  revision: number
  updatedByName: string | null
  updatedAt: string | null
  contributors: string[]
  scheduledPublishAt: string | null
  scheduledByName: string | null
  pendingRequest: PublishRequest | null
}
export interface PublishRequest {
  id: string
  contentId: string
  title: string
  changeSummary: string | null
  status: 'Pending' | 'Approved' | 'Rejected' | 'Withdrawn' | 'Superseded'
  requestedByName: string | null
  requestedAt: string
  decidedByName: string | null
  decidedAt: string | null
  decisionComment: string | null
}
export type PublishOutcome = 'Published' | 'Scheduled' | 'PendingApproval' | 'RestoredToDraft'
export type DiffOp = 'Equal' | 'Insert' | 'Delete' | 'Change'
export interface DiffBlock {
  op: DiffOp
  type: string
  before: string | null
  after: string | null
  segments: { op: DiffOp; text: string }[] | null
}
export interface Diff {
  from: { label: string; version: number | null; title: string }
  to: { label: string; version: number | null; title: string }
  titleChanged: boolean
  blocks: DiffBlock[]
}
export interface TrashItem {
  id: string
  title: string
  contentType: string
  deletedAt: string
  deletedByName: string | null
  descendants: number
}
export interface RestrictionSubject {
  subjectType: 'User' | 'Group'
  subjectId: string
  name?: string | null
}
export interface Restrictions {
  view: RestrictionSubject[]
  edit: RestrictionSubject[]
  inheritedFrom: { contentId: string; title: string }[]
}
export interface ContentListItem {
  id: string
  contentType: string
  title: string
  slug: string
  spaceId: string
  spaceKey: string
  spaceName: string
  updatedAt: string
}
export interface Template {
  id: string
  spaceId: string | null
  name: string
  description: string | null
  body: RaaviDoc
  updatedAt: string
  revision: number
  canManage: boolean
}
export interface Activity {
  id: string
  kind: string
  spaceId: string
  spaceKey: string
  contentId: string | null
  title: string
  version: number | null
  actorName: string | null
  occurredAt: string
}
export interface Attachment {
  id: string
  contentId: string
  fileName: string
  mediaType: string
  size: number
  isImage: boolean
  createdAt: string
}
export interface DirectoryEntry {
  kind: 'User' | 'Group'
  id: string
  displayName: string
  detail: string | null
}

const enc = encodeURIComponent

/** Loaded resource plus its ETag for the next conditional update. */
export interface Versioned<T> {
  value: T
  etag: string | null
}

async function versioned<T>(p: Promise<{ data: T; etag: string | null }>): Promise<Versioned<T>> {
  const r = await p
  return { value: r.data, etag: r.etag }
}

export const wikiApi = {
  spaces: (opts: { includeArchived?: boolean; administration?: boolean } = {}) =>
    api<SpaceSummary[]>('GET', `/spaces?includeArchived=${opts.includeArchived ? 'true' : 'false'}${opts.administration ? '&scope=administration' : ''}`),
  capabilities: () => api<{ canCreateSpaces: boolean }>('GET', '/spaces-capabilities'),
  createSpace: (body: { key: string; name: string; description: string | null; type: SpaceType; visibility: SpaceVisibility }) => api<Space>('POST', '/spaces', body),
  space: (key: string) => versioned(apiRequest<Space>('GET', `/spaces/${enc(key)}`)),
  updateSpace: (key: string, etag: string | null, body: { name: string; description: string | null; type: SpaceType; homePageId: string | null }) =>
    versioned(apiRequest<Space>('PUT', `/spaces/${enc(key)}`, body, { ifMatch: etag })),
  archiveSpace: (key: string, etag: string | null, archive: boolean) => versioned(apiRequest<Space>('POST', `/spaces/${enc(key)}/${archive ? 'archive' : 'restore'}`, undefined, { ifMatch: etag })),
  permissions: (key: string) => api<{ grants: Grant[]; actions: PermissionAction[] }>('GET', `/spaces/${enc(key)}/permissions`),
  savePermissions: (key: string, grants: Grant[]) =>
    api<{ grants: Grant[]; actions: PermissionAction[] }>('PUT', `/spaces/${enc(key)}/permissions`, { grants: grants.map(({ subjectType, subjectId, action, effect }) => ({ subjectType, subjectId, action, effect })) }),
  tree: (key: string, parentId?: string | null) => api<TreeNode[]>('GET', `/spaces/${enc(key)}/tree${parentId ? `?parentId=${enc(parentId)}` : ''}`),
  recent: (key: string) => api<ContentListItem[]>('GET', `/spaces/${enc(key)}/recent?limit=10`),
  spaceActivity: (key: string) => api<Activity[]>('GET', `/spaces/${enc(key)}/activity?limit=20`),
  activity: () => api<Activity[]>('GET', '/activity?limit=20'),
  createContent: (key: string, body: { title: string; parentId: string | null; contentType: 'page' | 'folder'; body?: RaaviDoc; templateId?: string | null; publish?: boolean }) =>
    api<Page>('POST', `/spaces/${enc(key)}/content`, body),
  content: (id: string) => versioned(apiRequest<Page>('GET', `/content/${enc(id)}`)),
  updateContent: (id: string, etag: string | null, body: { title: string; body: RaaviDoc | null; changeSummary: string | null }) =>
    versioned(apiRequest<Page>('PUT', `/content/${enc(id)}`, body, { ifMatch: etag })),
  move: (id: string, etag: string | null, body: { spaceKey: string | null; parentId: string | null; position: number | null }) =>
    versioned(apiRequest<Page>('POST', `/content/${enc(id)}/move`, body, { ifMatch: etag })),
  archive: (id: string, etag: string | null, archive: boolean) => versioned(apiRequest<Page>('POST', `/content/${enc(id)}/${archive ? 'archive' : 'unarchive'}`, undefined, { ifMatch: etag })),
  trash: (id: string, etag: string | null) => apiRequest<void>('DELETE', `/content/${enc(id)}`, undefined, { ifMatch: etag }),
  versions: (id: string) => api<VersionSummary[]>('GET', `/content/${enc(id)}/versions`),
  restrictions: (id: string) => api<Restrictions>('GET', `/content/${enc(id)}/restrictions`),
  saveRestrictions: (id: string, view: RestrictionSubject[], edit: RestrictionSubject[]) =>
    api<Restrictions>('PUT', `/content/${enc(id)}/restrictions`, {
      view: view.map(({ subjectType, subjectId }) => ({ subjectType, subjectId })),
      edit: edit.map(({ subjectType, subjectId }) => ({ subjectType, subjectId })),
    }),
  addLabel: (id: string, name: string) => api<string[]>('POST', `/content/${enc(id)}/labels`, { name }),
  removeLabel: (id: string, name: string) => api<string[]>('DELETE', `/content/${enc(id)}/labels/${enc(name)}`),
  suggestLabels: (prefix: string) => api<string[]>('GET', `/labels?prefix=${enc(prefix)}`),
  labelContent: (name: string) => api<ContentListItem[]>('GET', `/labels/${enc(name)}/content`),
  quickFind: (q: string) => api<{ spaces: SpaceSummary[]; content: ContentListItem[] }>('GET', `/quick-find?q=${enc(q)}&limit=10`),
  spaceTemplates: (key: string) => api<Template[]>('GET', `/spaces/${enc(key)}/templates`),
  createSpaceTemplate: (key: string, body: { name: string; description: string | null; body: RaaviDoc }) => api<Template>('POST', `/spaces/${enc(key)}/templates`, body),
  globalTemplates: () => api<Template[]>('GET', '/templates'),
  createGlobalTemplate: (body: { name: string; description: string | null; body: RaaviDoc }) => api<Template>('POST', '/templates', body),
  template: (id: string) => versioned(apiRequest<Template>('GET', `/templates/${enc(id)}`)),
  updateTemplate: (id: string, etag: string | null, body: { name: string; description: string | null; body: RaaviDoc }) =>
    versioned(apiRequest<Template>('PUT', `/templates/${enc(id)}`, body, { ifMatch: etag })),
  deleteTemplate: (id: string) => api<void>('DELETE', `/templates/${enc(id)}`),
  tenantPermissions: () => api<{ grants: Grant[] }>('GET', '/tenant/permissions'),
  saveTenantPermissions: (grants: Grant[]) =>
    api<{ grants: Grant[] }>('PUT', '/tenant/permissions', { grants: grants.map(({ subjectType, subjectId, action, effect }) => ({ subjectType, subjectId, action, effect })) }),
  draft: (id: string) => versioned(apiRequest<Draft>('GET', `/content/${enc(id)}/draft`)),
  saveDraft: (id: string, etag: string | null, body: { title: string; body: RaaviDoc | null }) => versioned(apiRequest<Draft>('PUT', `/content/${enc(id)}/draft`, body, { ifMatch: etag })),
  discardDraft: (id: string) => api<void>('DELETE', `/content/${enc(id)}/draft`),
  publish: (id: string, draftEtag: string | null, body: { changeSummary: string | null; publishAt: string | null }) =>
    versioned(apiRequest<{ outcome: PublishOutcome; page: Page }>('POST', `/content/${enc(id)}/publish`, body, { ifMatch: draftEtag })),
  cancelSchedule: (id: string) => api<Draft>('DELETE', `/content/${enc(id)}/schedule`),
  version: (id: string, n: number) => api<Version>('GET', `/content/${enc(id)}/versions/${n}`),
  diff: (id: string, from: number, to: number | 'draft') => api<Diff>('GET', `/content/${enc(id)}/diff?from=${from}&to=${to}`),
  restoreVersion: (id: string, n: number, etag: string | null) =>
    versioned(apiRequest<{ outcome: PublishOutcome; page: Page }>('POST', `/content/${enc(id)}/versions/${n}/restore`, undefined, { ifMatch: etag })),
  publishRequests: (key: string) => api<PublishRequest[]>('GET', `/spaces/${enc(key)}/publish-requests?pending=true`),
  decide: (requestId: string, approve: boolean, comment: string | null) =>
    api<{ outcome: PublishOutcome; page: Page }>('POST', `/publish-requests/${enc(requestId)}/${approve ? 'approve' : 'reject'}`, { comment }),
  setPublishing: (key: string, etag: string | null, requireApproval: boolean) => versioned(apiRequest<Space>('PUT', `/spaces/${enc(key)}/publishing`, { requireApproval }, { ifMatch: etag })),
  trashList: (key: string) => api<TrashItem[]>('GET', `/spaces/${enc(key)}/trash`),
  restoreFromTrash: (id: string) => api<Page>('POST', `/content/${enc(id)}/restore-from-trash`),
  purge: (id: string) => api<void>('DELETE', `/content/${enc(id)}/purge`),
  collaborationConfig: () => api<{ enabled: boolean }>('GET', '/collaboration/config'),
  collaborationTicket: (contentId: string) => api<{ ticket: string; documentName: string; url: string; permission: string; expiresAt: string }>('POST', '/collaboration/tickets', { contentId }),
  saveDraftTitle: (id: string, title: string) => api<Draft>('PUT', `/content/${enc(id)}/draft/title`, { title }),
  myDrafts: () => api<ContentListItem[]>('GET', '/me/drafts'),
  directory: (q: string) => api<DirectoryEntry[]>('GET', `/directory?q=${enc(q)}`),
  editorSchema: () => api<{ schema: string; schemaVersion: number; maxBytes: number; extensions: { id: string; name: string; version: number; kind: string; insertable: boolean }[] }>('GET', '/editor/schema'),
  linkRewrites: () => api<{ prefix: string; baseUrl: string }[]>('GET', '/editor/link-rewrites'),
  contentHtml: (id: string) => api<{ html: string }>('GET', `/content/${enc(id)}/html`),
  upload: (contentId: string, file: File) => {
    const form = new FormData()
    form.append('file', file, file.name)
    return apiUpload<Attachment>(`/content/${enc(contentId)}/attachments`, form)
  },
}

/** Router location of a content item (ids identify content; the slug only decorates the URL). */
export function contentRoute(item: { id: string; slug?: string; spaceKey: string }) {
  return { name: 'page', params: { key: item.spaceKey, id: item.id, slug: item.slug || undefined } }
}

/** Same-site href stored in the document for a page (opaque path; no structured pageId attrs). */
export function contentHref(item: { id: string; spaceKey: string }) {
  return `/spaces/${item.spaceKey}/pages/${item.id}`
}
