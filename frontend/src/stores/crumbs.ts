import { defineStore } from 'pinia'
import { ref } from 'vue'
import type { Crumb } from '@/core/navigation/trail'

/**
 * The trail a view provides for itself (it knows the space, the page ancestors, the database …). Views set it when their data has loaded and
 * clear it when they leave; `Breadcrumbs.vue` falls back to the route when nothing is set.
 */
export const useCrumbsStore = defineStore('crumbs', () => {
  const trail = ref<Crumb[] | null>(null)
  const set = (value: Crumb[]) => (trail.value = value)
  const clear = () => (trail.value = null)
  return { trail, set, clear }
})
