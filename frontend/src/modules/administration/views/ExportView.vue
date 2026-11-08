<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Message from 'primevue/message'

/**
 * Data export of the workspace (Phase 12a). The archive is built on request and downloaded straight away; nothing is kept on the server. It holds what
 * the signed-in administrator may view — not a full-tenant dump — and a manifest lists what was included and omitted.
 */
const { t } = useI18n()
const busy = ref(false)
const failed = ref('')

async function download() {
  busy.value = true
  failed.value = ''
  try {
    const response = await fetch('/api/v1/tenant/export', { credentials: 'same-origin' })
    if (!response.ok) {
      failed.value = response.status === 409 ? t('export.inProgress') : t('export.failed')
      return
    }

    const blob = await response.blob()
    const name = /filename\*?=(?:UTF-8'')?"?([^";]+)"?/i.exec(response.headers.get('Content-Disposition') ?? '')?.[1] ?? 'raavi-export.zip'
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = decodeURIComponent(name)
    a.click()
    setTimeout(() => URL.revokeObjectURL(url), 10_000)
  } catch {
    failed.value = t('export.failed')
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="export" data-testid="export">
    <h1>{{ t('export.title') }}</h1>
    <p>{{ t('export.intro') }}</p>
    <h2>{{ t('export.included') }}</h2>
    <ul>
      <li>{{ t('export.i1') }}</li>
      <li>{{ t('export.i2') }}</li>
      <li>{{ t('export.i3') }}</li>
    </ul>
    <h2>{{ t('export.notIncluded') }}</h2>
    <ul>
      <li>{{ t('export.n1') }}</li>
      <li>{{ t('export.n2') }}</li>
      <li>{{ t('export.n3') }}</li>
    </ul>
    <Message severity="info" :closable="false">{{ t('export.scopeNote') }}</Message>
    <Message v-if="failed" severity="error" :closable="false" data-testid="export-error">{{ failed }}</Message>
    <Button icon="pi pi-download" :label="busy ? t('export.preparing') : t('export.download')" :loading="busy" data-testid="export-download" @click="download" />
  </section>
</template>

<style scoped>
.export { display: grid; gap: var(--raavi-space-3); max-inline-size: 44rem; justify-items: start; }
h1 { margin: 0; font-size: 1.5rem; }
h2 { margin: 0; font-size: 1.05rem; }
ul { margin: 0; padding-inline-start: 1.2rem; display: grid; gap: 0.2rem; }
p { margin: 0; }
</style>
