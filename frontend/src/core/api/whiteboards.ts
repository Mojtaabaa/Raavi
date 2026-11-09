import { api, apiRequest } from './http'
import { prefixed } from '@/core/runtime/base'

export type BoardShape = 'Rect' | 'Ellipse' | 'Text' | 'Sticky' | 'Arrow' | 'Pen' | 'Diamond' | 'Triangle' | 'Line'

/**
 * One element of a board. (x, y) is the top-left corner and rotation turns the element around it; arrows and pen strokes keep their
 * coordinates in `points` relative to (x, y). List order is the stacking order.
 */
export interface BoardElement {
  id: string
  type: BoardShape
  x: number
  y: number
  w: number
  h: number
  rotation: number
  stroke: string
  fill: string | null
  strokeWidth: number
  text: string
  fontSize: number
  rtl: boolean
  points: number[]
}

export interface Whiteboard {
  id: string
  spaceKey: string
  spaceName: string
  title: string
  elementCount: number
  /** Empty in list responses; present when a single board is loaded. */
  elements: BoardElement[]
  createdByName: string | null
  createdAt: string
  updatedAt: string
  revision: number
  canEdit: boolean
}

export interface WhiteboardInput {
  title: string
  elements: BoardElement[]
}

const enc = encodeURIComponent

export const whiteboardsApi = {
  list: (spaceKey: string) => api<Whiteboard[]>('GET', `/spaces/${enc(spaceKey)}/whiteboards`),
  get: (id: string) => api<Whiteboard>('GET', `/whiteboards/${enc(id)}`),
  create: (spaceKey: string, body: WhiteboardInput) => api<Whiteboard>('POST', `/spaces/${enc(spaceKey)}/whiteboards`, body),
  update: (board: Whiteboard, body: WhiteboardInput) => apiRequest<Whiteboard>('PUT', `/whiteboards/${enc(board.id)}`, body, { ifMatch: `"r${board.revision}"` }).then((r) => r.data),
  rename: (id: string, title: string) => api<Whiteboard>('PUT', `/whiteboards/${enc(id)}/title`, { title }),
  remove: (id: string) => api<void>('DELETE', `/whiteboards/${enc(id)}`),
  exportUrl: (id: string) => prefixed(`/api/v1/whiteboards/${enc(id)}/export`),
}
