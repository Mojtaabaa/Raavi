import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { fetchHostContext, type HostContext } from '@/core/api/hostContext'
import { prefixed } from '@/core/runtime/base'

/** Which host the SPA runs on (tenant / system / unknown), loaded once at startup. */
export const useHostStore = defineStore('host', () => {
  const context = ref<HostContext | null>(null)
  const loading = computed(() => context.value === null)
  const tenant = computed(() => (context.value?.kind === 'tenant' ? context.value.tenant : null))

  /** Address of the workspace's own square logo, or null when it has none (the Raavi logo is shown then). */
  const logoUrl = computed(() => {
    const version = tenant.value?.branding?.logoVersion
    return version ? prefixed(`/api/v1/host/logo?v=${version}`) : null
  })

  async function load(fetchImpl: typeof fetch = fetch): Promise<HostContext> {
    context.value = await fetchHostContext(fetchImpl)
    return context.value
  }

  return { context, loading, tenant, logoUrl, load }
})
