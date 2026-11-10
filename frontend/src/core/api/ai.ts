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
  sources: { number: number; contentId: string; title: string; spaceKey: string; snippet: string }[]
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
  search: (q: string, space?: string, take = 15) => api<{ hits: SearchHit[]; mode: 'keyword' | 'hybrid' }>('GET', `/ai/search?q=${enc(q)}${space ? `&space=${enc(space)}` : ''}&take=${take}`),
  askWiki: (question: string, space?: string) => api<AskAnswer>('POST', '/ai/ask', { question, space: space || null }),
  indexStatus: () => api<IndexStatus>('GET', '/ai/index'),
  runIndex: () => api<IndexStatus>('POST', '/ai/index/run', {}),
  rebuildIndex: () => api<IndexStatus>('POST', '/ai/index/rebuild', {}),
  ask: (pageId: string, question: string) => api<AiAnswer>('POST', `/ai/pages/${enc(pageId)}/ask`, { question }),
}
