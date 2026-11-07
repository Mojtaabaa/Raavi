import { defineStore } from 'pinia'
import { ref } from 'vue'
import { notificationsApi, type AppNotification } from '@/core/api/comments'
import { connectEvents, disconnectEvents, eventsConnected, onEvent } from '@/core/realtime/events'

const FALLBACK_POLL_MS = 60_000

/** The signed-in member's notification inbox (tenant hosts only). */
export const useNotificationsStore = defineStore('notifications', () => {
  const unread = ref(0)
  const items = ref<AppNotification[]>([])
  const loaded = ref(false)
  let timer: ReturnType<typeof setInterval> | null = null
  let unsubscribe: (() => void) | null = null

  async function refreshCount() {
    unread.value = (await notificationsApi.unreadCount().catch(() => ({ count: unread.value }))).count
  }

  async function refreshList() {
    const r = await notificationsApi.list()
    items.value = r.items
    unread.value = r.unreadCount
    loaded.value = true
  }

  async function onSignal() {
    await refreshCount()
    if (loaded.value) await refreshList().catch(() => undefined)
  }

  function onVisible() {
    if (document.visibilityState === 'visible') void onSignal()
  }

  async function start() {
    if (unsubscribe) return
    unsubscribe = onEvent('notifications', () => void onSignal())
    await refreshCount()
    void connectEvents()
    // Push is the primary path; polling only while the event channel is down.
    timer = setInterval(() => {
      if (!eventsConnected()) void refreshCount()
    }, FALLBACK_POLL_MS)
    document.addEventListener('visibilitychange', onVisible)
  }

  async function stop() {
    unsubscribe?.()
    unsubscribe = null
    if (timer) clearInterval(timer)
    timer = null
    document.removeEventListener('visibilitychange', onVisible)
    unread.value = 0
    items.value = []
    loaded.value = false
    await disconnectEvents()
  }

  async function markRead(n: AppNotification) {
    if (n.readAt) return
    await notificationsApi.markRead(n.id)
    items.value = items.value.map((x) => (x.id === n.id ? { ...x, readAt: new Date().toISOString() } : x))
    unread.value = Math.max(0, unread.value - 1)
  }

  async function markAllRead() {
    await notificationsApi.markAllRead()
    const now = new Date().toISOString()
    items.value = items.value.map((n) => ({ ...n, readAt: n.readAt ?? now }))
    unread.value = 0
  }

  return { unread, items, loaded, start, stop, refreshList, refreshCount, markRead, markAllRead }
})
