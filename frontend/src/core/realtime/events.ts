import { HttpTransportType, HubConnectionBuilder, HubConnectionState, LogLevel, type HubConnection } from '@microsoft/signalr'

/**
 * In-app event channel (`/hubs/events`, ADR-024 §5). The server only sends content-free signals ("your inbox
 * changed"); data is always fetched through the REST API with its permission checks. WebSockets only (same transport
 * as live editing); cookies are the credential, so the connection is same-origin.
 */
export type EventName = 'notifications'

let connection: HubConnection | null = null
const handlers = new Map<EventName, Set<() => void>>()

export function onEvent(name: EventName, handler: () => void): () => void {
  let set = handlers.get(name)
  if (!set) handlers.set(name, (set = new Set()))
  set.add(handler)
  return () => set!.delete(handler)
}

function dispatch(name: EventName) {
  handlers.get(name)?.forEach((h) => h())
}

export async function connectEvents(): Promise<void> {
  if (typeof WebSocket === 'undefined' || connection) return
  let c: HubConnection
  try {
    c = new HubConnectionBuilder()
      .withUrl(new URL('/hubs/events', window.location.href).toString(), { transport: HttpTransportType.WebSockets, skipNegotiation: true })
      .withAutomaticReconnect([0, 2000, 5000, 10000, 30000, 60000])
      .configureLogging(LogLevel.None)
      .build()
  } catch {
    return
  }
  c.on('notifications', () => dispatch('notifications'))
  // After a reconnect the inbox may have changed while we were away.
  c.onreconnected(() => dispatch('notifications'))
  connection = c
  try {
    await c.start()
  } catch {
    // Offline or blocked: the notifications store falls back to periodic refresh.
  }
}

export async function disconnectEvents(): Promise<void> {
  const c = connection
  connection = null
  if (c && c.state !== HubConnectionState.Disconnected) await c.stop().catch(() => undefined)
}

export function eventsConnected(): boolean {
  return connection?.state === HubConnectionState.Connected
}
