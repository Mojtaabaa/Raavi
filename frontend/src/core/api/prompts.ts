import { api } from './http'
import type { RaaviDoc } from './wiki'

/** Prompt Hub (ADR-052). */
export type PromptVisibility = 'Personal' | 'Tenant'
export type PromptStatus = 'Draft' | 'Published' | 'Archived' | 'Deprecated'
export const PROMPT_CONTEXTS = ['page', 'selection', 'section', 'pages', 'space', 'jira-issue', 'jira-project', 'templar-report', 'generic'] as const
export type PromptContext = (typeof PROMPT_CONTEXTS)[number]
/** Flags: 1 = automatic execution, 2 = copy, 4 = preview. */
export const MODE_AUTOMATIC = 1
export const MODE_COPY = 2
export const MODE_PREVIEW = 4

export interface PromptSummary {
  id: string
  title: string
  description: string | null
  categoryId: string | null
  visibility: PromptVisibility
  status: PromptStatus
  tags: string[]
  language: string
  contexts: string[]
  modes: number
  ownerId: string
  ownerName: string | null
  createdAt: string
  updatedAt: string
  publishedAt: string | null
  lastUsedAt: string | null
  isOwner: boolean
  canEdit: boolean
  hasDraft: boolean
  latestVersion: number
  forkedFromId: string | null
  forkedFromTitle: string | null
  revision: number
  ratingAverage?: number | null
  ratingCount?: number
  myRating?: number | null
  uses?: number
}

export interface PromptVersionInfo {
  id: string
  number: number
  isPublished: boolean
  changeNote: string | null
  createdBy: string
  createdByName: string | null
  createdAt: string
  publishedAt: string | null
}

export interface PromptDetail {
  prompt: PromptSummary
  content: RaaviDoc
  contentText: string
  shown: PromptVersionInfo | null
  draft: PromptVersionInfo | null
  published: PromptVersionInfo | null
  variables: InputDefinition[]
}

export interface PromptPage {
  items: PromptSummary[]
  total: number
  skip: number
  take: number
}

export interface PromptCategory {
  id: string
  parentId: string | null
  name: string
  description: string | null
  sortOrder: number
  status: 'Active' | 'Archived'
  minePrompts: number
  publicPrompts: number
}

export interface PromptInput {
  title?: string
  description?: string | null
  categoryId?: string | null
  tags?: string[]
  language?: string
  contexts?: string[]
  modes?: number
  content?: RaaviDoc
  variables?: InputDefinition[]
  changeNote?: string | null
  revision?: number
}

export interface PromptFilter {
  scope: 'mine' | 'public'
  q?: string
  categoryId?: string | null
  context?: string
  status?: PromptStatus
  language?: string
  tag?: string
  sort?: string
  skip?: number
  take?: number
}

const qs = (f: PromptFilter) => {
  const p = new URLSearchParams({ scope: f.scope })
  for (const [k, v] of Object.entries(f)) if (k !== 'scope' && v !== undefined && v !== null && v !== '') p.set(k, String(v))
  return p.toString()
}

export const promptsApi = {
  list: (f: PromptFilter) => api<PromptPage>('GET', `/prompts?${qs(f)}`),
  get: (id: string) => api<PromptDetail>('GET', `/prompts/${id}`),
  create: (input: PromptInput) => api<PromptDetail>('POST', '/prompts', input),
  update: (id: string, input: PromptInput) => api<PromptDetail>('PUT', `/prompts/${id}`, input),
  publish: (id: string, changeNote?: string) => api<PromptDetail>('POST', `/prompts/${id}/publish`, { changeNote: changeNote ?? null }),
  setVisibility: (id: string, visibility: PromptVisibility) => api<PromptDetail>('POST', `/prompts/${id}/visibility`, { visibility }),
  setStatus: (id: string, status: PromptStatus) => api<PromptDetail>('POST', `/prompts/${id}/status`, { status }),
  duplicate: (id: string) => api<PromptDetail>('POST', `/prompts/${id}/duplicate`),
  remove: (id: string) => api<void>('DELETE', `/prompts/${id}`),
  move: (ids: string[], categoryId: string | null) => api<{ moved: number }>('POST', '/prompts/move', { ids, categoryId }),

  categories: (includeArchived = false) => api<PromptCategory[]>('GET', `/prompt-categories?includeArchived=${includeArchived}`),
  createCategory: (name: string, parentId: string | null, description?: string) => api<PromptCategory>('POST', '/prompt-categories', { name, parentId, description }),
  editCategory: (id: string, name: string, description?: string | null) => api<PromptCategory>('PUT', `/prompt-categories/${id}`, { name, description }),
  moveCategory: (id: string, parentId: string | null, index?: number) => api<PromptCategory[]>('POST', `/prompt-categories/${id}/move`, { parentId, index }),
  archiveCategory: (id: string) => api<PromptCategory[]>('POST', `/prompt-categories/${id}/archive`),
  deleteCategory: (id: string, strategy: 'parent' | 'category' | 'uncategorized', targetCategoryId?: string | null) =>
    api<PromptCategory[]>('POST', `/prompt-categories/${id}/delete`, { strategy, targetCategoryId: targetCategoryId ?? null }),
}

// ── Phases 2–4: context, running, ratings, forks, versions (ADR-052) ──
export interface PromptContextRequest {
  kind: string
  pageId?: string | null
  selectedText?: string | null
  section?: string | null
  pageIds?: string[] | null
  spaceKey?: string | null
  reference?: string | null
  text?: string | null
}
export interface InputDefinition {
  name: string
  label?: string | null
  type: 'text' | 'number' | 'boolean' | 'date' | 'select' | 'multiselect'
  required: boolean
  default?: string | null
  options?: string[] | null
  description?: string | null
}
export interface RenderRequest {
  versionId?: string | null
  context?: PromptContextRequest
  inputs?: Record<string, unknown>
  truncate?: boolean
  locale?: string
}
export interface PromptSource {
  kind: string
  title: string
  url: string | null
  chars: number
}
export interface RenderedPrompt {
  text: string
  tokenEstimate: number
  chars: number
  ready: boolean
  missing: string[]
  warnings: string[]
  sources: PromptSource[]
  variables: { name: string; origin: string; chars: number }[]
  truncated: boolean
  versionId: string
  versionNumber: number
  contextKind: string
  inputs: InputDefinition[]
}
export interface Execution {
  id: string
  promptId: string
  versionId: string
  versionNumber: number
  contextKind: string
  status: 'Running' | 'Completed' | 'Failed'
  result: string | null
  model: string | null
  promptTokens: number | null
  completionTokens: number | null
  durationMs: number | null
  estimatedTokens: number
  errorClass: string | null
  sources: PromptSource[]
  startedAt: string
  completedAt: string | null
}
export interface VariableInfo {
  name: string
  description: string
  type: string
  contexts: string[]
}
export interface Ratings {
  average: number | null
  count: number
  distribution: number[]
  mine: number | null
  entries: { userId: string; userName: string | null; stars: number; feedback: string | null; comment: string | null; updatedAt: string; mine: boolean }[]
}
export interface PromptStats {
  totalUses: number
  automaticExecutions: number
  successfulExecutions: number
  failedExecutions: number
  manualCopies: number
  confirmedManualUses: number
  uniqueUsers: number
  lastUsedAt: string | null
  averageRating: number | null
  ratingCount: number
  averageDurationMs: number | null
  tokensUsed: number
  estimatedCost: number | null
  versions: { versionId: string; number: number; started: number; completed: number; failed: number; copies: number }[]
}
export interface VersionDiff {
  fromNumber: number
  toNumber: number
  lines: { kind: 'same' | 'add' | 'del'; text: string }[]
  added: number
  removed: number
}
export interface Analytics {
  automatic: number
  successful: number
  failed: number
  copies: number
  confirmedManual: number
  users: number
  tokens: number
  prompts: { promptId: string; title: string; ownerName: string | null; automatic: number; successful: number; failed: number; copies: number; confirmedManual: number; users: number; tokens: number }[]
}

export const promptRunApi = {
  variables: () => api<{ variables: VariableInfo[]; kinds: string[] }>('GET', '/prompt-variables'),
  render: (id: string, body: RenderRequest) => api<RenderedPrompt>('POST', `/prompts/${id}/render`, body),
  copied: (id: string, versionId: string | null, context: string) => api<void>('POST', `/prompts/${id}/copied`, { versionId, context }),
  manualUse: (id: string, versionId: string | null, context: string) => api<void>('POST', `/prompts/${id}/manual-use`, { versionId, context }),
  execute: (id: string, render: RenderRequest, confirmed: boolean) => api<Execution>('POST', `/prompts/${id}/execute`, { render, confirmed }),
  executions: (promptId?: string) => api<Execution[]>('GET', `/prompt-executions${promptId ? `?promptId=${promptId}` : ''}`),
  stats: (id: string) => api<PromptStats>('GET', `/prompts/${id}/stats`),
  ratings: (id: string) => api<Ratings>('GET', `/prompts/${id}/ratings`),
  rate: (id: string, stars: number, feedback?: string | null, comment?: string | null) => api<Ratings>('PUT', `/prompts/${id}/rating`, { stars, feedback: feedback ?? null, comment: comment ?? null }),
  unrate: (id: string) => api<Ratings>('DELETE', `/prompts/${id}/rating`),
  fork: (id: string) => api<PromptDetail>('POST', `/prompts/${id}/fork`),
  versions: (id: string) => api<PromptVersionInfo[]>('GET', `/prompts/${id}/versions`),
  version: (id: string, versionId: string) => api<PromptDetail>('GET', `/prompts/${id}/versions/${versionId}`),
  diff: (id: string, from: string, to: string) => api<VersionDiff>('GET', `/prompts/${id}/diff?from=${from}&to=${to}`),
  restore: (id: string, versionId: string, discardDraft = false) => api<PromptDetail>('POST', `/prompts/${id}/versions/${versionId}/restore`, { discardDraft }),
  analytics: (from?: string, to?: string) => api<Analytics>('GET', `/prompt-analytics${from || to ? `?${new URLSearchParams({ ...(from ? { from } : {}), ...(to ? { to } : {}) })}` : ''}`),
}

/** Plain text (as an AI answer) → editor document: one paragraph per line, blank lines dropped. */
export function textToDoc(text: string): RaaviDoc {
  const lines = text.replace(/\r\n/g, '\n').split('\n').map((l) => l.trimEnd()).filter((l) => l.length > 0)
  return { schema: 'raavi.doc', schemaVersion: 2, doc: { type: 'doc', content: lines.length ? lines.map((l) => ({ type: 'paragraph', content: [{ type: 'text', text: l }] })) : [{ type: 'paragraph' }] } }
}
