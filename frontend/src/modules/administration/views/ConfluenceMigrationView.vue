<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Password from 'primevue/password'
import Textarea from 'primevue/textarea'
import Checkbox from 'primevue/checkbox'
import RadioButton from 'primevue/radiobutton'
import ProgressBar from 'primevue/progressbar'
import Tag from 'primevue/tag'
import Message from 'primevue/message'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import {
  isActive, migrationApi, needsToken, parseKeyMap, parseSpaces,
  type MigrationEvent, type MigrationJob, type MigrationReport, type MigrationStatus,
} from '@/core/api/migration'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import MigrationReportPanel from './MigrationReportPanel.vue'

const { t, te } = useI18n()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()

const jobs = ref<MigrationJob[]>([])
const loading = ref(true)
const selected = ref<MigrationJob | null>(null)
const report = ref<MigrationReport | null>(null)
const events = ref<MigrationEvent[]>([])
const busy = ref(false)

// New migration form. The token is only ever held in this component while the form is open.
const createOpen = ref(false)
const url = ref('')
const token = ref('')
const mode = ref<'dry' | 'import'>('dry')
const spacesText = ref('')
const keyMapText = ref('')
const userMapText = ref('')
const reconvert = ref(false)
const mapUsersByEmail = ref(false)
const restartToken = ref('')

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const severity = (s: MigrationStatus) =>
  s === 'Completed' ? 'success' : s === 'CompletedWithErrors' ? 'warn' : s === 'Failed' ? 'danger' : s === 'Running' || s === 'Queued' ? 'info' : 'secondary'
const percent = (j: MigrationJob) => (j.pagesTotal > 0 ? Math.min(100, Math.round(((j.pagesDone + j.pagesFailed) / j.pagesTotal) * 100)) : 0)
const when = (iso: string | null) => (iso ? formatDate(iso, locale.locale, { dateStyle: 'medium', timeStyle: 'short' }) : '—')

async function load() {
  try {
    jobs.value = await migrationApi.list()
    if (selected.value) {
      const fresh = jobs.value.find((j) => j.id === selected.value!.id)
      if (fresh) await show(fresh, false)
    }
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}

async function show(job: MigrationJob, resetDetail = true) {
  const finishedNow = selected.value && isActive(selected.value.status) && !isActive(job.status)
  selected.value = job
  if (resetDetail) {
    report.value = null
    events.value = []
    restartToken.value = ''
  }
  // The report exists once the job ended; events keep coming while it runs.
  const wantsReport = job.hasReport && (resetDetail || finishedNow || !report.value)
  const [r, ev] = await Promise.all([
    wantsReport ? migrationApi.report(job.id).catch(() => null) : Promise.resolve(report.value),
    migrationApi.events(job.id).catch(() => events.value),
  ])
  report.value = r
  events.value = ev
}

async function create() {
  busy.value = true
  try {
    const job = await migrationApi.create({
      url: url.value.trim(),
      token: token.value,
      dryRun: mode.value === 'dry',
      spaces: parseSpaces(spacesText.value),
      keyMap: parseKeyMap(keyMapText.value),
      reconvert: reconvert.value,
      mapUsersByEmail: mapUsersByEmail.value,
      userMap: parseKeyMap(userMapText.value),
    })
    createOpen.value = false
    token.value = ''
    jobs.value = [job, ...jobs.value]
    await show(job)
  } catch (e) {
    fail(e)
  } finally {
    busy.value = false
  }
}

async function run(fn: () => Promise<MigrationJob>) {
  busy.value = true
  try {
    const job = await fn()
    restartToken.value = ''
    jobs.value = jobs.value.map((j) => (j.id === job.id ? job : j))
    await show(job, false)
  } catch (e) {
    fail(e)
  } finally {
    busy.value = false
  }
}

const start = () => selected.value && run(() => migrationApi.start(selected.value!.id, restartToken.value || undefined))
const pause = () => selected.value && run(() => migrationApi.pause(selected.value!.id))

function cancel() {
  const job = selected.value
  if (!job) return
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('migration.confirmCancel'),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: () => run(() => migrationApi.cancel(job.id)),
  })
}

const canStart = computed(() => !!selected.value && !isActive(selected.value.status) && (!needsToken(selected.value.status) || restartToken.value.length > 0))
const executed = computed(() => selected.value?.mode === 'Execute')

// Progress is polled while a job is active (the worker, not this page, does the work).
let timer: ReturnType<typeof setInterval> | null = null
onMounted(async () => {
  await load()
  timer = setInterval(() => {
    if (jobs.value.some((j) => isActive(j.status)) || (selected.value && isActive(selected.value.status))) void load()
  }, 3000)
})
onBeforeUnmount(() => {
  if (timer) clearInterval(timer)
})
</script>

<template>
  <section class="page" data-testid="confluence-migration">
    <header class="page-head">
      <h1>{{ t('migration.title') }}</h1>
      <Button icon="pi pi-plus" :label="t('migration.new')" data-testid="new-migration" @click="createOpen = true" />
    </header>
    <Message severity="info" :closable="false">{{ t('migration.intro') }}</Message>

    <DataTable :value="jobs" :loading="loading" data-key="id" size="small" striped-rows selection-mode="single" @row-click="(e) => show(e.data)">
      <template #empty>{{ t('migration.empty') }}</template>
      <Column :header="t('migration.source')">
        <template #body="{ data }"><span dir="ltr">{{ data.sourceUrl }}</span></template>
      </Column>
      <Column :header="t('migration.mode')">
        <template #body="{ data }">{{ t(data.mode === 'DryRun' ? 'migration.modeDry' : 'migration.modeImport') }}</template>
      </Column>
      <Column :header="t('migration.status')">
        <template #body="{ data }"><Tag :severity="severity(data.status)" :value="t(`migration.statuses.${data.status}`)" /></template>
      </Column>
      <Column :header="t('migration.progress')">
        <template #body="{ data }">{{ data.pagesDone }} / {{ data.pagesTotal }}</template>
      </Column>
      <Column :header="t('migration.created')">
        <template #body="{ data }">{{ when(data.createdAt) }}</template>
      </Column>
    </DataTable>

    <article v-if="selected" class="detail" :aria-label="t('migration.details')" data-testid="migration-detail">
      <header class="detail-head">
        <h2>
          <span dir="ltr">{{ selected.sourceUrl }}</span>
          <Tag :severity="severity(selected.status)" :value="t(`migration.statuses.${selected.status}`)" data-testid="job-status" />
        </h2>
        <div class="actions">
          <Button v-if="isActive(selected.status)" icon="pi pi-pause" severity="secondary" :label="t('migration.pause')" :disabled="busy || selected.control === 'Pause'" data-testid="pause" @click="pause" />
          <Button v-if="['Created', 'Queued', 'Running', 'Paused'].includes(selected.status)" icon="pi pi-times" severity="danger" text :label="t('migration.cancel')" :disabled="busy || selected.control === 'Cancel'" data-testid="cancel" @click="cancel" />
        </div>
      </header>

      <p class="muted">
        {{ t('migration.sourceVersion', { v: selected.sourceVersion ?? '—' }) }} ·
        {{ t(selected.mode === 'DryRun' ? 'migration.modeDry' : 'migration.modeImport') }} ·
        {{ t('migration.startedAt', { time: when(selected.startedAt) }) }}
      </p>

      <ProgressBar :value="percent(selected)" :show-value="true" :aria-label="t('migration.progress')" data-testid="job-progress" />
      <p class="counters" data-testid="job-counters">
        {{ t('migration.counters', { spaces: `${selected.spacesDone}/${selected.spacesTotal}`, pages: `${selected.pagesDone}/${selected.pagesTotal}`, versions: selected.versionsDone, failed: selected.pagesFailed, warnings: selected.warnings }) }}
      </p>
      <p v-if="selected.currentOperation" class="muted" dir="auto" aria-live="polite">{{ selected.currentOperation }}</p>
      <Message v-if="selected.control" severity="warn" :closable="false">{{ t(selected.control === 'Pause' ? 'migration.pauseRequested' : 'migration.cancelRequested') }}</Message>

      <form v-if="!isActive(selected.status)" class="start" @submit.prevent="start">
        <template v-if="needsToken(selected.status)">
          <label for="restart-token">{{ t('migration.tokenAgain') }}</label>
          <Password id="restart-token" v-model="restartToken" input-id="restart-token-input" :feedback="false" toggle-mask :input-props="{ autocomplete: 'off' }" fluid />
        </template>
        <Button
          type="submit"
          icon="pi pi-play"
          :label="t(selected.status === 'Created' ? 'migration.start' : selected.status === 'Paused' ? 'migration.resume' : 'migration.runAgain')"
          :disabled="busy || !canStart"
          data-testid="start"
        />
      </form>

      <MigrationReportPanel :report="report" :events="events" :executed="executed" />
    </article>

    <Dialog v-model:visible="createOpen" modal :header="t('migration.new')" :style="{ inlineSize: 'min(36rem, 95vw)' }">
      <form class="form" autocomplete="off" @submit.prevent="create">
        <label for="m-url">{{ t('migration.url') }}</label>
        <InputText id="m-url" v-model="url" dir="ltr" placeholder="https://wiki.example.com" required data-testid="m-url" />
        <small class="muted">{{ t('migration.urlHelp') }}</small>

        <label for="m-token">{{ t('migration.token') }}</label>
        <Password id="m-token" v-model="token" input-id="m-token-input" :feedback="false" toggle-mask :input-props="{ autocomplete: 'off' }" fluid required data-testid="m-token" />
        <small class="muted">{{ t('migration.tokenHelp') }}</small>

        <fieldset>
          <legend>{{ t('migration.mode') }}</legend>
          <div class="radio"><RadioButton v-model="mode" input-id="mode-dry" value="dry" /><label for="mode-dry">{{ t('migration.modeDryHelp') }}</label></div>
          <div class="radio"><RadioButton v-model="mode" input-id="mode-import" value="import" /><label for="mode-import">{{ t('migration.modeImportHelp') }}</label></div>
        </fieldset>

        <label for="m-spaces">{{ t('migration.spaces') }}</label>
        <InputText id="m-spaces" v-model="spacesText" dir="ltr" placeholder="ENG, OPS" />
        <small class="muted">{{ t('migration.spacesHelp') }}</small>

        <label for="m-keys">{{ t('migration.keyMap') }}</label>
        <Textarea id="m-keys" v-model="keyMapText" dir="ltr" rows="3" placeholder="docs=DOCS" auto-resize />
        <small class="muted">{{ t('migration.keyMapHelp') }}</small>

        <fieldset>
          <legend>{{ t('migration.people') }}</legend>
          <div class="radio"><Checkbox v-model="mapUsersByEmail" binary input-id="m-map-email" data-testid="m-map-email" /><label for="m-map-email">{{ t('migration.mapByEmail') }}</label></div>
          <small class="muted">{{ t('migration.mapByEmailHelp') }}</small>
          <label for="m-usermap">{{ t('migration.userMap') }}</label>
          <Textarea id="m-usermap" v-model="userMapText" dir="ltr" rows="3" placeholder="vali=vali@example.com" auto-resize />
          <small class="muted">{{ t('migration.userMapHelp') }}</small>
        </fieldset>

        <div class="radio"><Checkbox v-model="reconvert" binary input-id="m-reconvert" /><label for="m-reconvert">{{ t('migration.reconvert') }}</label></div>
        <small class="muted">{{ t('migration.reconvertHelp') }}</small>

        <Button type="submit" :label="t('migration.create')" :loading="busy" :disabled="!url || !token" data-testid="m-create" />
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.page { display: grid; gap: var(--raavi-space-3); max-inline-size: 70rem; }
.page-head { display: flex; justify-content: space-between; align-items: center; gap: var(--raavi-space-3); flex-wrap: wrap; }
h1 { margin: 0; font-size: 1.6rem; }
.detail { display: grid; gap: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: var(--raavi-space-4); }
.detail-head { display: flex; justify-content: space-between; align-items: flex-start; gap: var(--raavi-space-3); flex-wrap: wrap; }
.detail-head h2 { margin: 0; font-size: 1.1rem; display: flex; gap: var(--raavi-space-2); align-items: center; flex-wrap: wrap; overflow-wrap: anywhere; }
.actions { display: flex; gap: var(--raavi-space-2); }
.start { display: grid; gap: var(--raavi-space-2); max-inline-size: 24rem; }
.form { display: grid; gap: var(--raavi-space-2); }
fieldset { border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); display: grid; gap: var(--raavi-space-2); }
.radio { display: flex; gap: var(--raavi-space-2); align-items: flex-start; }
.muted { color: var(--raavi-text-muted); margin: 0; }
.counters { margin: 0; }
</style>
