<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { databasesApi, type CellValue, type ColumnType, type DatabaseInput, type DbColumn, type DbRow, type SpaceDatabase } from '@/core/api/databases'
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

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const canEdit = computed(() => !!database.value?.canEdit)
const columns = computed(() => database.value?.columns ?? [])
const pages = computed(() => Math.max(1, Math.ceil(total.value / PAGE)))

async function loadRows() {
  try {
    const result = await databasesApi.rows(id.value, { q: q.value.trim(), sort: sort.value, dir: dir.value, skip: page.value * PAGE, take: PAGE })
    rows.value = result.rows
    total.value = result.total
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
onMounted(load)
watch(id, load)

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
        <Button as="a" :href="databasesApi.exportUrl(database.id)" icon="pi pi-download" text :label="t('databases.export')" data-testid="export-database" />
      </div>
    </header>

    <div class="scroll">
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
            <td v-if="canEdit"><Button text rounded size="small" severity="danger" icon="pi pi-trash" :aria-label="t('common.delete')" data-testid="delete-row" @click="removeRow(row)" /></td>
          </tr>
        </tbody>
      </table>
    </div>
    <p v-if="!loading && rows.length === 0" class="muted" data-testid="no-rows">{{ q ? t('databases.noMatches') : t('databases.noRows') }}</p>

    <nav v-if="pages > 1" class="pager" :aria-label="t('databases.pages')">
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
          <Button type="button" text rounded severity="danger" icon="pi pi-trash" :aria-label="t('common.delete')" @click="removeColumn(i)" />
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
.col { display: grid; grid-template-columns: 1fr 9rem auto; gap: var(--raavi-space-2); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: var(--raavi-space-2); align-items: start; }
.col textarea { grid-column: 1 / -1; font: inherit; }
.buttons { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.muted { color: var(--raavi-text-muted); margin: 0; }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); }
</style>
