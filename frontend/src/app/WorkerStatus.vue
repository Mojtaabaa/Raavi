<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Dialog from 'primevue/dialog'
import { opsApi, type WorkerReport } from '@/core/api/ops'
import { useAuthStore } from '@/stores/auth'
import { useHostStore } from '@/stores/host'
import { useLocaleStore } from '@/stores/locale'

/**
 * Is the RaaviWorkers service running? (ADR-051) Shown to administrators only: a coloured dot in the header (green = reported in the last two minutes,
 * red = silent or never started) and a dialog with the details and what to do. Without the worker notifications, the activity feed, scheduled jobs,
 * workspace purges and the smart-search index stand still.
 */
const { t } = useI18n()
const auth = useAuthStore()
const host = useHostStore()
const locale = useLocaleStore()
const report = ref<WorkerReport | null>(null)
const open = ref(false)
let timer: number | undefined

const isSystem = computed(() => host.context?.kind === 'system')
const allowed = computed(() => !!auth.me && (isSystem.value ? auth.me.isSystemAdministrator : auth.me.isTenantAdministrator))
const running = computed(() => report.value?.state === 'Running')
const known = computed(() => !!report.value && report.value.state !== 'Unknown')

async function load() {
  if (!allowed.value) {
    report.value = null
    return
  }
  try {
    report.value = await opsApi.workers(isSystem.value)
  } catch {
    report.value = null
  }
}

const when = (iso: string | null) => (iso ? new Date(iso).toLocaleString(locale.locale === 'fa' ? 'fa-IR' : 'en-GB') : '—')
const backlog = computed(() => (report.value?.jobs.Enqueued ?? 0) + (report.value?.jobs.Scheduled ?? 0))

watch(allowed, load)
onMounted(() => {
  load()
  timer = window.setInterval(load, 60_000)
})
onBeforeUnmount(() => window.clearInterval(timer))
</script>

<template>
  <template v-if="allowed && known">
    <button type="button" :class="['worker', running ? 'ok' : 'down']" :title="running ? t('workers.running') : t('workers.stopped')" data-testid="worker-status" @click="open = true; load()">
      <span class="dot" aria-hidden="true" />
      <span class="label">{{ running ? t('workers.short') : t('workers.stoppedShort') }}</span>
    </button>
    <Dialog v-model:visible="open" modal :header="t('workers.title')" :style="{ inlineSize: 'min(34rem, 95vw)' }">
      <div v-if="report" class="body" data-testid="worker-dialog">
        <p :class="['state', running ? 'ok' : 'down']" data-testid="worker-state">
          <i :class="running ? 'pi pi-check-circle' : 'pi pi-exclamation-triangle'" aria-hidden="true" />
          {{ report.state === 'Running' ? t('workers.running') : report.state === 'NeverStarted' ? t('workers.neverStarted') : t('workers.stopped') }}
        </p>
        <dl>
          <dt>{{ t('workers.lastHeartbeat') }}</dt>
          <dd>{{ when(report.lastHeartbeat) }}</dd>
          <dt>{{ t('workers.checkedAt') }}</dt>
          <dd>{{ when(report.checkedAt) }}</dd>
          <dt>{{ t('workers.waiting') }}</dt>
          <dd :class="{ warn: !running && backlog > 0 }">{{ backlog }}</dd>
          <dt>{{ t('workers.processing') }}</dt>
          <dd>{{ report.jobs.Processing ?? 0 }}</dd>
          <dt>{{ t('workers.failed') }}</dt>
          <dd>{{ report.jobs.Failed ?? 0 }}</dd>
        </dl>
        <ul v-if="report.servers.length" class="servers">
          <li v-for="s in report.servers" :key="s.name + s.lastHeartbeat">
            <span :class="['dot', s.alive ? 'ok' : 'down']" aria-hidden="true" /> <strong dir="ltr">{{ s.name }}</strong>
            <span class="muted">{{ t('workers.since', { date: when(s.startedAt) }) }}</span>
          </li>
        </ul>
        <div v-if="!running" class="help" data-testid="worker-help">
          <p>{{ t('workers.effect') }}</p>
          <p>{{ t('workers.fix') }}</p>
          <code dir="ltr">Get-Service RaaviWorkers; Start-Service RaaviWorkers</code>
        </div>
      </div>
    </Dialog>
  </template>
</template>

<style scoped>
.worker { display: inline-flex; align-items: center; gap: 0.4rem; border: 1px solid var(--raavi-border); background: transparent; color: inherit; border-radius: 999px; padding: 0.2rem 0.6rem; font: inherit; font-size: 0.8rem; cursor: pointer; }
.worker.down { border-color: var(--p-red-500, #dc2626); color: var(--p-red-600, #dc2626); }
.dot { inline-size: 0.6rem; block-size: 0.6rem; border-radius: 50%; background: var(--p-green-500, #16a34a); display: inline-block; }
.worker.down .dot, .dot.down { background: var(--p-red-500, #dc2626); }
.state { display: flex; align-items: center; gap: 0.5rem; font-weight: 600; margin: 0 0 var(--raavi-space-3); }
.state.ok { color: var(--p-green-600, #16a34a); }
.state.down { color: var(--p-red-600, #dc2626); }
dl { display: grid; grid-template-columns: max-content 1fr; gap: 0.3rem 1rem; margin: 0 0 var(--raavi-space-3); }
dt { color: var(--raavi-text-muted); }
dd { margin: 0; }
dd.warn { color: var(--p-red-600, #dc2626); font-weight: 600; }
.servers { list-style: none; padding: 0; margin: 0 0 var(--raavi-space-3); display: grid; gap: 0.25rem; }
.muted { color: var(--raavi-text-muted); font-size: 0.85rem; margin-inline-start: 0.5rem; }
.help { padding: var(--raavi-space-2) var(--raavi-space-3); border-radius: var(--raavi-radius-sm); background: color-mix(in srgb, var(--p-red-500, #dc2626) 10%, transparent); }
.help p { margin: 0 0 var(--raavi-space-2); }
.help code { display: block; overflow-x: auto; }
@media (max-width: 960px) { .label { display: none; } }
</style>
