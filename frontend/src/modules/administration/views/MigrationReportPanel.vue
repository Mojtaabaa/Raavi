<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Tag from 'primevue/tag'
import Message from 'primevue/message'
import type { MigrationEvent, MigrationReport } from '@/core/api/migration'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'

/** Dry-run / execution report: totals per space, what could not be converted, who could not be mapped, and the event log. */
const props = defineProps<{ report: MigrationReport | null; events: MigrationEvent[]; executed: boolean }>()
const { t } = useI18n()
const locale = useLocaleStore()

const macros = computed(() => Object.entries(props.report?.unsupportedMacros ?? {}).sort((a, b) => b[1] - a[1]))
const warnings = computed(() => Object.entries(props.report?.warningsByCode ?? {}).sort((a, b) => b[1] - a[1]))
const totals = computed(() => {
  const s = props.report?.spaces ?? []
  return { pages: s.reduce((n, x) => n + x.pages, 0), versions: s.reduce((n, x) => n + x.versions, 0), failed: s.reduce((n, x) => n + x.pagesFailed, 0) }
})
const mb = (n: number) => (n / (1024 * 1024)).toFixed(1)
const warnLabel = (code: string) => t(`migration.warning.${code.replace('.', '_')}`)
</script>

<template>
  <div class="report" data-testid="migration-report">
    <p v-if="!report" class="muted">{{ t('migration.report.none') }}</p>
    <template v-else>
      <ul class="totals" :aria-label="t('migration.report.totals')">
        <li><strong>{{ report.spaces.length }}</strong> {{ t('migration.report.spaces') }}</li>
        <li><strong>{{ totals.pages }}</strong> {{ t('migration.report.pages') }}</li>
        <li><strong>{{ totals.versions }}</strong> {{ t('migration.report.versions') }}</li>
        <li><strong>{{ report.attachments?.found ?? 0 }}</strong> {{ t('migration.report.files', { mb: mb(report.attachments?.bytes ?? 0) }) }}</li>
        <li v-if="executed"><strong>{{ report.linksResolved }}</strong> {{ t('migration.report.linksResolved') }}</li>
        <li v-if="executed"><strong>{{ report.linksUnresolved }}</strong> {{ t('migration.report.linksUnresolved') }}</li>
      </ul>

      <Message v-if="report.validation.length" severity="error" :closable="false" data-testid="validation-issues">
        {{ t('migration.report.validationIssues', { n: report.validation.length }) }}
        <ul>
          <li v-for="v in report.validation" :key="v.spaceKey + v.check">{{ v.spaceKey }}: {{ v.check }} — {{ v.expected }} / {{ v.actual }}</li>
        </ul>
      </Message>
      <Message v-else-if="executed" severity="success" :closable="false">{{ t('migration.report.validationOk') }}</Message>

      <h3>{{ t('migration.report.spaces') }}</h3>
      <DataTable :value="report.spaces" data-key="sourceKey" size="small" striped-rows>
        <Column field="sourceKey" :header="t('migration.report.sourceKey')" />
        <Column field="name" :header="t('migration.report.name')" />
        <Column v-if="executed" :header="t('migration.report.raaviKey')">
          <template #body="{ data }">
            <Tag v-if="data.keyConflict" severity="danger" :value="t('migration.report.keyConflict')" />
            <span v-else dir="ltr">{{ data.raaviKey }}</span>
          </template>
        </Column>
        <Column field="pages" :header="t('migration.report.pages')" />
        <Column field="versions" :header="t('migration.report.versions')" />
        <Column v-if="executed" field="versionsImported" :header="t('migration.report.imported')" />
        <Column field="pagesFailed" :header="t('migration.report.failed')" />
      </DataTable>

      <section v-if="report.attachments && report.attachments.found > 0" data-testid="attachments-summary">
        <h3>{{ t('migration.report.attachmentsTitle') }}</h3>
        <p>{{ t('migration.report.attachmentsLine', { imported: report.attachments.imported, found: report.attachments.found, large: report.attachments.tooLarge, failed: report.attachments.failed, max: Math.round(report.attachments.maxBytes / (1024 * 1024)) }) }}</p>
      </section>

      <section v-if="report.access" data-testid="access-summary">
        <h3>{{ t('migration.report.accessTitle') }}</h3>
        <p>{{ t('migration.report.accessLine', { pages: report.access.restrictedPages, applied: report.access.restrictionsApplied }) }}</p>
        <ul v-if="report.access.unmappedSubjects.length" class="plain">
          <li v-for="u in report.access.unmappedSubjects.slice(0, 20)" :key="u" dir="auto">{{ u }}</li>
        </ul>
        <p v-if="report.access.unmappedSubjects.length" class="muted small">{{ t('migration.report.unmappedHelp') }}</p>
        <p class="muted small">{{ t('migration.report.spacePermissions') }}</p>
      </section>

      <section v-if="report.authorMappings?.length" data-testid="author-mappings">
        <h3>{{ t('migration.report.authorsTable') }}</h3>
        <DataTable :value="report.authorMappings" data-key="sourceKey" size="small" striped-rows>
          <Column :header="t('migration.report.sourceUser')">
            <template #body="{ data }"><span dir="auto">{{ data.displayName ?? data.userName ?? data.sourceKey }}</span></template>
          </Column>
          <Column :header="t('migration.report.email')">
            <template #body="{ data }"><span dir="ltr">{{ data.email ?? '—' }}</span></template>
          </Column>
          <Column :header="t('migration.report.match')">
            <template #body="{ data }">
              <Tag v-if="data.applied" severity="success" :value="t('migration.report.applied', { name: data.suggestedUser })" />
              <Tag v-else-if="data.suggestedUser" severity="warn" :value="t('migration.report.suggested', { name: data.suggestedUser })" />
              <span v-else class="muted">{{ t('migration.report.noMatch') }}</span>
            </template>
          </Column>
        </DataTable>
        <p class="muted small">{{ t('migration.report.matchHelp') }}</p>
      </section>

      <div class="cols">
        <section>
          <h3>{{ t('migration.report.macros') }}</h3>
          <p v-if="macros.length === 0" class="muted">{{ t('migration.report.noMacros') }}</p>
          <ul v-else class="plain">
            <li v-for="[name, n] in macros" :key="name"><code dir="ltr">{{ name }}</code> × {{ n }}</li>
          </ul>
          <p class="muted small">{{ t('migration.report.macrosHelp') }}</p>
        </section>
        <section>
          <h3>{{ t('migration.report.warnings') }}</h3>
          <p v-if="warnings.length === 0" class="muted">{{ t('migration.report.noWarnings') }}</p>
          <ul v-else class="plain">
            <li v-for="[code, n] in warnings" :key="code">{{ warnLabel(code) }} × {{ n }}</li>
          </ul>
        </section>
        <section>
          <h3>{{ t('migration.report.authors') }}</h3>
          <p class="muted small">{{ t('migration.report.authorsHelp') }}</p>
          <ul class="plain" data-testid="unmapped-authors">
            <li v-for="a in report.unmappedAuthors.slice(0, 20)" :key="a" dir="auto">{{ a }}</li>
          </ul>
          <p v-if="report.unmappedAuthors.length > 20" class="muted small">{{ t('migration.report.more', { n: report.unmappedAuthors.length - 20 }) }}</p>
        </section>
      </div>
    </template>

    <h3>{{ t('migration.events.title') }}</h3>
    <p v-if="events.length === 0" class="muted">{{ t('migration.events.none') }}</p>
    <DataTable v-else :value="events" size="small" striped-rows :rows="20" paginator data-testid="migration-events">
      <Column :header="t('migration.events.level')">
        <template #body="{ data }">
          <Tag :severity="data.level === 'Error' ? 'danger' : data.level === 'Warning' ? 'warn' : 'info'" :value="t(`migration.events.levels.${data.level}`)" />
        </template>
      </Column>
      <Column :header="t('migration.events.at')">
        <template #body="{ data }">{{ formatDate(data.at, locale.locale, { dateStyle: 'short', timeStyle: 'medium' }) }}</template>
      </Column>
      <Column field="sourceRef" :header="t('migration.events.where')">
        <template #body="{ data }"><span dir="auto">{{ data.sourceRef }}</span></template>
      </Column>
      <Column field="message" :header="t('migration.events.message')">
        <template #body="{ data }"><span dir="auto">{{ data.message }}</span></template>
      </Column>
    </DataTable>
  </div>
</template>

<style scoped>
.report { display: grid; gap: var(--raavi-space-3); }
h3 { margin: var(--raavi-space-2) 0 0; font-size: 1rem; }
.totals { display: flex; flex-wrap: wrap; gap: var(--raavi-space-2) var(--raavi-space-4); list-style: none; margin: 0; padding: 0; }
.cols { display: grid; grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr)); gap: var(--raavi-space-4); }
.plain { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-1); }
.muted { color: var(--raavi-text-muted); }
.small { font-size: 0.85rem; }
</style>
