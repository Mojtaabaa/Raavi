<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Textarea from 'primevue/textarea'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Checkbox from 'primevue/checkbox'
import Tag from 'primevue/tag'
import { SPACE_TYPES, wikiApi, type SpaceSummary, type SpaceType, type SpaceVisibility } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { opsApi, type SpaceImportResult } from '@/core/api/ops'
import { useAuthStore } from '@/stores/auth'

const { t, te } = useI18n()
const router = useRouter()
const toast = useToast()
const auth = useAuthStore()

const spaces = ref<SpaceSummary[]>([])
const loading = ref(true)
const filter = ref('')
const includeArchived = ref(false)
const administration = ref(false)
const canCreate = ref(false)

const createOpen = ref(false)
const importOpen = ref(false)
const importFile = ref<File | null>(null)
const importKey = ref('')
const importName = ref('')
const importing = ref(false)
const imported = ref<SpaceImportResult | null>(null)

async function runImport() {
  if (!importFile.value) return
  importing.value = true
  try {
    imported.value = await opsApi.importSpace(importFile.value, importKey.value, importName.value)
    await load()
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
  } finally {
    importing.value = false
  }
}

function openImported() {
  const key = imported.value?.key
  importOpen.value = false
  imported.value = null
  importFile.value = null
  if (key) router.push({ name: 'space', params: { key } })
}
const form = ref({ name: '', key: '', description: '', type: 'Team' as SpaceType, visibility: 'Open' as SpaceVisibility })
const keyTouched = ref(false)
const saving = ref(false)

const typeOptions = computed(() => SPACE_TYPES.map((v) => ({ value: v, label: t(`wiki.spaceTypes.${v}`) })))
const visibilityOptions = computed(() => (['Open', 'Private'] as const).map((v) => ({ value: v, label: t(`wiki.visibility.${v}`) })))

async function load() {
  loading.value = true
  try {
    spaces.value = await wikiApi.spaces({ includeArchived: includeArchived.value, administration: administration.value })
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  } finally {
    loading.value = false
  }
}

onMounted(async () => {
  canCreate.value = (await wikiApi.capabilities().catch(() => ({ canCreateSpaces: false }))).canCreateSpaces
  await load()
})
watch([includeArchived, administration], load)

/** Suggests a key from the Latin letters of the name (users of Persian names type their own key). */
watch(
  () => form.value.name,
  (name) => {
    if (keyTouched.value) return
    const letters = name.toUpperCase().replace(/[^A-Z0-9]/g, '')
    form.value.key = /^[A-Z]/.test(letters) ? letters.slice(0, 10) : ''
  },
)

const visible = computed(() => {
  const q = filter.value.trim().toLowerCase()
  return q ? spaces.value.filter((s) => s.name.toLowerCase().includes(q) || s.key.toLowerCase().includes(q)) : spaces.value
})

async function create() {
  saving.value = true
  try {
    const space = await wikiApi.createSpace({ ...form.value, key: form.value.key.trim().toUpperCase(), description: form.value.description || null })
    createOpen.value = false
    await router.push({ name: 'space', params: { key: space.key } })
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <section class="page">
    <header class="page-head">
      <h1>{{ t('wiki.spaces.title') }}</h1>
      <div v-if="canCreate" class="head-actions">
        <Button outlined icon="pi pi-upload" :label="t('spaceTransfer.import')" data-testid="import-space" @click="importOpen = true" />
        <Button icon="pi pi-plus" :label="t('wiki.spaces.create')" data-testid="create-space" @click="createOpen = true" />
      </div>
    </header>

    <div class="filters">
      <label for="space-filter" class="sr-only">{{ t('wiki.spaces.filter') }}</label>
      <InputText id="space-filter" v-model="filter" :placeholder="t('wiki.spaces.filter')" />
      <div class="row">
        <Checkbox v-model="includeArchived" input-id="space-archived" binary />
        <label for="space-archived">{{ t('wiki.spaces.includeArchived') }}</label>
      </div>
      <div v-if="auth.me?.isTenantAdministrator" class="row">
        <Checkbox v-model="administration" input-id="space-admin" binary />
        <label for="space-admin">{{ t('wiki.spaces.administration') }}</label>
      </div>
    </div>

    <DataTable :value="visible" :loading="loading" data-key="id" size="small" striped-rows>
      <template #empty>{{ t('wiki.spaces.empty') }}</template>
      <Column :header="t('wiki.spaces.name')">
        <template #body="{ data }">
          <RouterLink :to="{ name: 'space', params: { key: data.key } }">{{ data.name }}</RouterLink>
          <Tag v-if="data.status === 'Archived'" severity="secondary" :value="t('wiki.spaces.archived')" class="tag" />
        </template>
      </Column>
      <Column field="key" :header="t('wiki.spaces.key')" />
      <Column :header="t('wiki.spaces.type')">
        <template #body="{ data }">{{ t(`wiki.spaceTypes.${data.type}`) }}</template>
      </Column>
      <Column field="description" :header="t('wiki.spaces.description')" />
    </DataTable>

    <Dialog v-model:visible="importOpen" modal :header="t('spaceTransfer.import')" :style="{ inlineSize: 'min(34rem, 95vw)' }">
      <form v-if="!imported" class="form" data-testid="import-form" @submit.prevent="runImport">
        <p class="muted">{{ t('spaceTransfer.importHelp') }}</p>
        <label for="imp-file">{{ t('spaceTransfer.file') }}</label>
        <input id="imp-file" type="file" accept=".zip,application/zip" data-testid="import-file" @change="importFile = ($event.target as HTMLInputElement).files?.[0] ?? null" />
        <label for="imp-key">{{ t('spaceTransfer.newKey') }}</label>
        <InputText id="imp-key" v-model="importKey" maxlength="32" dir="ltr" data-testid="import-key" />
        <small class="muted">{{ t('spaceTransfer.newKeyHelp') }}</small>
        <label for="imp-name">{{ t('spaceTransfer.newName') }}</label>
        <InputText id="imp-name" v-model="importName" maxlength="200" />
        <Button type="submit" icon="pi pi-upload" :label="t('spaceTransfer.import')" :loading="importing" :disabled="!importFile" data-testid="import-run" />
      </form>
      <div v-else class="form" data-testid="import-result">
        <p>{{ t('spaceTransfer.done', { pages: imported.pages, attachments: imported.attachments, comments: imported.comments }) }}</p>
        <p v-if="Object.keys(imported.notImported).length" class="muted">{{ t('spaceTransfer.notImported', { what: Object.keys(imported.notImported).map((k) => t(`spaceTransfer.kinds.${k}`)).join('، ') }) }}</p>
        <ul v-if="imported.warnings.length" class="warnings"><li v-for="w in imported.warnings" :key="w">{{ w }}</li></ul>
        <p class="muted">{{ t('spaceTransfer.permissionsNote') }}</p>
        <Button icon="pi pi-arrow-right" :label="t('spaceTransfer.open')" @click="openImported" />
      </div>
    </Dialog>

    <Dialog v-model:visible="createOpen" modal :header="t('wiki.spaces.create')" :style="{ inlineSize: 'min(32rem, 95vw)' }">
      <form class="form" @submit.prevent="create">
        <label for="sp-name">{{ t('wiki.spaces.name') }}</label>
        <InputText id="sp-name" v-model="form.name" required maxlength="200" />
        <label for="sp-key">{{ t('wiki.spaces.key') }}</label>
        <InputText id="sp-key" v-model="form.key" required maxlength="32" dir="ltr" aria-describedby="sp-key-help" @input="keyTouched = true" />
        <small id="sp-key-help" class="muted">{{ t('wiki.spaces.keyHelp') }}</small>
        <label for="sp-type">{{ t('wiki.spaces.type') }}</label>
        <Select v-model="form.type" input-id="sp-type" :options="typeOptions" option-label="label" option-value="value" />
        <span id="sp-vis-label">{{ t('wiki.spaces.visibility') }}</span>
        <SelectButton v-model="form.visibility" :options="visibilityOptions" option-label="label" option-value="value" :allow-empty="false" aria-labelledby="sp-vis-label" />
        <small class="muted">{{ t(`wiki.visibility.${form.visibility}Help`) }}</small>
        <label for="sp-desc">{{ t('wiki.spaces.description') }}</label>
        <Textarea id="sp-desc" v-model="form.description" rows="3" maxlength="2000" auto-resize />
        <Button type="submit" :label="t('common.create')" :loading="saving" />
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.page { max-inline-size: 72rem; margin-inline: auto; display: grid; gap: var(--raavi-space-4); }
.page-head { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-3); }
h1 { margin: 0; font-size: 1.4rem; }
.filters { display: flex; flex-wrap: wrap; align-items: center; gap: var(--raavi-space-4); }
.row { display: flex; align-items: center; gap: var(--raavi-space-2); }
.form { display: grid; gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); }
.head-actions { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.warnings { margin: 0; padding-inline-start: 1.2rem; max-block-size: 10rem; overflow: auto; font-size: 0.85rem; }
.tag { margin-inline-start: var(--raavi-space-2); }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
</style>
