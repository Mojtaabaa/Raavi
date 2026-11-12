import { api, apiRequest } from './http'

export interface AiSettings {
  enabled: boolean
  baseUrl: string
  model: string
  /** True when a key is stored; the key itself is never returned. */
  hasApiKey: boolean
  monthlyTokenCap: number
  maxOutputTokens: number
  revision: number
  /** False when the installation administrator has not allowed any AI host. */
  hostPolicyConfigured: boolean
  tokensThisMonth: number
  /** Embedding model for smart search ("" = keyword search only). */
  embeddingModel?: string
}

export interface AiSettingsInput {
  enabled: boolean
  baseUrl: string
  model: string
  /** Omitted/undefined keeps the stored key; an empty string removes it. */
  apiKey?: string
  monthlyTokenCap: number
  maxOutputTokens: number
  embeddingModel?: string
}

export interface SearchHit {
  contentId: string
  title: string
  spaceKey: string
  snippet: string
  score: number
  /** Found (also) by meaning, not only by words. */
  semantic: boolean
}

export interface AskAnswer {
  text: string
  model: string
  promptTokens: number
  completionTokens: number
  sources: { number: number; contentId: string; title: string; spaceKey: string; snippet: string; cited?: boolean }[]
}

export interface IndexStatus {
  pages: number
  chunks: number
  embeddedChunks: number
  pagesToIndex: number
  embeddingModel: string
  embeddingsOn: boolean
  lastIndexedAt: string | null
}

export interface AiAnswer {
  text: string
  model: string
  promptTokens: number
  completionTokens: number
  truncated: boolean
}

export interface AiUsage {
  tokensThisMonth: number
  requestsThisMonth: number
  failedThisMonth: number
  monthlyTokenCap: number
  byFeature: { feature: string; requests: number; tokens: number }[]
  topUsers: { userId: string; name: string | null; requests: number; tokens: number }[]
}

const enc = encodeURIComponent

export const aiApi = {
  status: () => api<{ available: boolean }>('GET', '/ai/status'),
  settings: () => api<AiSettings>('GET', '/ai/settings'),
  saveSettings: (current: AiSettings, body: AiSettingsInput) =>
    apiRequest<AiSettings>('PUT', '/ai/settings', body, current.revision > 0 ? { ifMatch: `"r${current.revision}"` } : {}).then((r) => r.data),
  usage: () => api<AiUsage>('GET', '/ai/usage'),
  summarize: (pageId: string) => api<AiAnswer>('POST', `/ai/pages/${enc(pageId)}/summary`, {}),
  search: (q: string, opts: {
    space?: string
    take?: number
    ownerUserId?: string
    reviewOwnerUserId?: string
    classification?: string
    lifecycleStatus?: string
    reviewDue?: boolean
    relationshipType?: string
    contentType?: string
    updatedFrom?: string
    updatedTo?: string
  } = {}) => {
    const p = new URLSearchParams()
    p.set('q', q)
    p.set('take', String(opts.take ?? 15))
    if (opts.space) p.set('space', opts.space)
    if (opts.ownerUserId) p.set('ownerUserId', opts.ownerUserId)
    if (opts.reviewOwnerUserId) p.set('reviewOwnerUserId', opts.reviewOwnerUserId)
    if (opts.classification) p.set('classification', opts.classification)
    if (opts.lifecycleStatus) p.set('lifecycleStatus', opts.lifecycleStatus)
    if (opts.reviewDue != null) p.set('reviewDue', String(opts.reviewDue))
    if (opts.relationshipType) p.set('relationshipType', opts.relationshipType)
    if (opts.contentType) p.set('contentType', opts.contentType)
    if (opts.updatedFrom) p.set('updatedFrom', opts.updatedFrom)
    if (opts.updatedTo) p.set('updatedTo', opts.updatedTo)
    return api<{ hits: SearchHit[]; mode: 'keyword' | 'hybrid' }>('GET', `/ai/search?${p}`)
  },
  askWiki: (question: string, space?: string) => api<AskAnswer>('POST', '/ai/ask', { question, space: space || null }),
  indexStatus: () => api<IndexStatus>('GET', '/ai/index'),
  runIndex: () => api<IndexStatus>('POST', '/ai/index/run', {}),
  rebuildIndex: () => api<IndexStatus>('POST', '/ai/index/rebuild', {}),
  ask: (pageId: string, question: string) => api<AiAnswer>('POST', `/ai/pages/${enc(pageId)}/ask`, { question }),
  /** EKOS Phase 2: selection from the client; requires Edit + confirmed consent. */
  editorSummarize: (pageId: string, selection: string, confirmed: boolean) =>
    api<AiAnswer>('POST', `/ai/pages/${enc(pageId)}/editor/summarize`, { selection, confirmed }),
  editorRewrite: (pageId: string, selection: string, instruction: string | undefined, confirmed: boolean) =>
    api<AiAnswer>('POST', `/ai/pages/${enc(pageId)}/editor/rewrite`, { selection, instruction: instruction || null, confirmed }),
  /** EKOS-9: catalog-constrained label suggestions (View). Empty / error → caller keeps heuristic list. */
  labelSuggestions: (pageId: string) =>
    api<{ slug: string; displayName: string; source: string }[]>('GET', `/ai/pages/${enc(pageId)}/label-suggestions`),
}
