import { ref, type Ref } from 'vue'
import * as Y from 'yjs'
import { IndexeddbPersistence } from 'y-indexeddb'
import { HocuspocusProvider } from '@hocuspocus/provider'
import Collaboration from '@tiptap/extension-collaboration'
import CollaborationCaret from '@tiptap/extension-collaboration-caret'
import type { AnyExtension } from '@tiptap/core'

/** What the editor needs to join a live document (from `POST /collaboration/tickets`). */
export interface CollaborationTarget {
  documentName: string
  url: string
  /** Fetches a fresh single-use ticket; called on every (re)connect so permissions are re-evaluated. */
  ticket: () => Promise<string>
  user: { id: string; name: string }
}

export type LiveStatus = 'connecting' | 'live' | 'offline' | 'revoked' | 'reset'

export interface LivePeer {
  clientId: number
  name: string
  color: string
}

const STORAGE_PREFIX = 'raavi-collab:'

/** Stable, readable colour per user (presence carets). */
export function colorFor(id: string): string {
  let h = 0
  for (const ch of id) h = (h * 31 + ch.charCodeAt(0)) >>> 0
  return `hsl(${h % 360} 70% 40%)`
}

/** IndexedDB key: tenant is part of the document name, so cached drafts never mix between workspaces. */
export function storageKey(documentName: string): string {
  return STORAGE_PREFIX + documentName
}

/** Deletes every locally cached live document (called on sign-out, ADR-011 §7). */
export async function clearCollaborationCache(): Promise<void> {
  if (typeof indexedDB === 'undefined' || typeof indexedDB.databases !== 'function') return
  const dbs = await indexedDB.databases().catch(() => [])
  await Promise.all(
    dbs
      .map((d) => d.name)
      .filter((n): n is string => !!n && n.startsWith(STORAGE_PREFIX))
      .map((n) => new Promise<void>((resolve) => {
        const r = indexedDB.deleteDatabase(n)
        r.onsuccess = r.onerror = r.onblocked = () => resolve()
      })),
  )
}

export interface LiveSession {
  document: Y.Doc
  provider: HocuspocusProvider
  status: Ref<LiveStatus>
  peers: Ref<LivePeer[]>
  extensions: AnyExtension[]
  destroy(): void
}

/**
 * Joins a live document: Yjs doc + offline cache (y-indexeddb) + Hocuspocus provider + Tiptap collaboration/caret.
 * Server close reasons: `raavi:revoked` (access removed — stop, do not reconnect) and `raavi:reset` (the draft was
 * replaced — drop the local cache; the caller re-joins).
 */
export function startLiveSession(target: CollaborationTarget): LiveSession {
  const document = new Y.Doc()
  const status = ref<LiveStatus>('connecting')
  const peers = ref<LivePeer[]>([])
  const persistence = new IndexeddbPersistence(storageKey(target.documentName), document)
  let stopped = false

  const provider = new HocuspocusProvider({
    url: target.url,
    name: target.documentName,
    document,
    token: target.ticket,
    onSynced: () => {
      if (!stopped) status.value = 'live'
    },
    onDisconnect: () => {
      if (!stopped && status.value !== 'revoked' && status.value !== 'reset') status.value = 'offline'
    },
    onAuthenticationFailed: () => {
      stopped = true
      status.value = 'revoked'
      provider.disconnect()
    },
    onClose: ({ event }) => {
      const reason = String(event?.reason ?? '')
      if (reason === 'raavi:revoked') {
        stopped = true
        status.value = 'revoked'
        provider.disconnect()
      } else if (reason === 'raavi:reset') {
        // Drop the local copy *before* signalling: the caller re-joins with a new session, which must not reload
        // (and re-sync) the discarded content from IndexedDB.
        stopped = true
        provider.disconnect()
        void persistence.clearData().finally(() => (status.value = 'reset'))
      }
    },
    onAwarenessChange: ({ states }) => {
      peers.value = states
        .filter((s) => s.clientId !== document.clientID && s.user)
        .map((s) => ({ clientId: s.clientId, name: String(s.user.name ?? ''), color: String(s.user.color ?? '#666') }))
    },
  })

  const user = { name: target.user.name, color: colorFor(target.user.id) }
  return {
    document,
    provider,
    status,
    peers,
    extensions: [
      Collaboration.configure({ document, field: 'default' }),
      CollaborationCaret.configure({ provider, user }),
    ],
    destroy() {
      stopped = true
      provider.destroy()
      void persistence.destroy()
    },
  }
}
