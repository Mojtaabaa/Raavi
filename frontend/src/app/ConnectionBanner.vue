<script setup lang="ts">
import { onBeforeUnmount, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { connection, markUp } from '@/core/api/connection'
import { ping } from '@/modules/status/statusApi'

/**
 * Shown on every page while real requests cannot reach the API. It says so plainly, links to the status page (which explains why) and keeps
 * probing `/health/live` by itself; it disappears as soon as the API answers.
 */
const { t } = useI18n()
let timer: ReturnType<typeof setTimeout> | undefined

async function tick() {
  if (await ping()) markUp()
  else timer = setTimeout(tick, 4000)
}

watch(
  () => connection.down.value,
  (down) => {
    clearTimeout(timer)
    if (down) timer = setTimeout(tick, 2500)
  },
  { immediate: true },
)
onBeforeUnmount(() => clearTimeout(timer))
</script>

<template>
  <div v-if="connection.down.value" class="conn down" role="alert" data-testid="connection-down">
    <i class="pi pi-exclamation-triangle" aria-hidden="true" />
    <strong>{{ t('status.banner.down') }}</strong>
    <span class="grow">{{ t('status.banner.help') }}</span>
    <button type="button" class="link" @click="tick">{{ t('status.banner.retry') }}</button>
    <RouterLink to="/status" class="link">{{ t('status.banner.details') }}</RouterLink>
  </div>
  <div v-else-if="connection.restored.value" class="conn up" role="status" data-testid="connection-restored">
    <i class="pi pi-check-circle" aria-hidden="true" />
    <span>{{ t('status.banner.restored') }}</span>
  </div>
</template>

<style scoped>
.conn { position: sticky; inset-block-start: 0; z-index: 1000; display: flex; align-items: center; gap: var(--raavi-space-2); flex-wrap: wrap; padding: var(--raavi-space-2) var(--raavi-space-4); color: #fff; font-size: 0.92rem; }
.conn.down { background: #b91c1c; }
.conn.up { background: #15803d; }
.grow { flex: 1; min-inline-size: 12rem; opacity: 0.95; }
.link { all: unset; cursor: pointer; text-decoration: underline; font-weight: 600; }
.link:focus-visible { outline: 2px solid #fff; outline-offset: 2px; }
.conn .pi { color: #fff; }
</style>
