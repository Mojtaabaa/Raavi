import type { IncomingMessage } from 'node:http'
import { Server, type Connection, type Document, type Hocuspocus } from '@hocuspocus/server'
import * as Y from 'yjs'
import { prosemirrorJSONToYXmlFragment, yXmlFragmentToProsemirrorJSON } from '@tiptap/y-tiptap'
import type { Schema } from 'prosemirror-model'
import { boardToJson, seedBoard, type BoardElementJson } from './board.ts'
import { ApiError, InternalApi } from './api.ts'
import { verify } from './signature.ts'
import { TicketError, TicketVerifier, type Ticket } from './tickets.ts'

/** Tiptap's Collaboration extension stores the document in this fragment. */
export const FIELD = 'default'
const GUID = '[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}'
/** Page drafts `{tenant}:page:{id}:draft` and whiteboards `{tenant}:board:{id}:live` (an address, never a credential). */
const DOCUMENT_NAME = new RegExp(`^${GUID}:(?:page:${GUID}:draft|board:${GUID}:live)$`)
const isBoard = (name: string) => name.includes(':board:')

export interface CollabConfig {
  apiUrl: string
  serviceKey: string
  port: number
  /** Periodic permission re-check of every connection (ADR-011). */
  recheckSeconds: number
  /** Full snapshot (and compaction) after this many stored updates. */
  snapshotEvery: number
  debounce: number
  maxDebounce: number
  /** Allowed browser origins for WebSocket upgrades (empty = any, development only). */
  allowedOrigins: string[]
  quiet?: boolean
}

interface DocState {
  /** State vector at the last successful store (null → store the full state next time). */
  stored: Uint8Array | null
  seq: number
  sinceSnapshot: number
  contributors: Set<string>
  /** Reset by the API (draft discarded/restored): never store this in-memory state again. */
  discarded: boolean
}

export interface Logger {
  info(msg: string, data?: object): void
  warn(msg: string, data?: object): void
}

/**
 * Raavi collaboration server (Hocuspocus 4 + Yjs): ticket authentication, persistence through the API's internal
 * endpoints only, permission re-checks and revocation, flush/reset requests from the API.
 */
export function createCollaborationServer(config: CollabConfig, schema: Schema, options: { fetchImpl?: typeof fetch; logger?: Logger } = {}) {
  const log: Logger = options.logger ?? { info: (m, d) => console.log(JSON.stringify({ level: 'info', msg: m, ...d })), warn: (m, d) => console.warn(JSON.stringify({ level: 'warn', msg: m, ...d })) }
  const api = new InternalApi(config.apiUrl, config.serviceKey, options.fetchImpl)
  const tickets = new TicketVerifier(() => api.jwks())
  const states = new Map<string, DocState>()
  const stateOf = (name: string) => {
    let s = states.get(name)
    if (!s) {
      s = { stored: null, seq: 0, sinceSnapshot: 0, contributors: new Set(), discarded: false }
      states.set(name, s)
    }
    return s
  }

  /** Stores the changes since the last store; the API validates the converted JSON and updates the shared draft. */
  async function persist(name: string, document: Y.Doc, forceSnapshot = false): Promise<void> {
    const state = stateOf(name)
    if (state.discarded) return
    const board = isBoard(name)
    const vector = Y.encodeStateVector(document)
    // Boards are small: every store carries the full state, which the API keeps as the one source for loading (no log, no compaction).
    const update = state.stored && !board ? Y.encodeStateAsUpdate(document, state.stored) : Y.encodeStateAsUpdate(document)
    const changed = !state.stored || !Buffer.from(vector).equals(Buffer.from(state.stored))
    if (changed) {
      const json = board ? { elements: boardToJson(document) } : yXmlFragmentToProsemirrorJSON(document.getXmlFragment(FIELD))
      const contributors = [...state.contributors]
      const result = await api.storeUpdate(name, update, json, contributors)
      state.stored = vector
      state.seq = result.seq
      state.sinceSnapshot++
      for (const c of contributors) state.contributors.delete(c)
    }
    if (!board && state.seq > 0 && state.sinceSnapshot > 0 && (forceSnapshot || state.sinceSnapshot >= config.snapshotEvery)) {
      await api.storeSnapshot(name, Y.encodeStateAsUpdate(document), state.seq)
      state.sinceSnapshot = 0
    }
  }

  function connectionsOf(instance: Hocuspocus, filter: (name: string) => boolean): { name: string; document: Document; connection: Connection<Ticket> }[] {
    const result: { name: string; document: Document; connection: Connection<Ticket> }[] = []
    for (const [name, document] of instance.documents) {
      if (!filter(name)) continue
      for (const connection of document.connections.keys()) result.push({ name, document, connection: connection as Connection<Ticket> })
    }
    return result
  }

  function terminate(connection: Connection<Ticket>, reason: string, code: number) {
    connection.close({ code, reason } as CloseEvent)
    // Also end the socket so a revoked client cannot keep sending on another multiplexed document.
    setTimeout(() => connection.webSocket.close(code, reason), 50)
  }

  /** Re-evaluates connections with the API: none → close (4403), view → read-only. */
  async function recheck(instance: Hocuspocus, filter: (name: string, ticket: Ticket) => boolean) {
    const targets = connectionsOf(instance, () => true).filter(({ name, connection }) => connection.context && filter(name, connection.context))
    const cache = new Map<string, Promise<'edit' | 'view' | 'none'>>()
    await Promise.all(
      targets.map(async ({ name, connection }) => {
        const t = connection.context
        const key = `${name}|${t.userId}|${t.sessionId}`
        let verdict = cache.get(key)
        if (!verdict) {
          verdict = api.check(name, t.userId, t.sessionId).then((r) => r.permission).catch(() => 'edit' as const) // API unreachable: keep, re-check later
          cache.set(key, verdict)
        }
        const permission = await verdict
        if (permission === 'none') {
          log.info('collaboration access revoked', { document: name, userId: t.userId })
          terminate(connection, 'raavi:revoked', 4403)
        } else if (permission === 'view' && !connection.readOnly) {
          connection.readOnly = true
        }
      }),
    )
  }

  async function readBody(request: IncomingMessage): Promise<Buffer> {
    const chunks: Buffer[] = []
    let size = 0
    for await (const chunk of request) {
      size += (chunk as Buffer).length
      if (size > 64 * 1024) throw new Error('body too large')
      chunks.push(chunk as Buffer)
    }
    return Buffer.concat(chunks)
  }

  const server = new Server<Ticket>({
    port: config.port,
    quiet: config.quiet ?? true,
    debounce: config.debounce,
    maxDebounce: config.maxDebounce,
    unloadImmediately: true,

    async onUpgrade({ request }) {
      const origin = request.headers.origin
      if (config.allowedOrigins.length && (!origin || !config.allowedOrigins.includes(origin))) throw new Error('origin not allowed')
    },

    async onAuthenticate({ token, documentName, connectionConfig }) {
      if (!DOCUMENT_NAME.test(documentName)) throw new Error('invalid document')
      try {
        const ticket = await tickets.verify(token, documentName)
        if (ticket.permission === 'view') connectionConfig.readOnly = true
        return ticket
      } catch (e) {
        log.warn('ticket rejected', { document: documentName, reason: e instanceof TicketError ? e.message : 'error' })
        throw new Error('permission denied')
      }
    },

    async onLoadDocument({ document, documentName }) {
      const state = stateOf(documentName)
      state.discarded = false
      const stored = await api.state(documentName)
      if (stored.snapshot || stored.updates.length) {
        if (stored.snapshot) Y.applyUpdate(document, Buffer.from(stored.snapshot, 'base64'), 'load')
        for (const u of stored.updates) Y.applyUpdate(document, Buffer.from(u, 'base64'), 'load')
        state.stored = Y.encodeStateVector(document)
        state.seq = stored.seq
        state.sinceSnapshot = stored.updates.length
      } else if (stored.seed) {
        // First collaborative session: build the Yjs document from the draft (or the published version), or from the board elements.
        if (isBoard(documentName)) seedBoard(document, (stored.seed as { elements?: BoardElementJson[] }).elements ?? [])
        else document.transact(() => prosemirrorJSONToYXmlFragment(schema, (stored.seed as { doc: Record<string, unknown> }).doc, document.getXmlFragment(FIELD)), 'seed')
        state.stored = null
      }
      return document
    },

    async onChange({ documentName, context }) {
      if (context?.userId) stateOf(documentName).contributors.add(context.userId)
    },

    async onStoreDocument({ document, documentName, clientsCount }) {
      try {
        await persist(documentName, document, clientsCount === 0)
      } catch (e) {
        if (e instanceof ApiError && e.status === 400) {
          // The API refused the content (outside the editor registry): drop the in-memory state, clients reload.
          log.warn('stored state rejected by the API; resetting document', { document: documentName })
          stateOf(documentName).discarded = true
          for (const { connection } of connectionsOf(server.hocuspocus, (n) => n === documentName)) terminate(connection, 'raavi:reset', 4409)
          return
        }
        throw e
      }
    },

    async afterUnloadDocument({ documentName }) {
      states.delete(documentName)
    },

    async onRequest({ request, response, instance }) {
      const url = new URL(request.url ?? '/', 'http://collab')
      if (!url.pathname.startsWith('/internal/')) return
      try {
        const body = await readBody(request)
        if (request.method !== 'POST' || !verify(config.serviceKey, request.headers['x-raavi-signature'] as string | undefined, 'POST', url.pathname + url.search, body)) {
          response.writeHead(401).end()
          throw null
        }
        const payload = JSON.parse(body.toString('utf8') || '{}') as { documentName?: string; tenantId?: string; userId?: string | null }
        if (url.pathname === '/internal/flush' && payload.documentName && DOCUMENT_NAME.test(payload.documentName)) {
          const document = instance.documents.get(payload.documentName)
          if (document) await persist(payload.documentName, document)
          response.writeHead(204).end()
        } else if (url.pathname === '/internal/reset' && payload.documentName && DOCUMENT_NAME.test(payload.documentName)) {
          const document = instance.documents.get(payload.documentName)
          if (document) {
            stateOf(payload.documentName).discarded = true
            for (const { connection } of connectionsOf(instance, (n) => n === payload.documentName)) terminate(connection, 'raavi:reset', 4409)
            await instance.unloadDocument(document)
          }
          response.writeHead(204).end()
        } else if (url.pathname === '/internal/recheck' && payload.tenantId) {
          await recheck(instance, (name, t) => name.startsWith(`${payload.tenantId}:`) && (!payload.userId || t.userId === payload.userId))
          response.writeHead(204).end()
        } else {
          response.writeHead(404).end()
        }
      } catch (e) {
        if (e === null) throw null
        if (!response.headersSent) response.writeHead(400).end()
      }
      throw null // response handled
    },
  })

  let timer: ReturnType<typeof setInterval> | undefined
  return {
    server,
    get hocuspocus() {
      return server.hocuspocus
    },
    async listen() {
      await server.listen()
      timer = setInterval(() => void recheck(server.hocuspocus, () => true), config.recheckSeconds * 1000)
      log.info('collaboration server listening', { port: server.address.port })
      return server
    },
    async destroy() {
      clearInterval(timer)
      server.hocuspocus.flushPendingStores()
      await server.destroy()
    },
    recheckAll: () => recheck(server.hocuspocus, () => true),
  }
}
