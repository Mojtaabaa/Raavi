import { api, apiRequest, apiUpload } from './http'
import { prefixed } from '@/core/runtime/base'

export type BoardShape = 'Rect' | 'Ellipse' | 'Text' | 'Sticky' | 'Arrow' | 'Pen' | 'Diamond' | 'Triangle' | 'Line' | 'RoundRect' | 'Star' | 'Image' | 'Connector' | 'Group' | 'Table'

export type BoardPort = 'Center' | 'Top' | 'Right' | 'Bottom' | 'Left'
export type BoardRoute = 'Straight' | 'Orthogonal' | 'Curve'
export type BoardGridStyle = 'None' | 'Dot' | 'Line'

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
  cornerRadius?: number
  attachmentId?: string | null
  alt?: string
  locked?: boolean
  hidden?: boolean
  parentId?: string | null
  fromId?: string | null
  toId?: string | null
  fromPort?: BoardPort
  toPort?: BoardPort
  route?: BoardRoute
  startArrow?: boolean
  endArrow?: boolean
  children?: string[]
  rows?: number
  cols?: number
  cells?: string[]
  colWidths?: number[]
}

export interface Whiteboard {
  id: string
  spaceKey: string
  spaceName: string
  title: string
  description: string | null
  background: string
  gridStyle: BoardGridStyle
  elementCount: number
  /** Empty in list responses; present when a single board is loaded. */
  elements: BoardElement[]
  createdByName: string | null
  updatedByName: string | null
  createdAt: string
  updatedAt: string
  archivedAt: string | null
  revision: number
  canEdit: boolean
  favorited: boolean
}

export interface WhiteboardInput {
  title: string
  elements: BoardElement[]
  description?: string | null
  background?: string
  gridStyle?: BoardGridStyle
}

export interface BoardMetaInput {
  description?: string | null
  background?: string | null
  gridStyle?: BoardGridStyle | null
}

export interface BoardAttachment {
  id: string
  whiteboardId: string
  fileName: string
  mediaType: string
  size: number
  isImage: boolean
  createdAt: string
}

export interface BoardRevision {
  id: string
  boardRevision: number
  title: string
  createdAt: string
  createdByName: string | null
}

export interface BoardTemplate {
  id: string
  spaceId: string | null
  name: string
  description: string | null
  isSystem: boolean
  createdAt: string
}

export interface BoardComment {
  id: string
  parentId: string | null
  objectId: string | null
  x: number | null
  y: number | null
  body: string
  createdByName: string | null
  createdAt: string
  resolvedAt: string | null
}

export interface BoardCommentInput {
  body: string
  parentId?: string | null
  objectId?: string | null
  x?: number | null
  y?: number | null
}

const enc = encodeURIComponent

export const whiteboardsApi = {
  list: (spaceKey: string, includeArchived = false) => api<Whiteboard[]>('GET', `/spaces/${enc(spaceKey)}/whiteboards${includeArchived ? '?archived=true' : ''}`),
  get: (id: string) => api<Whiteboard>('GET', `/whiteboards/${enc(id)}`),
  create: (spaceKey: string, body: WhiteboardInput) => api<Whiteboard>('POST', `/spaces/${enc(spaceKey)}/whiteboards`, body),
  update: (board: Whiteboard, body: WhiteboardInput) => apiRequest<Whiteboard>('PUT', `/whiteboards/${enc(board.id)}`, body, { ifMatch: `"r${board.revision}"` }).then((r) => r.data),
  rename: (id: string, title: string) => api<Whiteboard>('PUT', `/whiteboards/${enc(id)}/title`, { title }),
  updateMeta: (id: string, body: BoardMetaInput) => api<Whiteboard>('PUT', `/whiteboards/${enc(id)}/meta`, body),
  duplicate: (id: string) => api<Whiteboard>('POST', `/whiteboards/${enc(id)}/duplicate`),
  archive: (id: string) => api<Whiteboard>('POST', `/whiteboards/${enc(id)}/archive`),
  restore: (id: string) => api<Whiteboard>('POST', `/whiteboards/${enc(id)}/restore`),
  favorite: (id: string) => api<void>('PUT', `/whiteboards/${enc(id)}/favorite`),
  unfavorite: (id: string) => api<void>('DELETE', `/whiteboards/${enc(id)}/favorite`),
  remove: (id: string) => api<void>('DELETE', `/whiteboards/${enc(id)}`),
  exportUrl: (id: string) => prefixed(`/api/v1/whiteboards/${enc(id)}/export`),
  exportJsonUrl: (id: string) => prefixed(`/api/v1/whiteboards/${enc(id)}/export.json`),
  listAttachments: (id: string) => api<BoardAttachment[]>('GET', `/whiteboards/${enc(id)}/attachments`),
  uploadAttachment: (id: string, form: FormData) => apiUpload<BoardAttachment>(`/whiteboards/${enc(id)}/attachments`, form),
  attachmentUrl: (id: string) => prefixed(`/api/v1/whiteboard-attachments/${enc(id)}`),
  listRevisions: (id: string) => api<BoardRevision[]>('GET', `/whiteboards/${enc(id)}/revisions`),
  restoreRevision: (id: string, revisionId: string) => api<Whiteboard>('POST', `/whiteboards/${enc(id)}/revisions/${enc(revisionId)}/restore`),
  listComments: (id: string) => api<BoardComment[]>('GET', `/whiteboards/${enc(id)}/comments`),
  addComment: (id: string, body: BoardCommentInput) => api<BoardComment>('POST', `/whiteboards/${enc(id)}/comments`, body),
  resolveComment: (id: string, commentId: string) => api<void>('POST', `/whiteboards/${enc(id)}/comments/${enc(commentId)}/resolve`),
  listTemplates: (spaceKey?: string) => api<BoardTemplate[]>('GET', `/whiteboard-templates${spaceKey ? `?spaceKey=${enc(spaceKey)}` : ''}`),
  createFromTemplate: (spaceKey: string, templateId: string, title?: string) =>
    api<Whiteboard>('POST', `/spaces/${enc(spaceKey)}/whiteboards/from-template/${enc(templateId)}`, title ? { title } : {}),
  saveAsTemplate: (id: string, title?: string) => api<BoardTemplate>('POST', `/whiteboards/${enc(id)}/template`, title ? { title } : {}),
}
