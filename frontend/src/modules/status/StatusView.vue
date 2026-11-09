<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import BrandLogo from '@/app/BrandLogo.vue'
import MiniCharts from './MiniCharts.vue'
import { formatDuration, probe, reportText, type Health, type Probe, type StatusReport } from './statusApi'
import { useLocaleStore } from '@/stores/locale'
import { useHostStore } from '@/stores/host'
import { SUPPORTED_LOCALES, LOCALES } from '@/core/i18n/locales'

/**
 * The status page: one glance tells whether Raavi works. It is a public page of the SPA (served by the web site, so it loads even when the
 * API is down) that polls `/health/status`. When the answer cannot be had, it says so loudly and says WHY (no network / web server cannot reach
 * the API / not the API / switched off / too slow) with what to check, instead of showing stale numbers as if they were live.
 */
const { t, te } = useI18n()
const locale = useLocaleStore()
const host = useHostStore()

const INTERVALS = [5, 15, 30, 60] as const
const interval = ref<number>(5)
const paused = ref(false)
const current = ref<Probe | null>(null)
const last = ref<StatusReport | null>(null) // last good report (kept, but marked stale while the API is unreachable)
const lastOkAt = ref<Date | null>(null)
const downSince = ref<Date | null>(null)
const failures = ref(0)
const checks = ref(0)
const okChecks = ref(0)
const roundTrips = ref<number[]>([])
const loading = ref(false)
const now = ref(Date.now())
const copied = ref(false)
let timer: ReturnType<typeof setTimeout> | undefined
let clock: ReturnType<typeof setInterval> | undefined

async function refresh() {
  if (loading.value) return
  loading.value = true
  const p = await probe()
  loading.value = false
  current.value = p
  checks.value++
  roundTrips.value = [...roundTrips.value, p.roundTripMs].slice(-60)
  if (p.ok) {
    okChecks.value++
    failures.value = 0
    downSince.value = null
    last.value = p.report
    lastOkAt.value = new Date()
    // The app could not start because the API was down; now it answers: start it again from the beginning.
    if (host.context?.kind === 'unavailable') setTimeout(() => window.location.assign('/'), 1500)
  } else {
    failures.value++
    downSince.value ??= new Date()
  }
  schedule()
}

function schedule() {
  clearTimeout(timer)
  if (paused.value) return
  // While unreachable, ask a little more often (the page should notice the recovery fast), but never faster than every 3 s.
  const seconds = failures.value > 0 ? Math.max(3, Math.min(interval.value, 10)) : interval.value
  timer = setTimeout(refresh, seconds * 1000)
}

watch([interval, paused], schedule)
onMounted(() => {
  void refresh()
  clock = setInterval(() => (now.value = Date.now()), 1000)
})
onBeforeUnmount(() => {
  clearTimeout(timer)
  clearInterval(clock)
})

const unreachable = computed(() => (current.value && !current.value.ok ? current.value : null))
const report = computed(() => (current.value?.ok ? current.value.report : null))
const overall = computed<Health | 'Unreachable' | 'Checking'>(() => (unreachable.value ? 'Unreachable' : report.value ? report.value.status : 'Checking'))
const uptime = computed(() => {
  const r = report.value
  const units = { d: t('status.units.d'), h: t('status.units.h'), m: t('status.units.m'), s: t('status.units.s') }
  return r ? formatDuration(r.uptimeSeconds + Math.max(0, Math.round((now.value - new Date(r.checkedAt).getTime()) / 1000)), units) : '—'
})
const downFor = computed(() => (downSince.value ? formatDuration(Math.max(0, Math.round((now.value - downSince.value.getTime()) / 1000)), { d: t('status.units.d'), h: t('status.units.h'), m: t('status.units.m'), s: t('status.units.s') }) : ''))
const agoLastOk = computed(() => (lastOkAt.value ? formatDuration(Math.max(0, Math.round((now.value - lastOkAt.value.getTime()) / 1000)), { d: t('status.units.d'), h: t('status.units.h'), m: t('status.units.m'), s: t('status.units.s') }) : ''))
const availability = computed(() => (checks.value ? Math.round((1000 * okChecks.value) / checks.value) / 10 : null))
const num = (n: number | undefined, digits = 0) => (n === undefined ? '—' : new Intl.NumberFormat(locale.locale === 'fa' ? 'fa-IR' : 'en-US', { maximumFractionDigits: digits }).format(n))

const series = computed(() => last.value?.traffic.series ?? [])
const reqPerMin = computed(() => series.value.map((p) => p.requests))
const errPerMin = computed(() => series.value.map((p) => p.errors))
const avgPerMin = computed(() => series.value.map((p) => p.averageMs))

const componentName = (n: string) => (te(`status.components.${n}`) ? t(`status.components.${n}`) : n)
const componentHint = (n: string, s: Health) => (s !== 'Healthy' && te(`status.fix.${n}`) ? t(`status.fix.${n}`) : '')
const severityClass = (s: string) => `st-${s.toLowerCase()}`

watch(overall, (o) => {
  document.title = `${t(`status.overall.${o}`)} · ${t('status.title')}`
}, { immediate: true })

async function copyReport() {
  if (!current.value) return
  try {
    await navigator.clipboard.writeText(reportText(current.value, new Date()))
    copied.value = true
    setTimeout(() => (copied.value = false), 2500)
  } catch {
    /* clipboard blocked: nothing else to do */
  }
}
const setLocale = (code: (typeof SUPPORTED_LOCALES)[number]) => locale.setLocale(code)
</script>

<template>
  <div class="status-page" data-testid="status-page">
    <header class="bar">
      <RouterLink to="/" class="home" :aria-label="t('status.backHome')"><BrandLogo icon :size="28" /> <span>{{ t('status.title') }}</span></RouterLink>
      <span class="spacer" />
      <span class="seg" role="group" :aria-label="t('shell.language')">
        <button v-for="code in SUPPORTED_LOCALES" :key="code" type="button" :class="{ on: locale.locale === code }" :aria-pressed="locale.locale === code" @click="setLocale(code)">{{ LOCALES[code].nativeName }}</button>
      </span>
    </header>

    <main class="body">
      <!-- The one-line answer. role=status so a screen reader hears the change. -->
      <section class="hero" :class="severityClass(overall)" role="status" aria-live="polite" data-testid="overall">
        <span class="orb" aria-hidden="true"><i :class="['pi', overall === 'Healthy' ? 'pi-check-circle' : overall === 'Checking' ? 'pi-spin pi-spinner' : overall === 'Degraded' ? 'pi-exclamation-triangle' : 'pi-times-circle']" /></span>
        <div>
          <h1>{{ t(`status.overall.${overall}`) }}</h1>
          <p class="sub">{{ t(`status.overallHelp.${overall}`) }}</p>
        </div>
        <div class="hero-side">
          <span class="muted">{{ t('status.lastCheck') }}: <strong>{{ lastOkAt ? lastOkAt.toLocaleTimeString(locale.locale === 'fa' ? 'fa-IR' : 'en-GB') : '—' }}</strong></span>
        </div>
      </section>

      <!-- Cannot reach the API: loud, specific, actionable. -->
      <section v-if="unreachable" class="down" role="alert" data-testid="unreachable">
        <h2><i class="pi pi-exclamation-circle" aria-hidden="true" /> {{ t('status.unreachable.title') }}</h2>
        <p class="why" data-testid="unreachable-reason">{{ t(`status.unreachable.${unreachable.reason}.what`) }}</p>
        <p class="muted">{{ t('status.unreachable.since', { time: downFor, tries: failures }) }}<template v-if="lastOkAt"> · {{ t('status.unreachable.lastOk', { time: agoLastOk }) }}</template></p>
        <h3>{{ t('status.unreachable.check') }}</h3>
        <ol>
          <li v-for="n in 4" :key="n" v-show="te(`status.unreachable.${unreachable.reason}.${n}`)">{{ te(`status.unreachable.${unreachable.reason}.${n}`) ? t(`status.unreachable.${unreachable.reason}.${n}`) : '' }}</li>
        </ol>
        <p class="muted small">{{ t('status.unreachable.stale') }}</p>
      </section>

      <div class="toolbar">
        <Button size="small" icon="pi pi-refresh" :label="t('status.refresh')" :loading="loading" data-testid="refresh" @click="refresh" />
        <Button size="small" outlined :icon="paused ? 'pi pi-play' : 'pi pi-pause'" :label="paused ? t('status.resume') : t('status.pause')" :aria-pressed="paused" @click="paused = !paused" />
        <label class="interval">{{ t('status.every') }}
          <select v-model.number="interval" :disabled="paused" :aria-label="t('status.every')">
            <option v-for="s in INTERVALS" :key="s" :value="s">{{ s }} {{ t('status.units.s') }}</option>
          </select>
        </label>
        <span class="spacer" />
        <Button size="small" text icon="pi pi-copy" :label="copied ? t('status.copied') : t('status.copy')" @click="copyReport" />
      </div>

      <!-- KPIs -->
      <section class="kpis" :class="{ stale: !!unreachable }" :aria-label="t('status.kpis')">
        <article class="kpi">
          <h2>{{ t('status.kpi.availability') }}</h2>
          <p class="v">{{ availability === null ? '—' : `${num(availability, 1)}%` }}</p>
          <p class="d">{{ t('status.kpi.availabilityHelp', { ok: num(okChecks), all: num(checks) }) }}</p>
        </article>
        <article class="kpi">
          <h2>{{ t('status.kpi.roundTrip') }}</h2>
          <p class="v">{{ current ? `${num(current.roundTripMs)} ms` : '—' }}</p>
          <p class="d">{{ t('status.kpi.roundTripHelp') }}</p>
        </article>
        <article class="kpi">
          <h2>{{ t('status.kpi.uptime') }}</h2>
          <p class="v">{{ uptime }}</p>
          <p class="d">{{ t('status.kpi.version') }} <code dir="ltr">{{ report?.version ?? last?.version ?? '—' }}</code></p>
        </article>
        <article class="kpi">
          <h2>{{ t('status.kpi.requests') }}</h2>
          <p class="v">{{ num(last?.traffic.requests) }}</p>
          <p class="d">{{ t('status.kpi.requestsHelp') }}</p>
        </article>
        <article class="kpi" :class="{ warn: (last?.traffic.errorRatePercent ?? 0) >= 1, bad: (last?.traffic.errorRatePercent ?? 0) >= 5 }">
          <h2>{{ t('status.kpi.errors') }}</h2>
          <p class="v">{{ last ? `${num(last.traffic.errorRatePercent, 2)}%` : '—' }}</p>
          <p class="d">{{ t('status.kpi.errorsHelp', { count: num(last?.traffic.serverErrors) }) }}</p>
        </article>
        <article class="kpi" :class="{ warn: (last?.traffic.p95Ms ?? 0) >= 1000, bad: (last?.traffic.p95Ms ?? 0) >= 2500 }">
          <h2>{{ t('status.kpi.p95') }}</h2>
          <p class="v">{{ last ? `${num(last.traffic.p95Ms)} ms` : '—' }}</p>
          <p class="d">{{ t('status.kpi.p95Help', { avg: num(last?.traffic.averageMs) }) }}</p>
        </article>
        <article class="kpi">
          <h2>{{ t('status.kpi.memory') }}</h2>
          <p class="v">{{ last ? `${num(last.process.memoryMb)} MB` : '—' }}</p>
          <p class="d">{{ t('status.kpi.memoryHelp', { threads: num(last?.process.threads) }) }}</p>
        </article>
      </section>

      <!-- Charts -->
      <section class="charts" :class="{ stale: !!unreachable }" :aria-label="t('status.charts')">
        <div class="card">
          <h2>{{ t('status.chart.requests') }}</h2>
          <MiniCharts :values="reqPerMin" :overlay="errPerMin" :label="t('status.chart.requestsHelp')" />
        </div>
        <div class="card">
          <h2>{{ t('status.chart.latency') }}</h2>
          <MiniCharts :values="avgPerMin" kind="line" :label="t('status.chart.latencyHelp')" unit=" ms" color="var(--raavi-success)" />
        </div>
        <div class="card">
          <h2>{{ t('status.chart.roundTrip') }}</h2>
          <MiniCharts :values="roundTrips" kind="line" :label="t('status.chart.roundTripHelp')" unit=" ms" color="var(--raavi-icon, var(--p-primary-color))" />
        </div>
      </section>

      <!-- Components -->
      <section class="card comps" :class="{ stale: !!unreachable }">
        <h2>{{ t('status.components.title') }}</h2>
        <p v-if="!last" class="muted">{{ t('status.noData') }}</p>
        <table v-else class="table">
          <thead><tr><th scope="col">{{ t('status.col.component') }}</th><th scope="col">{{ t('status.col.state') }}</th><th scope="col">{{ t('status.col.time') }}</th><th scope="col">{{ t('status.col.detail') }}</th></tr></thead>
          <tbody>
            <tr v-for="c in last.components" :key="c.name" data-testid="component">
              <th scope="row">{{ componentName(c.name) }}</th>
              <td><span class="badge" :class="severityClass(c.status)">{{ t(`status.state.${c.status}`) }}</span></td>
              <td class="num">{{ num(c.durationMs) }} ms</td>
              <td>
                <span>{{ c.description ?? '' }}</span>
                <span v-if="componentHint(c.name, c.status)" class="hint"> — {{ componentHint(c.name, c.status) }}</span>
                <span v-if="c.name === 'disk' && c.data" class="muted"> ({{ num(Number(c.data.freeGb), 1) }} GB · {{ num(Number(c.data.freePercent), 1) }}%)</span>
              </td>
            </tr>
          </tbody>
        </table>
      </section>

      <footer class="foot muted small">
        <span>{{ t('status.footer', { env: last?.environment ?? '—' }) }}</span>
        <a href="/help/fa/" target="_blank" rel="noopener">{{ t('help.open') }}</a>
      </footer>
    </main>
  </div>
</template>

<style scoped>
.status-page { min-block-size: 100vh; background: var(--raavi-bg); }
.bar { display: flex; align-items: center; gap: var(--raavi-space-3); padding: var(--raavi-space-2) var(--raavi-space-4); background: var(--raavi-surface); border-block-end: 1px solid var(--raavi-border); }
.home { display: inline-flex; align-items: center; gap: var(--raavi-space-2); font-weight: 700; color: inherit; text-decoration: none; }
.spacer { flex: 1; }
.seg { display: inline-flex; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); overflow: hidden; }
.seg button { font: inherit; border: 0; background: transparent; color: var(--raavi-text-muted); padding: 0.25rem 0.7rem; cursor: pointer; }
.seg button.on { background: var(--p-primary-color); color: var(--p-primary-contrast-color, #fff); }
.body { max-inline-size: 72rem; margin-inline: auto; padding: var(--raavi-space-4); display: grid; gap: var(--raavi-space-4); grid-template-columns: minmax(0, 1fr); }

.hero { display: flex; align-items: center; gap: var(--raavi-space-4); flex-wrap: wrap; padding: var(--raavi-space-4) var(--raavi-space-6); border-radius: var(--raavi-radius-lg); color: #fff; box-shadow: var(--raavi-shadow-2); transition: background 0.3s; }
.hero h1 { margin: 0; font-size: 1.7rem; line-height: 1.2; }
.hero .sub { margin: 4px 0 0; opacity: 0.92; }
.hero .orb { display: grid; place-items: center; inline-size: 3.4rem; block-size: 3.4rem; border-radius: 50%; background: rgba(255, 255, 255, 0.2); font-size: 1.8rem; }
.hero-side { margin-inline-start: auto; font-size: 0.9rem; }
.hero .muted { color: rgba(255, 255, 255, 0.88); }
.hero.st-healthy { background: linear-gradient(135deg, #15803d, #16a34a); }
.hero.st-degraded { background: linear-gradient(135deg, #b45309, #d97706); }
.hero.st-unhealthy, .hero.st-unreachable { background: linear-gradient(135deg, #991b1b, #dc2626); }
.hero.st-checking { background: linear-gradient(135deg, #475569, #64748b); }
.hero .pi { color: #fff; }

.down { border: 2px solid var(--raavi-danger); background: color-mix(in srgb, var(--raavi-danger) 9%, var(--raavi-surface)); border-radius: var(--raavi-radius-md); padding: var(--raavi-space-4); }
.down h2 { margin: 0 0 var(--raavi-space-2); font-size: 1.15rem; color: var(--raavi-danger); display: flex; gap: var(--raavi-space-2); align-items: center; }
.down h3 { margin: var(--raavi-space-3) 0 var(--raavi-space-1); font-size: 0.95rem; }
.down .why { margin: 0 0 var(--raavi-space-1); font-weight: 600; }
.down ol { margin: 0; padding-inline-start: 1.4rem; display: grid; gap: 4px; }

.toolbar { display: flex; align-items: center; gap: var(--raavi-space-2); flex-wrap: wrap; }
.interval { display: inline-flex; gap: var(--raavi-space-1); align-items: center; font-size: 0.9rem; color: var(--raavi-text-muted); }
.interval select { font: inherit; padding: 0.2rem 0.4rem; border: 1px solid var(--raavi-border); border-radius: 4px; background: var(--raavi-surface); color: inherit; }

.kpis { display: grid; grid-template-columns: repeat(auto-fit, minmax(10.5rem, 1fr)); gap: var(--raavi-space-3); }
.kpi { background: var(--raavi-surface); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-md); padding: var(--raavi-space-3) var(--raavi-space-4); border-inline-start: 4px solid var(--p-primary-color); min-inline-size: 0; }
.kpi.warn { border-inline-start-color: #d97706; }
.kpi.bad { border-inline-start-color: var(--raavi-danger); }
.kpi h2 { margin: 0; font-size: 0.78rem; font-weight: 600; color: var(--raavi-text-muted); text-transform: uppercase; letter-spacing: 0.03em; }
.kpi .v { margin: 2px 0; font-size: 1.55rem; font-weight: 700; font-variant-numeric: tabular-nums; overflow-wrap: anywhere; }
.kpi .d { margin: 0; font-size: 0.78rem; color: var(--raavi-text-muted); }
.stale { opacity: 0.55; filter: grayscale(0.6); }

.charts { display: grid; grid-template-columns: repeat(auto-fit, minmax(17rem, 1fr)); gap: var(--raavi-space-3); }
.card { background: var(--raavi-surface); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-md); padding: var(--raavi-space-3) var(--raavi-space-4); min-inline-size: 0; }
.card h2 { margin: 0 0 var(--raavi-space-2); font-size: 0.95rem; }
.table { inline-size: 100%; border-collapse: collapse; font-size: 0.92rem; }
.table th, .table td { text-align: start; padding: var(--raavi-space-2); border-block-end: 1px solid var(--raavi-border); vertical-align: top; }
.table thead th { font-size: 0.78rem; color: var(--raavi-text-muted); text-transform: uppercase; }
.table .num { font-variant-numeric: tabular-nums; white-space: nowrap; }
.comps { overflow-x: auto; }
.badge { display: inline-block; padding: 0.05rem 0.6rem; border-radius: 999px; font-size: 0.8rem; font-weight: 600; color: #fff; white-space: nowrap; }
.badge.st-healthy { background: #16a34a; }
.badge.st-degraded { background: #d97706; }
.badge.st-unhealthy { background: #dc2626; }
.hint { color: var(--raavi-text-muted); }
.foot { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; }
.muted { color: var(--raavi-text-muted); }
.small { font-size: 0.82rem; }
code { background: var(--raavi-bg); padding: 0 0.3rem; border-radius: 4px; }
@media (max-width: 640px) {
  .hero { padding: var(--raavi-space-3); }
  .hero-side { margin-inline-start: 0; }
  .hero h1 { font-size: 1.35rem; }
}
@media (prefers-reduced-motion: reduce) { .hero { transition: none; } }
</style>
