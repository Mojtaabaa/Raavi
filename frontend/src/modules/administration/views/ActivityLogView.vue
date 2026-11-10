<script setup lang="ts">
import CalendarDateInput from '@/app/CalendarDateInput.vue'
import { formatDate } from '@/core/i18n/locales'
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import { activityApi, type ActivityItem, type SecurityRow } from '@/core/api/activity'
import { useLocaleStore } from '@/stores/locale'
import ActivityChart from '@/modules/wiki/components/ActivityChart.vue'

/**
 * Workspace administrators' log: what happened in the workspace (content and space activity) and what happened to accounts and access
 * (security events). One search box looks in every field; the filters narrow by kind, space and time. Rows open to show everything recorded.
 */
const { t, te } = useI18n()
const locale = useLocaleStore()

const tab = ref<'content' | 'security'>('content')
const q = ref('')
const kind = ref('')
const from = ref('')
const to = ref('')
const pageSize = ref(50)
const page = ref(0)
const total = ref(0)
const content = ref<ActivityItem[]>([])
const security = ref<SecurityRow[]>([])
const kinds = ref<string[]>([])
const types = ref<string[]>([])
const loading = ref(false)
const failed = ref(false)
const open = ref<string | null>(null)

const dayStart = (d: string) => (d ? new Date(d + 'T00:00:00').toISOString() : undefined)
const dayEnd = (d: string) => (d ? new Date(new Date(d + 'T00:00:00').getTime() + 86_400_000).toISOString() : undefined)

let ticket = 0
async function load() {
  const mine = ++ticket
  loading.value = true
  failed.value = false
  try {
    const base = { q: q.value.trim() || undefined, from: dayStart(from.value), to: dayEnd(to.value), skip: page.value * pageSize.value, take: pageSize.value }
    if (tab.value === 'content') {
      const r = await activityApi.log({ ...base, kind: kind.value || undefined })
      if (mine !== ticket) return
      content.value = r.items
      total.value = r.total
      kinds.value = r.kinds
    } else {
      const r = await activityApi.security({ ...base, type: kind.value || undefined })
      if (mine !== ticket) return
      security.value = r.items
      total.value = r.total
      types.value = r.types
    }
  } catch {
    if (mine === ticket) failed.value = true
  } finally {
    if (mine === ticket) loading.value = false
  }
}

let timer: ReturnType<typeof setTimeout> | undefined
watch(q, () => {
  clearTimeout(timer)
  timer = setTimeout(() => {
    page.value = 0
    void load()
  }, 300)
})
watch([kind, from, to, pageSize], () => {
  page.value = 0
  void load()
})
watch(tab, () => {
  kind.value = ''
  page.value = 0
  open.value = null
  void load()
})
onMounted(load)
onBeforeUnmount(() => clearTimeout(timer))

const pages = computed(() => Math.max(1, Math.ceil(total.value / pageSize.value)))
const options = computed(() => (tab.value === 'content' ? kinds.value : types.value))
const when = (iso: string) => formatDate(iso, locale.locale, { dateStyle: 'medium', timeStyle: 'medium' })
const kindLabel = (k: string) => (te(`wiki.activity.${k}`) ? t(`wiki.activity.${k}`) : k)
const num = (n: number) => new Intl.NumberFormat(locale.locale === 'fa' ? 'fa-IR' : 'en-US').format(n)

function csv() {
  const rows: string[][] =
    tab.value === 'content'
      ? [['time', 'kind', 'space', 'title', 'version', 'person', 'actorId'], ...content.value.map((e) => [e.occurredAt, e.kind, e.spaceKey, e.title, String(e.version ?? ''), e.actorName ?? '', e.actorId])]
      : [['time', 'type', 'person', 'actor', 'address', 'details'], ...security.value.map((e) => [e.occurredAt, e.eventType, e.userName ?? e.userId ?? '', e.actorName ?? e.actorId, e.ipAddress ?? '', e.details ?? ''])]
  // a leading = + - @ would be a formula in a spreadsheet: neutralised like the server's CSV export
  const cell = (v: string) => `"${(/^[=+\-@\t\r]/.test(v) ? `'${v}` : v).replace(/"/g, '""')}"`
  const blob = new Blob(['﻿' + rows.map((r) => r.map(cell).join(',')).join('\r\n')], { type: 'text/csv;charset=utf-8' })
  const a = document.createElement('a')
  a.href = URL.createObjectURL(blob)
  a.download = `raavi-${tab.value}-log.csv`
  a.click()
  setTimeout(() => URL.revokeObjectURL(a.href), 2000)
}
</script>

<template>
  <section class="page">
    <header class="page-head">
      <h1>{{ t('activityLog.title') }}</h1>
      <Button size="small" text icon="pi pi-download" :label="t('activityLog.exportPage')" :disabled="total === 0" data-testid="log-export" @click="csv" />
    </header>

    <ActivityChart />

    <div class="tabs" role="tablist" :aria-label="t('activityLog.title')">
      <button type="button" role="tab" :aria-selected="tab === 'content'" :class="{ on: tab === 'content' }" data-testid="tab-content" @click="tab = 'content'">{{ t('activityLog.content') }}</button>
      <button type="button" role="tab" :aria-selected="tab === 'security'" :class="{ on: tab === 'security' }" data-testid="tab-security" @click="tab = 'security'">{{ t('activityLog.security') }}</button>
    </div>

    <div class="filters" role="search">
      <input v-model="q" type="search" class="grow" :placeholder="t('activityLog.search')" :aria-label="t('activityLog.search')" data-testid="log-search" />
      <select v-model="kind" :aria-label="t('activityLog.kind')" data-testid="log-kind">
        <option value="">{{ t('activityLog.allKinds') }}</option>
        <option v-for="k in options" :key="k" :value="k">{{ tab === 'content' ? kindLabel(k) : k }}</option>
      </select>
      <label>{{ t('activityLog.from') }} <CalendarDateInput v-model="from" /></label>
      <label>{{ t('activityLog.to') }} <CalendarDateInput v-model="to" /></label>
      <button v-if="q || kind || from || to" type="button" class="reset" @click="((q = ''), (kind = ''), (from = ''), (to = ''))">{{ t('activityLog.clear') }}</button>
    </div>
    <p class="count muted" role="status" data-testid="log-count">{{ t('activityLog.found', { n: num(total) }) }}</p>

    <p v-if="failed" class="error" role="alert">{{ t('activityLog.failed') }}</p>
    <div class="scroll" :aria-busy="loading">
      <table v-if="tab === 'content'" class="log" data-testid="content-log">
        <thead><tr><th scope="col">{{ t('activityLog.col.time') }}</th><th scope="col">{{ t('activityLog.col.person') }}</th><th scope="col">{{ t('activityLog.col.action') }}</th><th scope="col">{{ t('activityLog.col.target') }}</th><th scope="col">{{ t('activityLog.col.space') }}</th></tr></thead>
        <tbody>
          <template v-for="e in content" :key="e.id">
            <tr class="row" tabindex="0" :aria-expanded="open === e.id" data-testid="log-row" @click="open = open === e.id ? null : e.id" @keydown.enter="open = open === e.id ? null : e.id">
              <td class="nowrap">{{ when(e.occurredAt) }}</td>
              <td dir="auto">{{ e.actorName ?? '—' }}</td>
              <td><span class="kind">{{ kindLabel(e.kind) }}</span></td>
              <td dir="auto"><RouterLink v-if="e.contentId" :to="{ name: 'page', params: { key: e.spaceKey, id: e.contentId } }" @click.stop>{{ e.title }}</RouterLink><template v-else>{{ e.title }}</template></td>
              <td><code dir="ltr">{{ e.spaceKey }}</code></td>
            </tr>
            <tr v-if="open === e.id" class="detail"><td colspan="5">
              <dl>
                <dt>{{ t('activityLog.detail.kind') }}</dt><dd><code dir="ltr">{{ e.kind }}</code></dd>
                <dt>{{ t('activityLog.detail.version') }}</dt><dd>{{ e.version ?? '—' }}</dd>
                <dt>{{ t('activityLog.detail.person') }}</dt><dd><code dir="ltr">{{ e.actorId }}</code></dd>
                <dt>{{ t('activityLog.detail.content') }}</dt><dd><code dir="ltr">{{ e.contentId ?? '—' }}</code></dd>
                <dt>{{ t('activityLog.detail.event') }}</dt><dd><code dir="ltr">{{ e.id }}</code></dd>
              </dl>
            </td></tr>
          </template>
        </tbody>
      </table>
      <table v-else class="log" data-testid="security-log">
        <thead><tr><th scope="col">{{ t('activityLog.col.time') }}</th><th scope="col">{{ t('activityLog.col.type') }}</th><th scope="col">{{ t('activityLog.col.person') }}</th><th scope="col">{{ t('activityLog.col.by') }}</th><th scope="col">{{ t('activityLog.col.address') }}</th></tr></thead>
        <tbody>
          <template v-for="(e, i) in security" :key="i">
            <tr class="row" tabindex="0" :aria-expanded="open === String(i)" data-testid="log-row" @click="open = open === String(i) ? null : String(i)" @keydown.enter="open = open === String(i) ? null : String(i)">
              <td class="nowrap">{{ when(e.occurredAt) }}</td>
              <td><span class="kind">{{ e.eventType }}</span></td>
              <td dir="auto">{{ e.userName ?? '—' }}</td>
              <td dir="auto">{{ e.actorName ?? e.actorId }}</td>
              <td><code dir="ltr">{{ e.ipAddress ?? '—' }}</code></td>
            </tr>
            <tr v-if="open === String(i)" class="detail"><td colspan="5">
              <dl>
                <dt>{{ t('activityLog.detail.details') }}</dt><dd><code dir="ltr" class="wrap">{{ e.details ?? '—' }}</code></dd>
                <dt>{{ t('activityLog.detail.person') }}</dt><dd><code dir="ltr">{{ e.userId ?? '—' }}</code></dd>
                <dt>{{ t('activityLog.detail.actor') }}</dt><dd><code dir="ltr">{{ e.actorId }}</code></dd>
              </dl>
            </td></tr>
          </template>
        </tbody>
      </table>
      <p v-if="!loading && total === 0" class="muted empty">{{ t('activityLog.empty') }}</p>
    </div>

    <nav v-if="pages > 1" class="pager" :aria-label="t('databases.pages')">
      <Button icon="pi pi-angle-left" text rounded :disabled="page === 0" :aria-label="t('databases.prev')" @click="((page = Math.max(0, page - 1)), load())" />
      <span>{{ num(page + 1) }} / {{ num(pages) }}</span>
      <Button icon="pi pi-angle-right" text rounded :disabled="page >= pages - 1" :aria-label="t('databases.next')" @click="((page = Math.min(pages - 1, page + 1)), load())" />
      <select v-model.number="pageSize" :aria-label="t('activityLog.perPage')"><option :value="25">25</option><option :value="50">50</option><option :value="100">100</option><option :value="200">200</option></select>
    </nav>
  </section>
</template>

<style scoped>
.page { max-inline-size: 80rem; margin-inline: auto; display: grid; gap: var(--raavi-space-3); }
.page-head { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; }
h1 { margin: 0; font-size: 1.4rem; }
.tabs { display: flex; gap: 2px; border-block-end: 1px solid var(--raavi-border); }
.tabs button { font: inherit; border: 0; background: transparent; color: var(--raavi-text-muted); padding: 0.5rem 1rem; cursor: pointer; border-block-end: 3px solid transparent; }
.tabs button.on { color: var(--p-primary-color); border-block-end-color: var(--p-primary-color); font-weight: 600; }
.filters { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.filters input, .filters select, .pager select { font: inherit; padding: 0.35rem 0.5rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: var(--raavi-surface); color: inherit; }
.filters .grow { flex: 1; min-inline-size: 14rem; }
.filters label { display: inline-flex; gap: 6px; align-items: center; font-size: 0.85rem; color: var(--raavi-text-muted); }
.reset { all: unset; cursor: pointer; color: var(--p-primary-color); text-decoration: underline; font-size: 0.85rem; }
.count { margin: 0; font-size: 0.85rem; }
.scroll { overflow-x: auto; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-md); background: var(--raavi-surface); }
.log { inline-size: 100%; border-collapse: collapse; font-size: 0.9rem; }
.log th, .log td { padding: 0.5rem 0.75rem; text-align: start; border-block-end: 1px solid var(--raavi-border); vertical-align: top; }
.log thead th { position: sticky; inset-block-start: 0; background: var(--raavi-surface); font-size: 0.78rem; color: var(--raavi-text-muted); text-transform: uppercase; }
.row { cursor: pointer; }
.row:hover, .row:focus-visible { background: var(--p-primary-50, rgba(0, 0, 0, 0.04)); outline: none; }
.row[aria-expanded='true'] { background: var(--p-primary-50, rgba(0, 0, 0, 0.05)); }
.nowrap { white-space: nowrap; }
.kind { display: inline-block; padding: 0.05rem 0.55rem; border-radius: 999px; background: var(--raavi-bg); border: 1px solid var(--raavi-border); font-size: 0.82rem; }
.detail td { background: var(--raavi-bg); }
.detail dl { display: grid; grid-template-columns: max-content minmax(0, 1fr); gap: 4px var(--raavi-space-4); margin: 0; font-size: 0.85rem; }
.detail dt { color: var(--raavi-text-muted); }
.detail dd { margin: 0; overflow-wrap: anywhere; }
.wrap { white-space: pre-wrap; }
code { font-size: 0.82rem; }
.pager { display: flex; gap: var(--raavi-space-2); align-items: center; justify-content: center; }
.muted { color: var(--raavi-text-muted); }
.empty { padding: var(--raavi-space-4); margin: 0; text-align: center; }
.error { color: var(--raavi-danger); }
</style>
