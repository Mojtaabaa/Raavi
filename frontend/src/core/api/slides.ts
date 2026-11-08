import { api, apiRequest } from './http'

export type SlideLayout = 'Title' | 'Bullets' | 'Text'
export type SlideDirection = 'Ltr' | 'Rtl'

export interface Slide {
  id: string
  layout: SlideLayout
  title: string
  body: string
  notes: string
}

export interface SlideDeck {
  id: string
  spaceKey: string
  spaceName: string
  title: string
  direction: SlideDirection
  slideCount: number
  /** Empty in list responses; present when a single deck is loaded. */
  slides: Slide[]
  createdByName: string | null
  createdAt: string
  updatedAt: string
  revision: number
  canEdit: boolean
}

export interface SlideInput {
  /** Absent for a new slide. */
  id?: string
  layout: SlideLayout
  title: string
  body: string
  notes: string
}

export interface SlideDeckInput {
  title: string
  direction: SlideDirection
  slides: SlideInput[]
}

const enc = encodeURIComponent

export const slidesApi = {
  list: (spaceKey: string) => api<SlideDeck[]>('GET', `/spaces/${enc(spaceKey)}/slides`),
  get: (id: string) => api<SlideDeck>('GET', `/slides/${enc(id)}`),
  create: (spaceKey: string, body: SlideDeckInput) => api<SlideDeck>('POST', `/spaces/${enc(spaceKey)}/slides`, body),
  update: (deck: SlideDeck, body: SlideDeckInput) => apiRequest<SlideDeck>('PUT', `/slides/${enc(deck.id)}`, body, { ifMatch: `"r${deck.revision}"` }).then((r) => r.data),
  remove: (id: string) => api<void>('DELETE', `/slides/${enc(id)}`),
  exportUrl: (id: string) => `/api/v1/slides/${enc(id)}/export`,
}
