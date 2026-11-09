<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { databasesApi, type CellValue, type ColumnType, type DatabaseInput, type DbColumn, type DbRow, type NumberSummary, type SpaceDatabase } from '@/core/api/databases'
import { parseCsv, planImport } from './csv'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'

/**
 * One database (Phase 9c): a table whose columns are defined as data. Cells edit in place and save on change (each row has its own ETag);
 * the header sorts, the search box filters on the server, and the schema dialog adds, renames and removes columns (types of existing
 * columns are fixed so stored data can never be reinterpreted).
 */
const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()

const PAGE = 50
const id = computed(() => String(route.params.id))
const database = ref<SpaceDatabase | null>(null)
const rows = ref<DbRow[]>([])
const total = ref(0)
const loading = ref(true)
const q = ref('')
const sort = ref<string | null>(null)
const dir = ref<'asc' | 'desc'>('asc')
const page = ref(0)
const summary = ref<Record<string, NumberSummary>>({})
const view = ref<'table' | 'board'>('table')
const boardColumnId = ref<string | null>(null)
const filters = ref<{ columnId: string; value: string }[]>([])
const filterColumnId = ref<string | null>(null)
const filterValue = ref('')
const hidden = ref<string[]>([])
const columnsMenu = ref(false)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const canEdit = computed(() => !!database.value?.canEdit)
const allColumns = computed(() => database.value?.columns ?? [])
/** Columns shown (the person can hide some; the choice is kept per database in this browser). */
const columns = computed(() => allColumns.value.filter((c) => !hidden.value.includes(c.id)))
const selectColumns = computed(() => allColumns.value.filter((c) => c.type === 'Select'))
const boardColumn = computed(() => selectColumns.value.find((c) => c.id === boardColumnId.value) ?? selectColumns.value[0] ?? null)
const hiddenKey = () => `raavi.db.hidden.${id.value}`
function loadHidden() {
  try {
    hidden.value = JSON.parse(localStorage.getItem(hiddenKey()) ?? '[]') as string[]
  } catch {
    hidden.value = []
  }
}
function toggleColumn(col: DbColumn) {
  hidden.value = hidden.value.includes(col.id) ? hidden.value.filter((x) => x !== col.id) : [...hidden.value, col.id]
  try {
    localStorage.setItem(hiddenKey(), JSON.stringify(hidden.value))
  } catch {
    /* only for this page */
  }
}
const pages = computed(() => Math.max(1, Math.ceil(total.value / PAGE)))

const filterParams = () => filters.value.map((f) => `${f.columnId}:${f.value}`)

async function loadRows() {
  try {
    // The board shows many cards at once: it asks for the server maximum instead of a page.
    const board = view.value === 'board'
    const result = await databasesApi.rows(id.value, { q: q.value.trim(), sort: sort.value, dir: dir.value, skip: board ? 0 : page.value * PAGE, take: board ? 500 : PAGE, filters: filterParams() })
    rows.value = result.rows
    total.value = result.total
    summary.value = result.summary ?? {}
  } catch (e) {
    fail(e)
  }
}

async function load() {
  loading.value = true
  try {
    database.value = await databasesApi.get(id.value)
    await loadRows()
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}
onMounted(() => {
  loadHidden()
  void load()
})
watch(id, () => {
  filters.value = []
  loadHidden()
  void load()
})
watch(view, () => {
  page.value = 0
  void loadRows()
})

// ── Filters ───────────────────────────────────────────────────────────────────────────────────
const filterColumn = computed(() => allColumns.value.find((c) => c.id === filterColumnId.value) ?? null)
function addFilter() {
  const col = filterColumn.value
  if (!col) return
  if (col.type === 'Checkbox' && filterValue.value === '') filterValue.value = 'true'
  if (filterValue.value === '' && col.type !== 'Select') return // an empty text filter would mean "is empty": ask for it explicitly with the select
  filters.value = [...filters.value.filter((f) => f.columnId !== col.id), { columnId: col.id, value: filterValue.value }]
  filterValue.value = ''
  page.value = 0
  void loadRows()
}
function removeFilter(columnId: string) {
  filters.value = filters.value.filter((f) => f.columnId !== columnId)
  page.value = 0
  void loadRows()
}
const clearFilters = () => {
  filters.value = []
  void loadRows()
}
function filterLabel(f: { columnId: string; value: string }): string {
  const col = allColumns.value.find((c) => c.id === f.columnId)
  if (!col) return f.value
  if (col.type === 'Select') return `${col.name}: ${f.value ? optionLabel(col, f.value) : t('databases.empty')}`
  if (col.type === 'Checkbox') return `${col.name}: ${f.value === 'true' ? t('common.yes') : t('common.no')}`
  return `${col.name}: ${f.value}`
}

const number = (n: number) => new Intl.NumberFormat(locale.locale === 'fa' ? 'fa-IR' : 'en-US', { maximumFractionDigits: 2 }).format(n)

let timer: ReturnType<typeof setTimeout> | undefined
watch(q, () => {
  clearTimeout(timer)
  timer = setTimeout(() => {
    page.value = 0
    void loadRows()
  }, 250)
})

function sortBy(col: DbColumn) {
  if (sort.value === col.id) dir.value = dir.value === 'asc' ? 'desc' : 'asc'
  else {
    sort.value = col.id
    dir.value = 'asc'
  }
  page.value = 0
  void loadRows()
}
const ariaSort = (col: DbColumn) => (sort.value === col.id ? (dir.value === 'asc' ? 'ascending' : 'descending') : 'none')

function go(delta: number) {
  page.value = Math.min(pages.value - 1, Math.max(0, page.value + delta))
  void loadRows()
}

// ── Cells ─────────────────────────────────────────────────────────────────────────────────────

const optionLabel = (col: DbColumn, v: CellValue | undefined) => col.options.find((o) => o.id === v)?.label ?? ''
const dateLabel = (v: CellValue | undefined) => (typeof v === 'string' ? formatDate(`${v}T00:00:00Z`, locale.locale, { dateStyle: 'medium', timeZone: 'UTC' }) : '')

async function saveCell(row: DbRow, col: DbColumn, raw: unknown) {
  const next: Record<string, CellValue> = { ...row.values }
  let value: CellValue | null
  if (col.type === 'Number') value = raw === '' || raw === null ? null : Number(raw)
  else if (col.type === 'Checkbox') value = Boolean(raw)
  else value = raw === '' || raw === null ? null : String(raw)
  if (value === null || (typeof value === 'number' && Number.isNaN(value))) delete next[col.id]
  else next[col.id] = value
  try {
    const saved = await databasesApi.updateRow(row, next)
    rows.value = rows.value.map((r) => (r.id === saved.id ? saved : r))
  } catch (e) {
    fail(e)
    await loadRows()
  }
}
const onInput = (row: DbRow, col: DbColumn, e: Event) => {
  const el = e.target as HTMLInputElement | HTMLSelectElement
  void saveCell(row, col, col.type === 'Checkbox' ? (el as HTMLInputElement).checked : el.value)
}

async function addRow() {
  try {
    await databasesApi.addRow(id.value, {})
    q.value = ''
    sort.value = null
    total.value += 1
    page.value = Math.max(0, Math.ceil(total.value / PAGE) - 1)
    database.value = await databasesApi.get(id.value)
    await loadRows()
  } catch (e) {
    fail(e)
  }
}

async function duplicateRow(row: DbRow) {
  try {
    await databasesApi.addRow(id.value, { ...row.values })
    database.value = await databasesApi.get(id.value)
    await loadRows()
  } catch (e) {
    fail(e)
  }
}

// ── CSV import ────────────────────────────────────────────────────────────────────────────────
const importInput = ref<HTMLInputElement | null>(null)
const importing = ref(false)
const MAX_IMPORT = 500
async function importFile(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  importing.value = true
  try {
    const plan = planImport(parseCsv(await file.text()), allColumns.value)
    if (plan.rows.length === 0 || plan.rows.every((r) => Object.keys(r).length === 0)) {
      toast.add({ severity: 'warn', summary: t('databases.importNothing'), life: 7000 })
      return
    }
    const rowsToAdd = plan.rows.slice(0, MAX_IMPORT)
    let added = 0
    for (const values of rowsToAdd) {
      await databasesApi.addRow(id.value, values)
      added++
    }
    const notes = [
      plan.rows.length > MAX_IMPORT ? t('databases.importCapped', { max: MAX_IMPORT }) : '',
      plan.ignored.length ? t('databases.importIgnored', { names: plan.ignored.join(', ') }) : '',
      plan.problems.length ? t('databases.importProblems', { count: plan.problems.length }) : '',
    ].filter(Boolean)
    toast.add({ severity: notes.length ? 'warn' : 'success', summary: t('databases.importDone', { count: added }), detail: notes.join(' '), life: notes.length ? 10000 : 4000 })
    database.value = await databasesApi.get(id.value)
    await loadRows()
  } catch (e) {
    fail(e)
    await load()
  } finally {
    importing.value = false
  }
}

// ── Board (cards grouped by a Select column) ──────────────────────────────────────────────────
const lanes = computed(() => {
  const col = boardColumn.value
  if (!col) return []
  const lane = (key: string, label: string) => ({ key, label, rows: rows.value.filter((r) => (r.values[col.id] ?? '') === key) })
  return [lane('', t('databases.noValue')), ...col.options.map((o) => lane(o.id, o.label))]
})
const cardTitle = (row: DbRow): string => {
  const first = columns.value.find((c) => c.type === 'Text' || c.type === 'Url') ?? columns.value[0]
  const v = first ? row.values[first.id] : undefined
  return v === undefined || v === '' ? t('databases.untitledRow') : String(v)
}
const cardDetails = (row: DbRow): string[] =>
  columns.value
    .filter((c) => c.id !== boardColumn.value?.id && row.values[c.id] !== undefined && c.type !== 'Checkbox' && c.id !== (columns.value.find((x) => x.type === 'Text' || x.type === 'Url') ?? columns.value[0])?.id)
    .slice(0, 3)
    .map((c) => `${c.name}: ${c.type === 'Select' ? optionLabel(c, row.values[c.id]) : c.type === 'Date' ? dateLabel(row.values[c.id]) : String(row.values[c.id])}`)
const dragging = ref<string | null>(null)
async function dropOn(laneKey: string) {
  const row = rows.value.find((r) => r.id === dragging.value)
  const col = boardColumn.value
  dragging.value = null
  if (!row || !col || !canEdit.value || (row.values[col.id] ?? '') === laneKey) return
  await saveCell(row, col, laneKey)
}

function removeRow(row: DbRow) {
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('databases.confirmDeleteRow'),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await databasesApi.removeRow(row.id)
        await load()
      } catch (e) {
        fail(e)
      }
    },
  })
}

// ── Schema editor ─────────────────────────────────────────────────────────────────────────────

interface DraftColumn {
  id?: string
  name: string
  type: ColumnType
  /** One option per line; existing options keep their ids. */
  options: { id?: string; label: string }[]
  optionText: string
}
const types: ColumnType[] = ['Text', 'Number', 'Date', 'Checkbox', 'Select', 'Url']
const editingSchema = ref(false)
const draftName = ref('')
const draft = ref<DraftColumn[]>([])
const saving = ref(false)

function openSchema() {
  const db = database.value!
  draftName.value = db.name
  draft.value = db.columns.map((c) => ({ id: c.id, name: c.name, type: c.type, options: c.options.map((o) => ({ ...o })), optionText: c.options.map((o) => o.label).join('\n') }))
  editingSchema.value = true
}
const addColumn = () => draft.value.push({ name: '', type: 'Text', options: [], optionText: '' })
const removeColumn = (i: number) => draft.value.splice(i, 1)
function moveColumn(i: number, by: -1 | 1) {
  const j = i + by
  if (j < 0 || j >= draft.value.length) return
  const [item] = draft.value.splice(i, 1)
  draft.value.splice(j, 0, item!)
}

function schemaBody(): DatabaseInput {
  return {
    name: draftName.value.trim(),
    description: database.value!.description,
    columns: draft.value.map((c) => {
      const labels = c.optionText.split('\n').map((l) => l.trim()).filter(Boolean)
      // A label that already existed keeps its id (so stored cells stay valid even when other options are added or removed).
      const options = c.type === 'Select' ? labels.map((label) => ({ id: c.options.find((o) => o.label === label)?.id, label })) : undefined
      return { id: c.id, name: c.name.trim(), type: c.type, options }
    }),
  }
}

async function saveSchema() {
  saving.value = true
  try {
    database.value = await databasesApi.update(database.value!, schemaBody())
    editingSchema.value = false
    await loadRows()
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}

function removeDatabase() {
  const db = database.value
  if (!db) return
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('databases.confirmDelete', { name: db.name }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await databasesApi.remove(db.id)
        await router.push({ name: 'space-databases', params: { key: db.spaceKey } })
      } catch (e) {
        fail(e)
      }
    },
  })
}
</script>

<template>
  <section v-if="database" class="database" data-testid="database">
    <header class="head">
      <div>
        <RouterLink :to="{ name: 'space-databases', params: { key: database.spaceKey } }" class="muted">{{ database.spaceName }}</RouterLink>
        <h1 dir="auto">{{ database.name }}</h1>
      </div>
      <div class="tools">
        <InputText v-model="q" type="search" :placeholder="t('databases.search')" :aria-label="t('databases.search')" data-testid="database-search" />
        <Button v-if="canEdit" icon="pi pi-plus" :label="t('databases.addRow')" data-testid="add-row" @click="addRow" />
        <Button v-if="canEdit" icon="pi pi-cog" text :label="t('databases.columns')" data-testid="edit-schema" @click="openSchema" />
        <Button v-if="canEdit" icon="pi pi-upload" text :label="t('databases.importCsv')" :loading="importing" data-testid="import-csv" @click="importInput?.click()" />
        <input ref="importInput" type="file" accept=".csv,text/csv,text/plain" class="visually-hidden" tabindex="-1" aria-hidden="true" data-testid="import-input" @change="importFile" />
        <Button as="a" :href="databasesApi.exportUrl(database.id)" icon="pi pi-download" text :label="t('databases.export')" data-testid="export-database" />
      </div>
    </header>

    <div class="options" role="group" :aria-label="t('databases.viewOptions')">
      <span v-if="selectColumns.length" class="seg" role="group" :aria-label="t('databases.view')">
        <Button size="small" :severity="view === 'table' ? 'primary' : 'secondary'" :outlined="view !== 'table'" icon="pi pi-table" :label="t('databases.viewTable')" :aria-pressed="view === 'table'" data-testid="view-table" @click="view = 'table'" />
        <Button size="small" :severity="view === 'board' ? 'primary' : 'secondary'" :outlined="view !== 'board'" icon="pi pi-objects-column" :label="t('databases.viewBoard')" :aria-pressed="view === 'board'" data-testid="view-board" @click="view = 'board'" />
      </span>
      <label v-if="view === 'board' && selectColumns.length > 1" class="inline">{{ t('databases.boardBy') }}
        <select v-model="boardColumnId" data-testid="board-by"><option v-for="c in selectColumns" :key="c.id" :value="c.id">{{ c.name }}</option></select>
      </label>
      <span class="filter">
        <select v-model="filterColumnId" :aria-label="t('databases.filterColumn')" data-testid="filter-column" @change="filterValue = ''">
          <option :value="null">{{ t('databases.filter') }}…</option>
          <option v-for="c in allColumns" :key="c.id" :value="c.id">{{ c.name }}</option>
        </select>
        <template v-if="filterColumn">
          <select v-if="filterColumn.type === 'Select'" v-model="filterValue" :aria-label="t('databases.filterValue')" data-testid="filter-value">
            <option value="">{{ t('databases.empty') }}</option>
            <option v-for="o in filterColumn.options" :key="o.id" :value="o.id">{{ o.label }}</option>
          </select>
          <select v-else-if="filterColumn.type === 'Checkbox'" v-model="filterValue" :aria-label="t('databases.filterValue')" data-testid="filter-value">
            <option value="true">{{ t('common.yes') }}</option>
            <option value="false">{{ t('common.no') }}</option>
          </select>
          <input v-else v-model="filterValue" type="text" :aria-label="t('databases.filterValue')" data-testid="filter-value" @keydown.enter.prevent="addFilter" />
          <Button size="small" icon="pi pi-filter" :label="t('databases.addFilter')" data-testid="add-filter" @click="addFilter" />
        </template>
      </span>
      <span v-for="f in filters" :key="f.columnId" class="chip" data-testid="filter-chip">{{ filterLabel(f) }}
        <button type="button" :aria-label="t('databases.removeFilter')" @click="removeFilter(f.columnId)"><i class="pi pi-times" aria-hidden="true" /></button>
      </span>
      <Button v-if="filters.length > 1" size="small" text :label="t('databases.clearFilters')" @click="clearFilters" />
      <span v-if="view === 'table'" class="cols-menu">
        <Button size="small" text icon="pi pi-eye" :label="t('databases.shownColumns')" :aria-expanded="columnsMenu" data-testid="columns-menu" @click="columnsMenu = !columnsMenu" />
        <div v-if="columnsMenu" class="menu" role="group" :aria-label="t('databases.shownColumns')">
          <label v-for="c in allColumns" :key="c.id"><input type="checkbox" :checked="!hidden.includes(c.id)" :disabled="!hidden.includes(c.id) && columns.length === 1" @change="toggleColumn(c)" /> {{ c.name }}</label>
        </div>
      </span>
      <span class="count muted" role="status" data-testid="row-count">{{ t('databases.rowCount', { count: total }) }}</span>
    </div>

    <div v-if="view === 'board' && boardColumn" class="board" data-testid="board">
      <section v-for="lane in lanes" :key="lane.key || 'none'" class="lane" :aria-label="lane.label" data-testid="lane" @dragover.prevent @drop.prevent="dropOn(lane.key)">
        <h2>{{ lane.label }} <span class="muted">({{ lane.rows.length }})</span></h2>
        <article v-for="row in lane.rows" :key="row.id" class="card" :draggable="canEdit" data-testid="card" @dragstart="dragging = row.id" @dragend="dragging = null">
          <strong dir="auto">{{ cardTitle(row) }}</strong>
          <span v-for="(d, i) in cardDetails(row)" :key="i" class="muted small" dir="auto">{{ d }}</span>
          <span v-if="canEdit" class="card-move">
            <select :value="lane.key" :aria-label="t('databases.moveTo')" data-testid="card-move" @change="saveCell(row, boardColumn, ($event.target as HTMLSelectElement).value)">
              <option v-for="l in lanes" :key="l.key || 'none'" :value="l.key">{{ l.label }}</option>
            </select>
          </span>
        </article>
      </section>
    </div>

    <div v-else class="scroll">
      <table class="table">
        <thead>
          <tr>
            <th v-for="col in columns" :key="col.id" scope="col" :aria-sort="ariaSort(col)">
              <button type="button" class="sort" data-testid="sort" @click="sortBy(col)">
                {{ col.name }}
                <i v-if="sort === col.id" :class="dir === 'asc' ? 'pi pi-arrow-up' : 'pi pi-arrow-down'" aria-hidden="true" />
              </button>
            </th>
            <th v-if="canEdit" scope="col"><span class="sr-only">{{ t('common.actions') }}</span></th>
          </tr>
        </thead>
        <tfoot v-if="Object.keys(summary).length">
          <tr data-testid="summary-row">
            <td v-for="col in columns" :key="col.id">
              <span v-if="summary[col.id]" class="sum" :title="t('databases.summaryTitle', { avg: number(summary[col.id]!.average), min: number(summary[col.id]!.min), max: number(summary[col.id]!.max), count: summary[col.id]!.count })">Σ {{ number(summary[col.id]!.sum) }}</span>
            </td>
            <td v-if="canEdit" />
          </tr>
        </tfoot>
        <tbody>
          <tr v-for="row in rows" :key="row.id" data-testid="row">
            <td v-for="col in columns" :key="col.id">
              <template v-if="canEdit">
                <input v-if="col.type === 'Checkbox'" type="checkbox" :checked="row.values[col.id] === true" :aria-label="col.name" @change="onInput(row, col, $event)" />
                <select v-else-if="col.type === 'Select'" :value="row.values[col.id] ?? ''" :aria-label="col.name" @change="onInput(row, col, $event)">
                  <option value="" />
                  <option v-for="o in col.options" :key="o.id" :value="o.id">{{ o.label }}</option>
                </select>
                <input v-else :type="col.type === 'Number' ? 'number' : col.type === 'Date' ? 'date' : col.type === 'Url' ? 'url' : 'text'" :value="row.values[col.id] ?? ''" :aria-label="col.name" :dir="col.type === 'Text' ? 'auto' : 'ltr'" @change="onInput(row, col, $event)" />
              </template>
              <template v-else>
                <span v-if="col.type === 'Checkbox'">{{ row.values[col.id] === true ? '✓' : '' }}</span>
                <span v-else-if="col.type === 'Select'">{{ optionLabel(col, row.values[col.id]) }}</span>
                <span v-else-if="col.type === 'Date'">{{ dateLabel(row.values[col.id]) }}</span>
                <a v-else-if="col.type === 'Url' && row.values[col.id]" :href="String(row.values[col.id])" rel="noopener noreferrer nofollow" target="_blank">{{ row.values[col.id] }}</a>
                <span v-else dir="auto">{{ row.values[col.id] ?? '' }}</span>
              </template>
            </td>
            <td v-if="canEdit" class="row-actions">
              <Button text rounded size="small" icon="pi pi-copy" :aria-label="t('databases.duplicateRow')" data-testid="duplicate-row" @click="duplicateRow(row)" />
              <Button text rounded size="small" severity="danger" icon="pi pi-trash" :aria-label="t('common.delete')" data-testid="delete-row" @click="removeRow(row)" />
            </td>
          </tr>
        </tbody>
      </table>
    </div>
    <p v-if="!loading && rows.length === 0" class="muted" data-testid="no-rows">{{ q ? t('databases.noMatches') : t('databases.noRows') }}</p>

    <nav v-if="pages > 1 && view === 'table'" class="pager" :aria-label="t('databases.pages')">
      <Button icon="pi pi-angle-left" text rounded :disabled="page === 0" :aria-label="t('databases.prev')" @click="go(-1)" />
      <span>{{ page + 1 }} / {{ pages }}</span>
      <Button icon="pi pi-angle-right" text rounded :disabled="page >= pages - 1" :aria-label="t('databases.next')" @click="go(1)" />
    </nav>

    <Dialog v-model:visible="editingSchema" modal :header="t('databases.columns')" :style="{ inlineSize: 'min(40rem, 95vw)' }">
      <form class="form" @submit.prevent="saveSchema">
        <label for="schema-name">{{ t('databases.name') }}</label>
        <InputText id="schema-name" v-model="draftName" maxlength="200" required />
        <fieldset v-for="(c, i) in draft" :key="c.id ?? `new-${i}`" class="col" data-testid="schema-column">
          <InputText v-model="c.name" maxlength="100" :aria-label="t('databases.columnName')" data-testid="column-name" />
          <select v-model="c.type" :disabled="!!c.id" :aria-label="t('databases.columnType')" data-testid="column-type">
            <option v-for="ty in types" :key="ty" :value="ty">{{ t(`databases.types.${ty}`) }}</option>
          </select>
          <textarea v-if="c.type === 'Select'" v-model="c.optionText" rows="3" :aria-label="t('databases.options')" :placeholder="t('databases.optionsHint')" data-testid="column-options" />
          <span class="col-actions">
            <Button type="button" text rounded size="small" icon="pi pi-arrow-up" :disabled="i === 0" :aria-label="t('databases.moveUp')" @click="moveColumn(i, -1)" />
            <Button type="button" text rounded size="small" icon="pi pi-arrow-down" :disabled="i === draft.length - 1" :aria-label="t('databases.moveDown')" @click="moveColumn(i, 1)" />
            <Button type="button" text rounded severity="danger" icon="pi pi-trash" :aria-label="t('common.delete')" @click="removeColumn(i)" />
          </span>
        </fieldset>
        <Button type="button" severity="secondary" outlined icon="pi pi-plus" :label="t('databases.addColumn')" data-testid="add-column" @click="addColumn" />
        <div class="buttons">
          <Button type="submit" :label="t('common.save')" :loading="saving" :disabled="!draftName.trim() || draft.some((c) => !c.name.trim())" data-testid="save-schema" />
          <Button type="button" severity="danger" text :label="t('databases.deleteDatabase')" @click="removeDatabase" />
        </div>
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.database { display: grid; gap: var(--raavi-space-3); }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: end; }
h1 { margin: 0; font-size: 1.5rem; }
.tools { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.scroll { overflow-x: auto; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.table { inline-size: 100%; border-collapse: collapse; }
th, td { padding: var(--raavi-space-1) var(--raavi-space-2); border-block-end: 1px solid var(--raavi-border); text-align: start; min-inline-size: 8rem; }
th { background: var(--raavi-surface-muted, transparent); }
.sort { all: unset; cursor: pointer; font-weight: 600; display: inline-flex; gap: 0.35rem; align-items: center; }
.sort:focus-visible { outline: 2px solid var(--raavi-primary, #2563eb); }
td input:not([type='checkbox']), td select { inline-size: 100%; border: 1px solid transparent; background: transparent; color: inherit; font: inherit; padding: 0.25rem; border-radius: 4px; }
td input:hover, td select:hover, td input:focus, td select:focus { border-color: var(--raavi-border); background: var(--raavi-surface, transparent); }
.pager { display: flex; gap: var(--raavi-space-2); align-items: center; justify-content: center; }
.form { display: grid; gap: var(--raavi-space-2); }
.options { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.options select, .options input[type='text'] { font: inherit; padding: 0.25rem 0.4rem; border: 1px solid var(--raavi-border); border-radius: 4px; background: var(--raavi-surface); color: inherit; max-inline-size: 12rem; }
.seg { display: inline-flex; gap: var(--raavi-space-1); }
.inline { display: inline-flex; gap: var(--raavi-space-1); align-items: center; font-size: 0.9rem; }
.filter { display: inline-flex; gap: var(--raavi-space-1); align-items: center; flex-wrap: wrap; }
.chip { display: inline-flex; align-items: center; gap: 0.3rem; padding: 0.1rem 0.2rem 0.1rem 0.6rem; border-radius: 999px; background: var(--p-primary-50, rgba(0,0,0,0.06)); color: var(--p-primary-color); font-size: 0.85rem; }
.chip button { all: unset; cursor: pointer; display: inline-grid; place-items: center; inline-size: 1.3rem; block-size: 1.3rem; border-radius: 50%; }
.chip button:hover { background: rgba(0,0,0,0.08); }
.cols-menu { position: relative; }
.menu { position: absolute; inset-block-start: 100%; inset-inline-start: 0; z-index: 8; display: grid; gap: 2px; padding: var(--raavi-space-2); min-inline-size: 12rem; background: var(--raavi-surface); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); box-shadow: var(--raavi-shadow-2); }
.menu label { display: flex; gap: var(--raavi-space-2); align-items: center; }
.count { margin-inline-start: auto; font-size: 0.85rem; }
.board { display: flex; gap: var(--raavi-space-3); overflow-x: auto; align-items: flex-start; padding-block-end: var(--raavi-space-2); }
.lane { flex: 0 0 16rem; display: grid; gap: var(--raavi-space-2); padding: var(--raavi-space-2); background: var(--raavi-bg); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-md); min-block-size: 6rem; }
.lane h2 { margin: 0; font-size: 0.95rem; }
.card { display: grid; gap: 2px; padding: var(--raavi-space-2); background: var(--raavi-surface); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); cursor: grab; overflow-wrap: anywhere; }
.card[draggable='false'] { cursor: default; }
.card-move select { font: inherit; font-size: 0.8rem; max-inline-size: 100%; margin-block-start: 4px; }
.small { font-size: 0.8rem; }
.sum { font-variant-numeric: tabular-nums; font-weight: 600; }
tfoot td { background: var(--raavi-bg); border-block-start: 2px solid var(--raavi-border); }
.row-actions { white-space: nowrap; }
.col-actions { display: inline-flex; }
.visually-hidden { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip: rect(0 0 0 0); }
th:first-child, td:first-child { position: sticky; inset-inline-start: 0; background: var(--raavi-surface); z-index: 1; }
thead th { position: sticky; inset-block-start: 0; z-index: 2; background: var(--raavi-surface); }
.col { display: grid; grid-template-columns: 1fr 9rem auto; gap: var(--raavi-space-2); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: var(--raavi-space-2); align-items: start; }
.col textarea { grid-column: 1 / -1; font: inherit; }
.buttons { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.muted { color: var(--raavi-text-muted); margin: 0; }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); }
</style>
