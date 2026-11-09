<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { activityApi, type ActivityStats } from '@/core/api/activity'
import { useLocaleStore } from '@/stores/locale'
import UserAvatar from '@/app/UserAvatar.vue'

/**
 * Activity overview: what was done (by kind of work), when (per day and per hour) and by whom. Counts only, from the spaces and pages the
 * viewer may see. Hand-drawn SVG (no chart library): colours are CSS variables, every chart has a text alternative and a table view.
 */
const props = withDefaults(defineProps<{ compact?: boolean }>(), { compact: false })
const { t, te } = useI18n()
const locale = useLocaleStore()

const RANGES = [7, 30, 90] as const
const days = ref<number>(30)
const stats = ref<ActivityStats | null>(null)
const failed = ref(false)
const showTable = ref(false)

/** Kinds of work are grouped, so the chart reads as "created / edited / published / reorganised / access". */
const GROUPS: { id: string; kinds: (k: string) => boolean; color: string }[] = [
  { id: 'created', kinds: (k) => k === 'content.created' || k === 'space.created', color: '#16a34a' },
  { id: 'edited', kinds: (k) => k === 'content.updated' || k === 'space.updated', color: '#2563eb' },
  { id: 'published', kinds: (k) => k.startsWith('content.publish'), color: '#9333ea' },
  { id: 'organised', kinds: (k) => ['content.moved', 'content.archived', 'content.restored', 'content.trashed', 'content.purged', 'space.archived', 'space.restored'].includes(k), color: '#d97706' },
  { id: 'access', kinds: (k) => k.endsWith('restrictions_changed') || k.endsWith('permissions_changed'), color: '#dc2626' },
]
const groupOf = (k: string) => GROUPS.find((g) => g.kinds(k))?.id ?? 'other'
const OTHER = { id: 'other', color: '#6b7280' }
const all = [...GROUPS.map((g) => ({ id: g.id, color: g.color })), OTHER]
const hidden = ref<string[]>([])

async function load() {
  failed.value = false
  try {
    stats.value = await activityApi.stats(days.value)
  } catch {
    failed.value = true
  }
}
onMounted(load)
watch(days, load)

const rows = computed(() =>
  (stats.value?.days ?? []).map((d) => {
    const by: Record<string, number> = {}
    for (const [k, n] of Object.entries(d.byKind)) by[groupOf(k)] = (by[groupOf(k)] ?? 0) + n
    const visible = all.filter((g) => !hidden.value.includes(g.id))
    return { date: d.date, by, total: visible.reduce((s, g) => s + (by[g.id] ?? 0), 0) }
  }),
)
const max = computed(() => Math.max(1, ...rows.value.map((r) => r.total)))
const W = 600
const H = props.compact ? 120 : 170
const step = computed(() => W / Math.max(1, rows.value.length))
const bars = computed(() =>
  rows.value.map((r, i) => {
    let y = H - 18
    const parts = all
      .filter((g) => !hidden.value.includes(g.id) && (r.by[g.id] ?? 0) > 0)
      .map((g) => {
        const h = ((r.by[g.id] ?? 0) / max.value) * (H - 30)
        y -= h
        return { id: g.id, color: g.color, y, h }
      })
    return { x: i * step.value + step.value * 0.15, w: step.value * 0.7, parts, date: r.date, total: r.total }
  }),
)
const groupTotals = computed(() => {
  const totals: Record<string, number> = {}
  for (const [k, n] of Object.entries(stats.value?.byKind ?? {})) totals[groupOf(k)] = (totals[groupOf(k)] ?? 0) + n
  return all.map((g) => ({ ...g, total: totals[g.id] ?? 0 })).filter((g) => g.total > 0)
})
const total = computed(() => groupTotals.value.reduce((s, g) => s + g.total, 0))
const hourMax = computed(() => Math.max(1, ...(stats.value?.byHour ?? [0])))
const topMax = computed(() => Math.max(1, ...(stats.value?.topActors ?? []).map((a) => a.count)))

const fmtDay = (iso: string) => new Intl.DateTimeFormat(locale.locale === 'fa' ? 'fa-IR' : 'en-GB', { month: 'short', day: 'numeric' }).format(new Date(iso + 'T00:00:00Z'))
const num = (n: number) => new Intl.NumberFormat(locale.locale === 'fa' ? 'fa-IR' : 'en-US').format(n)
const label = (id: string) => (te(`activityChart.groups.${id}`) ? t(`activityChart.groups.${id}`) : id)
const tickEvery = computed(() => (rows.value.length > 45 ? 14 : rows.value.length > 14 ? 5 : 1))
const toggle = (id: string) => (hidden.value = hidden.value.includes(id) ? hidden.value.filter((x) => x !== id) : [...hidden.value, id])
const peakHour = computed(() => (stats.value ? stats.value.byHour.indexOf(Math.max(...stats.value.byHour)) : -1))
</script>

<template>
  <section class="chart-card" :aria-label="t('activityChart.title')" data-testid="activity-chart">
    <header class="head">
      <h2>{{ t('activityChart.title') }}</h2>
      <span class="spacer" />
      <span class="seg" role="group" :aria-label="t('activityChart.range')">
        <button v-for="r in RANGES" :key="r" type="button" :class="{ on: days === r }" :aria-pressed="days === r" @click="days = r">{{ t('activityChart.days', { n: r }) }}</button>
      </span>
    </header>

    <p v-if="failed" class="muted" role="alert">{{ t('activityChart.failed') }}</p>
    <p v-else-if="!stats" class="muted">{{ t('common.loading') }}</p>
    <p v-else-if="total === 0" class="muted" data-testid="activity-empty">{{ t('activityChart.none') }}</p>
    <template v-else>
      <div class="kpis">
        <div class="kpi"><span class="v">{{ num(stats.total) }}</span><span class="d">{{ t('activityChart.total', { n: days }) }}</span></div>
        <div class="kpi"><span class="v">{{ num(Math.round(stats.total / days * 10) / 10) }}</span><span class="d">{{ t('activityChart.perDay') }}</span></div>
        <div class="kpi"><span class="v">{{ stats.topActors.length }}</span><span class="d">{{ t('activityChart.people') }}</span></div>
        <div class="kpi"><span class="v">{{ peakHour >= 0 ? `${String(peakHour).padStart(2, '0')}:00` : '—' }}</span><span class="d">{{ t('activityChart.peakHour') }}</span></div>
      </div>

      <div class="legend" role="group" :aria-label="t('activityChart.legend')">
        <button v-for="g in groupTotals" :key="g.id" type="button" class="chip" :class="{ off: hidden.includes(g.id) }" :aria-pressed="!hidden.includes(g.id)" @click="toggle(g.id)">
          <span class="dot" :style="{ background: g.color }" />{{ label(g.id) }} <strong>{{ num(g.total) }}</strong>
        </button>
      </div>

      <svg v-if="!showTable" :viewBox="`0 0 ${W} ${H}`" preserveAspectRatio="none" class="bars" role="img" :aria-label="t('activityChart.alt', { total: stats.total, n: days })">
        <line x1="0" :x2="W" :y1="H - 18" :y2="H - 18" class="axis" />
        <g v-for="(b, i) in bars" :key="b.date">
          <rect v-for="p in b.parts" :key="p.id" :x="b.x" :y="p.y" :width="b.w" :height="p.h" :fill="p.color" rx="1.5"><title>{{ fmtDay(b.date) }} · {{ label(p.id) }}</title></rect>
          <rect :x="b.x" :y="0" :width="b.w" :height="H - 18" fill="transparent"><title>{{ fmtDay(b.date) }}: {{ b.total }}</title></rect>
          <text v-if="i % tickEvery === 0" :x="b.x + b.w / 2" :y="H - 4" class="tick" text-anchor="middle">{{ fmtDay(b.date) }}</text>
        </g>
      </svg>
      <div v-else class="tablewrap">
        <table class="mini">
          <thead><tr><th scope="col">{{ t('activityChart.date') }}</th><th v-for="g in groupTotals" :key="g.id" scope="col">{{ label(g.id) }}</th></tr></thead>
          <tbody><tr v-for="r in rows.filter((x) => x.total > 0)" :key="r.date"><th scope="row">{{ fmtDay(r.date) }}</th><td v-for="g in groupTotals" :key="g.id">{{ num(r.by[g.id] ?? 0) }}</td></tr></tbody>
        </table>
      </div>
      <button type="button" class="linklike" :aria-pressed="showTable" @click="showTable = !showTable">{{ showTable ? t('activityChart.showChart') : t('activityChart.showTable') }}</button>

      <div v-if="!compact" class="two">
        <div>
          <h3>{{ t('activityChart.byHour') }}</h3>
          <div class="hours" role="img" :aria-label="t('activityChart.byHourAlt')">
            <span v-for="(n, h) in stats.byHour" :key="h" class="hour" :style="{ blockSize: `${Math.max(3, (n / hourMax) * 100)}%`, opacity: n ? 1 : 0.25 }" :title="`${String(h).padStart(2, '0')}:00 — ${n}`" />
          </div>
          <div class="hours-axis"><span>00</span><span>06</span><span>12</span><span>18</span><span>23</span></div>
        </div>
        <div>
          <h3>{{ t('activityChart.topPeople') }}</h3>
          <ol class="people">
            <li v-for="a in stats.topActors" :key="a.actorId">
              <UserAvatar :user-id="a.actorId" :name="a.name" :size="22" />
              <span class="n" dir="auto">{{ a.name ?? '—' }}</span>
              <span class="bar"><i :style="{ inlineSize: `${(a.count / topMax) * 100}%` }" /></span>
              <span class="c">{{ num(a.count) }}</span>
            </li>
          </ol>
        </div>
      </div>
    </template>
  </section>
</template>

<style scoped>
.chart-card { background: var(--raavi-surface); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-md); padding: var(--raavi-space-4); display: grid; gap: var(--raavi-space-3); min-inline-size: 0; }
.head { display: flex; align-items: center; gap: var(--raavi-space-2); flex-wrap: wrap; }
.head h2 { margin: 0; font-size: 1.05rem; }
.spacer { flex: 1; }
.seg { display: inline-flex; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); overflow: hidden; }
.seg button { font: inherit; font-size: 0.85rem; border: 0; background: transparent; color: var(--raavi-text-muted); padding: 0.2rem 0.65rem; cursor: pointer; }
.seg button.on { background: var(--p-primary-color); color: var(--p-primary-contrast-color, #fff); }
.kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(8rem, 1fr)); gap: var(--raavi-space-2); }
.kpi { display: grid; padding: var(--raavi-space-2) var(--raavi-space-3); background: var(--raavi-bg); border-radius: var(--raavi-radius-sm); }
.kpi .v { font-size: 1.35rem; font-weight: 700; font-variant-numeric: tabular-nums; }
.kpi .d { font-size: 0.78rem; color: var(--raavi-text-muted); }
.legend { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.chip { display: inline-flex; align-items: center; gap: 6px; font: inherit; font-size: 0.82rem; padding: 0.15rem 0.6rem; border: 1px solid var(--raavi-border); background: var(--raavi-surface); color: var(--raavi-text); border-radius: 999px; cursor: pointer; }
.chip.off { opacity: 0.45; text-decoration: line-through; }
.dot { inline-size: 0.7rem; block-size: 0.7rem; border-radius: 50%; display: inline-block; }
.bars { inline-size: 100%; block-size: 11rem; background: var(--raavi-bg); border-radius: var(--raavi-radius-sm); }
.axis { stroke: var(--raavi-border); }
.tick { fill: var(--raavi-text-muted); font-size: 9px; }
.tablewrap { overflow: auto; max-block-size: 14rem; }
.mini { inline-size: 100%; border-collapse: collapse; font-size: 0.85rem; }
.mini th, .mini td { padding: 3px 8px; text-align: start; border-block-end: 1px solid var(--raavi-border); font-variant-numeric: tabular-nums; }
.linklike { all: unset; cursor: pointer; font-size: 0.82rem; color: var(--p-primary-color); text-decoration: underline; justify-self: start; }
.linklike:focus-visible { outline: 2px solid var(--p-primary-color); }
.two { display: grid; grid-template-columns: repeat(auto-fit, minmax(16rem, 1fr)); gap: var(--raavi-space-4); }
h3 { margin: 0 0 var(--raavi-space-2); font-size: 0.85rem; color: var(--raavi-text-muted); font-weight: 600; }
.hours { display: flex; align-items: flex-end; gap: 2px; block-size: 5rem; }
.hour { flex: 1; background: var(--p-primary-color); border-radius: 2px 2px 0 0; }
.hours-axis { display: flex; justify-content: space-between; font-size: 0.7rem; color: var(--raavi-text-muted); }
.people { list-style: none; margin: 0; padding: 0; display: grid; gap: 6px; }
.people li { display: grid; grid-template-columns: auto minmax(0, 8rem) 1fr auto; align-items: center; gap: var(--raavi-space-2); font-size: 0.88rem; }
.people .n { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.people .bar { block-size: 6px; background: var(--raavi-bg); border-radius: 3px; overflow: hidden; }
.people .bar i { display: block; block-size: 100%; background: var(--p-primary-color); }
.people .c { font-variant-numeric: tabular-nums; color: var(--raavi-text-muted); }
.muted { color: var(--raavi-text-muted); margin: 0; }
</style>
