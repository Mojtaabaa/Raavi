import { ref, type Ref } from 'vue'
import * as Y from 'yjs'
import { HocuspocusProvider } from '@hocuspocus/provider'
import { api } from '@/core/api/http'
import { colorFor, type LivePeer, type LiveStatus } from '@/modules/editor/collaboration'
import { LiveBoard } from './liveBoard'

/** Answer of `POST /collaboration/tickets` for a board. */
interface BoardTicket {
  ticket: string
  documentName: string
  url: string
}

export interface BoardSession {
  board: LiveBoard
  status: Ref<LiveStatus>
  peers: Ref<LivePeer[]>
  destroy(): void
}

/** True when the installation runs a collaboration server (the editor falls back to whole-board saves otherwise). */
export const liveBoardsAvailable = async (): Promise<boolean> => (await api<{ enabled: boolean }>('GET', '/collaboration/config').catch(() => ({ enabled: false }))).enabled

const requestTicket = (boardId: string) => api<BoardTicket>('POST', '/collaboration/tickets', { contentId: boardId, kind: 'board' })

/**
 * Joins the live session of a board. The first ticket is fetched up front, so a refusal (no edit right, collaboration off) is an error here, not
 * a silent offline state; every reconnect fetches a fresh ticket, which makes the API re-check the user's rights each time.
 * Server close reasons: `raavi:revoked` (rights withdrawn — stop) and `raavi:reset` (the board was replaced by a whole-board save — re-join).
 * There is no offline cache: unsynced edits stay in memory and merge on reconnect, but a reload while offline loses them.
 */
export async function startBoardSession(boardId: string, user: { id: string; name: string }): Promise<BoardSession> {
  const first = await requestTicket(boardId)
  const doc = new Y.Doc()
  const status = ref<LiveStatus>('connecting')
  const peers = ref<LivePeer[]>([])
  let stopped = false
  let pending: string | null = first.ticket

  const provider = new HocuspocusProvider({
    url: first.url,
    name: first.documentName,
    document: doc,
    token: async () => {
      if (pending) {
        const t = pending
        pending = null
        return t
      }

      return (await requestTicket(boardId)).ticket
    },
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
        stopped = true
        provider.disconnect()
        status.value = 'reset'
      }
    },
    onAwarenessChange: ({ states }) => {
      peers.value = states
        .filter((s) => s.clientId !== doc.clientID && s.user)
        .map((s) => ({ clientId: s.clientId, name: String(s.user.name ?? ''), color: String(s.user.color ?? '#666') }))
    },
  })
  provider.setAwarenessField('user', { name: user.name, color: colorFor(user.id) })

  const board = new LiveBoard(doc)
  return {
    board,
    status,
    peers,
    destroy() {
      stopped = true
      board.destroy()
      provider.destroy()
    },
  }
}
