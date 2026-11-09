import { defineStore } from 'pinia'
import { ref } from 'vue'
import { avatarsApi } from '@/core/api/avatars'

const BATCH = 100
const DELAY_MS = 25

/**
 * Which users have a profile picture (and its version). Components ask for ids; the store collects the ids of one render pass and asks the
 * server once (up to 100 per request), so a page with fifty comments costs one request — and users without a picture cost no failed image loads.
 * `null` = known to have none, `undefined` = not asked yet.
 */
export const useAvatarStore = defineStore('avatars', () => {
  const versions = ref<Record<string, number | null>>({})
  const pending = new Set<string>()
  let timer: ReturnType<typeof setTimeout> | null = null

  async function flush() {
    timer = null
    const ids = [...pending]
    pending.clear()
    for (let i = 0; i < ids.length; i += BATCH) {
      const chunk = ids.slice(i, i + BATCH)
      let found: Record<string, number> = {}
      try {
        found = await avatarsApi.versions(chunk)
      } catch {
        // pictures are decoration: a failed lookup only means initials are shown
      }
      const next = { ...versions.value }
      for (const id of chunk) next[id] = found[id] ?? null
      versions.value = next
    }
  }

  /** Asks (once) whether the user has a picture. */
  function want(id: string | null | undefined) {
    if (!id || versions.value[id] !== undefined || pending.has(id)) return
    pending.add(id)
    timer ??= setTimeout(() => void flush(), DELAY_MS)
  }

  /** After the signed-in user changed their own picture. */
  function set(id: string, version: number | null) {
    versions.value = { ...versions.value, [id]: version }
  }

  function reset() {
    versions.value = {}
    pending.clear()
    if (timer) clearTimeout(timer)
    timer = null
  }

  return { versions, want, set, reset }
})
