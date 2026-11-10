<script setup lang="ts">
import CalendarDateInput from '@/app/CalendarDateInput.vue'
import { formatDate } from '@/core/i18n/locales'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import { errorLogApi, type ErrorDetail, type ErrorItem, type ErrorSummary } from '@/core/api/errorLog'
import { useLocaleStore } from '@/stores/locale'
import MiniCharts from '@/modules/status/MiniCharts.vue'

/**
 * Error log of the installation (system administrators): every unhandled server error and every error the browsers reported, with search
 * over all fields, grouping of identical errors, and the full detail (stack trace, request, correlation id) on demand.
 */
const { t } = useI18n()
const locale = useLocaleStore()
const confirm = useConfirm()
const toast = useToast()

const q = ref('')
const source = ref('')
const from = ref('')
const to = ref('')
const fingerprint = ref('')
const page = ref(0)
const pageSize = ref(50)
const total = ref(0)
const items = ref<ErrorItem[]>([])
const summary = ref<ErrorSummary | null>(null)
const loading = ref(false)
const failed = ref(false)
const open = ref<string | null>(null)
const detail = ref<ErrorDetail | null>(null)
const auto = ref(false)
let timer: ReturnType<typeof setTimeout> | undefined
let refresher: ReturnType<typeof setInterval> | undefined

const dayStart = (d: string) => (d ? new Date(d + 'T00:00:00').toISOString() : undefined)
const dayEnd = (d: string) => (d ? new Date(new Date(d + 'T00:00:00').getTime() + 86_400_000).toISOString() : undefined)

let ticket = 0
async function load() {
  const mine = ++ticket
  loading.value = true
  failed.value = false
  try {
    const [list, sum] = await Promise.all([
      errorLogApi.list({ q: q.value.trim() || undefined, source: source.value || undefined, from: dayStart(from.value), to: dayEnd(to.value), fingerprint: fingerprint.value || undefined, skip: page.value * pageSize.value, take: pageSize.value }),
      errorLogApi.summary(14),
    ])
    if (mine !== ticket) return
    items.value = list.items
    total.value = list.total
    summary.value = sum
  } catch {
    if (mine === ticket) failed.value = true
  } finally {
    if (mine === ticket) loading.value = false
  }
}

watch(q, () => {
  clearTimeout(timer)
  timer = setTimeout(() => {
    page.value = 0
    void load()
  }, 300)
})
watch([source, from, to, fingerprint, pageSize], () => {
  page.value = 0
  void load()
})
watch(auto, (on) => {
  clearInterval(refresher)
  if (on) refresher = setInterval(load, 10_000)
})
onMounted(load)
onBeforeUnmount(() => {
  clearTimeout(timer)
  clearInterval(refresher)
})

async function toggle(e: ErrorItem) {
  if (open.value === e.id) {
    open.value = null
    detail.value = null
    return
  }
  open.value = e.id
  detail.value = null
  try {
    detail.value = await errorLogApi.get(e.id)
  } catch {
    /* the row stays open without detail */
  }
}

function purge() {
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('errorLog.confirmPurge'),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      const r = await errorLogApi.purge(0)
      toast.add({ severity: 'success', summary: t('errorLog.purged', { n: r.deleted }), life: 4000 })
      await load()
    },
  })
}

const pages = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)))
const when = (iso: string) => formatDate(iso, locale.locale, { dateStyle: 'medium', timeStyle: 'medium' })
const num = (n: number) => new Intl.NumberFormat(locale.locale === 'fa' ? 'fa-IR' : 'en-US').format(n)
const serverSeries = computed(() => summary.value?.days.map((d) => d.server) ?? [])
const clientSeries = computed(() => summary.value?.days.map((d) => d.client) ?? [])
const today = computed(() => summary.value?.days.at(-1))
</script>

<template>
  <section class="page">
    <header class="page-head">
      <h1>{{ t('errorLog.title') }}</h1>
      <span class="actions">
        <label class="auto"><input v-model="auto" type="checkbox" /> {{ t('errorLog.auto') }}</label>
        <Button size="small" text icon="pi pi-refresh" :label="t('status.refresh')" :loading="loading" @click="load" />
        <Button size="small" text severity="danger" icon="pi pi-trash" :label="t('errorLog.purge')" :disabled="total === 0 && !summary?.total" data-testid="purge" @click="purge" />
      </span>
    </header>
    <p class="muted intro">{{ t('errorLog.intro') }}</p>

    <section v-if="summary" class="kpis" :aria-label="t('status.kpis')">
      <article class="kpi" :class="{ bad: (today?.server ?? 0) > 0 }"><h2>{{ t('errorLog.kpi.today') }}</h2><p class="v">{{ num((today?.server ?? 0) + (today?.client ?? 0)) }}</p><p class="d">{{ t('errorLog.kpi.todayHelp', { s: num(today?.server ?? 0), c: num(today?.client ?? 0) }) }}</p></article>
      <article class="kpi"><h2>{{ t('errorLog.kpi.fourteen') }}</h2><p class="v">{{ num(summary.total) }}</p><p class="d">{{ t('errorLog.kpi.todayHelp', { s: num(summary.server), c: num(summary.client) }) }}</p></article>
      <article class="kpi"><h2>{{ t('errorLog.kpi.kinds') }}</h2><p class="v">{{ num(summary.groups.length) }}</p><p class="d">{{ t('errorLog.kpi.kindsHelp') }}</p></article>
      <div class="card chart"><h2>{{ t('errorLog.perDay') }}</h2><MiniCharts :values="serverSeries.map((n, i) => n + (clientSeries[i] ?? 0))" :overlay="serverSeries" :label="t('errorLog.perDayHelp')" /></div>
    </section>

    <section v-if="summary && summary.groups.length" class="card">
      <h2>{{ t('errorLog.groups') }}</h2>
      <table class="log">
        <thead><tr><th scope="col">{{ t('errorLog.col.count') }}</th><th scope="col">{{ t('errorLog.col.error') }}</th><th scope="col">{{ t('errorLog.col.where') }}</th><th scope="col">{{ t('errorLog.col.last') }}</th></tr></thead>
        <tbody>
          <tr v-for="g in summary.groups" :key="g.fingerprint" class="row" tabindex="0" :class="{ on: fingerprint === g.fingerprint }" data-testid="group-row" @click="fingerprint = fingerprint === g.fingerprint ? '' : g.fingerprint" @keydown.enter="fingerprint = fingerprint === g.fingerprint ? '' : g.fingerprint">
            <td class="num"><strong>{{ num(g.count) }}</strong></td>
            <td dir="auto"><span class="src" :class="g.source">{{ g.source }}</span> <code dir="ltr">{{ g.exceptionType ?? '' }}</code> {{ g.message.slice(0, 140) }}</td>
            <td><code dir="ltr">{{ g.path ?? '—' }}</code></td>
            <td class="nowrap">{{ when(g.lastSeen) }}</td>
          </tr>
        </tbody>
      </table>
    </section>

    <div class="filters" role="search">
      <input v-model="q" type="search" class="grow" :placeholder="t('errorLog.search')" :aria-label="t('errorLog.search')" data-testid="error-search" />
      <select v-model="source" :aria-label="t('errorLog.source')"><option value="">{{ t('errorLog.allSources') }}</option><option value="server">{{ t('errorLog.server') }}</option><option value="client">{{ t('errorLog.client') }}</option></select>
      <label>{{ t('activityLog.from') }} <CalendarDateInput v-model="from" /></label>
      <label>{{ t('activityLog.to') }} <CalendarDateInput v-model="to" /></label>
      <button v-if="fingerprint" type="button" class="reset" @click="fingerprint = ''">{{ t('errorLog.clearGroup') }}</button>
      <button v-if="q || source || from || to" type="button" class="reset" @click="((q = ''), (source = ''), (from = ''), (to = ''))">{{ t('activityLog.clear') }}</button>
    </div>
    <p class="muted count" role="status" data-testid="error-count">{{ t('activityLog.found', { n: num(total) }) }}</p>
    <p v-if="failed" class="error" role="alert">{{ t('activityLog.failed') }}</p>

    <div class="scroll" :aria-busy="loading">
      <table class="log" data-testid="error-table">
        <thead><tr><th scope="col">{{ t('activityLog.col.time') }}</th><th scope="col">{{ t('errorLog.source') }}</th><th scope="col">{{ t('errorLog.col.error') }}</th><th scope="col">{{ t('errorLog.col.where') }}</th><th scope="col">{{ t('errorLog.col.workspace') }}</th></tr></thead>
        <tbody>
          <template v-for="e in items" :key="e.id">
            <tr class="row" tabindex="0" :aria-expanded="open === e.id" data-testid="error-row" @click="toggle(e)" @keydown.enter="toggle(e)">
              <td class="nowrap">{{ when(e.occurredAt) }}</td>
              <td><span class="src" :class="e.source">{{ e.source }}</span></td>
              <td dir="auto"><code v-if="e.exceptionType" dir="ltr">{{ e.exceptionType }}</code> {{ e.message.slice(0, 160) }}</td>
              <td><code dir="ltr">{{ e.method ?? '' }} {{ e.path ?? '—' }}</code></td>
              <td><code dir="ltr">{{ e.tenantCode ?? '—' }}</code></td>
            </tr>
            <tr v-if="open === e.id" class="detail"><td colspan="5">
              <dl>
                <dt>{{ t('errorLog.d.message') }}</dt><dd dir="auto">{{ e.message }}</dd>
                <dt>{{ t('errorLog.d.status') }}</dt><dd>{{ e.statusCode ?? '—' }}</dd>
                <dt>{{ t('errorLog.d.correlation') }}</dt><dd><code dir="ltr">{{ e.correlationId ?? '—' }}</code></dd>
                <dt>{{ t('errorLog.d.user') }}</dt><dd><code dir="ltr">{{ e.userId ?? '—' }}</code></dd>
                <dt>{{ t('errorLog.d.same') }}</dt><dd>{{ detail ? num(detail.sameCount) : '…' }}</dd>
                <dt>{{ t('errorLog.d.browser') }}</dt><dd><code dir="ltr">{{ detail?.userAgent ?? '—' }}</code></dd>
              </dl>
              <h3>{{ t('errorLog.d.stack') }}</h3>
              <pre dir="ltr" class="stack">{{ detail?.stackTrace ?? t('common.loading') }}</pre>
            </td></tr>
          </template>
        </tbody>
      </table>
      <p v-if="!loading && total === 0" class="muted empty" data-testid="no-errors">{{ t('errorLog.empty') }}</p>
    </div>

    <nav v-if="pages > 1" class="pager" :aria-label="t('databases.pages')">
      <Button icon="pi pi-angle-left" text rounded :disabled="page === 0" :aria-label="t('databases.prev')" @click="((page = Math.max(0, page - 1)), load())" />
      <span>{{ num(page + 1) }} / {{ num(pages) }}</span>
      <Button icon="pi pi-angle-right" text rounded :disabled="page >= pages - 1" :aria-label="t('databases.next')" @click="((page = Math.min(pages - 1, page + 1)), load())" />
    </nav>
  </section>
</template>

<style scoped>
.page { max-inline-size: 80rem; margin-inline: auto; display: grid; gap: var(--raavi-space-3); }
.page-head { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; }
.actions { display: inline-flex; align-items: center; gap: var(--raavi-space-2); flex-wrap: wrap; }
.auto { font-size: 0.85rem; color: var(--raavi-text-muted); display: inline-flex; gap: 6px; align-items: center; }
h1 { margin: 0; font-size: 1.4rem; }
h2 { margin: 0 0 var(--raavi-space-2); font-size: 0.95rem; }
h3 { margin: var(--raavi-space-3) 0 4px; font-size: 0.85rem; color: var(--raavi-text-muted); }
.intro { margin: 0; }
.kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(11rem, 1fr)); gap: var(--raavi-space-3); }
.kpi, .card { background: var(--raavi-surface); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-md); padding: var(--raavi-space-3) var(--raavi-space-4); min-inline-size: 0; }
.kpi { border-inline-start: 4px solid var(--p-primary-color); }
.kpi.bad { border-inline-start-color: var(--raavi-danger); }
.kpi h2 { font-size: 0.78rem; color: var(--raavi-text-muted); text-transform: uppercase; margin: 0; }
.kpi .v { margin: 2px 0; font-size: 1.6rem; font-weight: 700; font-variant-numeric: tabular-nums; }
.kpi .d { margin: 0; font-size: 0.78rem; color: var(--raavi-text-muted); }
.chart { grid-column: span 2; }
@media (max-width: 640px) { .chart { grid-column: auto; } }
.filters { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.filters input, .filters select { font: inherit; padding: 0.35rem 0.5rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: var(--raavi-surface); color: inherit; }
.filters .grow { flex: 1; min-inline-size: 14rem; }
.filters label { display: inline-flex; gap: 6px; align-items: center; font-size: 0.85rem; color: var(--raavi-text-muted); }
.reset { all: unset; cursor: pointer; color: var(--p-primary-color); text-decoration: underline; font-size: 0.85rem; }
.scroll { overflow-x: auto; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-md); background: var(--raavi-surface); }
.log { inline-size: 100%; border-collapse: collapse; font-size: 0.88rem; }
.log th, .log td { padding: 0.45rem 0.7rem; text-align: start; border-block-end: 1px solid var(--raavi-border); vertical-align: top; }
.log thead th { font-size: 0.76rem; color: var(--raavi-text-muted); text-transform: uppercase; }
.row { cursor: pointer; }
.row:hover, .row:focus-visible, .row.on { background: var(--p-primary-50, rgba(0, 0, 0, 0.05)); outline: none; }
.num { text-align: end; font-variant-numeric: tabular-nums; }
.nowrap { white-space: nowrap; }
.src { display: inline-block; padding: 0 0.5rem; border-radius: 999px; font-size: 0.75rem; font-weight: 600; color: #fff; background: #dc2626; }
.src.client { background: #d97706; }
.detail td { background: var(--raavi-bg); }
.detail dl { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 4px var(--raavi-space-4); margin: 0; font-size: 0.85rem; }
.detail dt { color: var(--raavi-text-muted); }
.detail dd { margin: 0; overflow-wrap: anywhere; }
.stack { margin: 0; padding: var(--raavi-space-2); background: #0f172a; color: #e2e8f0; border-radius: var(--raavi-radius-sm); overflow: auto; max-block-size: 18rem; font-size: 0.78rem; text-align: left; }
code { font-size: 0.8rem; }
.pager { display: flex; gap: var(--raavi-space-2); align-items: center; justify-content: center; }
.muted { color: var(--raavi-text-muted); }
.count { margin: 0; font-size: 0.85rem; }
.empty { padding: var(--raavi-space-4); margin: 0; text-align: center; }
.error { color: var(--raavi-danger); }
</style>
