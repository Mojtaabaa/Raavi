// jsdom does not implement matchMedia; PrimeVue overlays (Select, etc.) subscribe to it on mount.
if (typeof window !== 'undefined' && typeof window.matchMedia !== 'function') {
  window.matchMedia = (query: string) =>
    ({
      matches: false,
      media: query,
      onchange: null,
      addListener: () => undefined,
      removeListener: () => undefined,
      addEventListener: () => undefined,
      removeEventListener: () => undefined,
      dispatchEvent: () => false,
    }) as MediaQueryList
}

// jsdom does not implement ResizeObserver; PrimeVue's auto-resizing Textarea observes its element.
if (typeof window !== 'undefined' && typeof window.ResizeObserver !== 'function') {
  window.ResizeObserver = class {
    observe(): void {}
    unobserve(): void {}
    disconnect(): void {}
  } as unknown as typeof ResizeObserver
}

// The in-app event channel (SignalR over WebSockets) has no server in unit tests. Handlers registered by the
// notifications store are collected so tests can simulate a push with `pushEvent('notifications')`.
import { vi } from 'vitest'
const eventHandlers: { name: string; handler: () => void }[] = []
;(globalThis as unknown as { pushEvent: (name: string) => void }).pushEvent = (name: string) =>
  eventHandlers.filter((e) => e.name === name).forEach((e) => e.handler())
vi.mock('@/core/realtime/events', () => ({
  connectEvents: async () => undefined,
  disconnectEvents: async () => undefined,
  eventsConnected: () => true,
  onEvent: (name: string, handler: () => void) => {
    const entry = { name, handler }
    eventHandlers.push(entry)
    return () => eventHandlers.splice(eventHandlers.indexOf(entry), 1)
  },
}))
