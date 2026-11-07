import { api, apiRequest } from './http'
import type { RaaviDoc } from './wiki'

/** Text-quote selector of an inline thread (ADR-024): resolved against the page's plain text on the client. */
export interface CommentAnchor {
  exact: string
  prefix: string
  suffix: string
}
export interface Reaction {
  emoji: string
  count: number
  mine: boolean
}
export interface Comment {
  id: string
  threadId: string
  parentId: string | null
  body: RaaviDoc | null
  isDeleted: boolean
  /** Null for an imported comment whose source author is not a workspace member (authorName is the source name). */
  authorId: string | null
  authorName: string | null
  createdAt: string
  editedAt: string | null
  pageVersion: number
  reactions: Reaction[]
  revision: number
  rights: { canEdit: boolean; canDelete: boolean }
}
export type CommentKind = 'Page' | 'Inline'
export interface Thread {
  id: string
  kind: CommentKind
  anchor: CommentAnchor | null
  status: 'Open' | 'Resolved'
  resolvedByName: string | null
  resolvedAt: string | null
  canResolve: boolean
  canReply: boolean
  comments: Comment[]
}
export interface CommentsResponse {
  threads: Thread[]
  canComment: boolean
  watching: boolean
  reactions: string[]
}
export type NotificationKind = 'mention' | 'comment.reply' | 'comment.created' | 'comment.resolved' | 'page.published'
export interface AppNotification {
  id: string
  kind: NotificationKind
  spaceId: string
  spaceKey: string | null
  contentId: string
  commentId: string | null
  actorId: string
  actorName: string | null
  title: string
  excerpt: string
  createdAt: string
  readAt: string | null
}

const enc = encodeURIComponent

export const commentsApi = {
  list: (contentId: string, includeResolved: boolean) => api<CommentsResponse>('GET', `/content/${enc(contentId)}/comments?includeResolved=${includeResolved}`),
  create: (contentId: string, body: RaaviDoc, anchor: CommentAnchor | null) =>
    api<Thread>('POST', `/content/${enc(contentId)}/comments`, { body, kind: anchor ? 'Inline' : 'Page', anchor }),
  reply: (commentId: string, body: RaaviDoc) => api<Comment>('POST', `/comments/${enc(commentId)}/replies`, { body }),
  edit: (comment: Comment, body: RaaviDoc) => apiRequest<Comment>('PUT', `/comments/${enc(comment.id)}`, { body }, { ifMatch: `"r${comment.revision}"` }).then((r) => r.data),
  remove: (commentId: string) => api<void>('DELETE', `/comments/${enc(commentId)}`),
  resolve: (threadId: string, resolved: boolean) => api<Thread>('POST', `/comments/${enc(threadId)}/${resolved ? 'resolve' : 'reopen'}`),
  react: (commentId: string, emoji: string, on: boolean) => api<Reaction[]>('POST', `/comments/${enc(commentId)}/reactions`, { emoji, on }),
  watch: (contentId: string, watching: boolean) => api<{ watching: boolean }>(watching ? 'PUT' : 'DELETE', `/content/${enc(contentId)}/watch`, watching ? {} : undefined),
}

export const notificationsApi = {
  list: (unreadOnly = false) => api<{ items: AppNotification[]; unreadCount: number }>('GET', `/notifications?unreadOnly=${unreadOnly}`),
  unreadCount: () => api<{ count: number }>('GET', '/notifications/unread-count'),
  markRead: (id: string) => api<void>('POST', `/notifications/${enc(id)}/read`),
  markAllRead: () => api<void>('POST', '/notifications/read-all'),
  preferences: () => api<{ emailEnabled: boolean; emailAvailable: boolean }>('GET', '/notifications/preferences'),
  setPreferences: (emailEnabled: boolean) => api<{ emailEnabled: boolean; emailAvailable: boolean }>('PUT', '/notifications/preferences', { emailEnabled }),
}
