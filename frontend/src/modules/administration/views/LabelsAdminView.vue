<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Textarea from 'primevue/textarea'
import Select from 'primevue/select'
import { errorMessage } from '@/core/api/errors'
import { wikiApi, type LabelAnalytics, type LabelInfo, type LabelMergePreview } from '@/core/api/wiki'

const { t, te } = useI18n()
const toast = useToast()
const rows = ref<LabelInfo[]>([])
const analytics = ref<LabelAnalytics | null>(null)
const q = ref('')
const loading = ref(false)
const showEdit = ref(false)
const editing = ref<LabelInfo | 'new' | null>(null)
const form = ref({ displayName: '', description: '', color: '#3b82f6', aliases: '', parentId: null as string | null })
const mergeOpen = ref(false)
const mergeSource = ref<string | null>(null)
const mergeDest = ref<string | null>(null)
const mergePreview = ref<LabelMergePreview | null>(null)

async function load() {
  loading.value = true
  try {
    rows.value = await wikiApi.adminLabels({ q: q.value || undefined, take: 200 })
    analytics.value = await wikiApi.adminLabelAnalytics()
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  } finally {
    loading.value = false
  }
}

function openNew() {
  editing.value = 'new'
  form.value = { displayName: '', description: '', color: '#3b82f6', aliases: '', parentId: null }
  showEdit.value = true
}

function openEdit(row: LabelInfo) {
  editing.value = row
  form.value = {
    displayName: row.displayName,
    description: row.description ?? '',
    color: row.color ?? '#3b82f6',
    aliases: row.aliases.join(', '),
    parentId: row.parentId,
  }
  showEdit.value = true
}

async function save() {
  const body = {
    displayName: form.value.displayName,
    description: form.value.description || null,
    color: form.value.color || null,
    parentId: form.value.parentId,
    aliases: form.value.aliases.split(',').map((a) => a.trim()).filter(Boolean),
  }
  try {
    if (editing.value === 'new') await wikiApi.adminCreateLabel(body)
    else if (editing.value) await wikiApi.adminUpdateLabel(editing.value.id, body)
    showEdit.value = false
    toast.add({ severity: 'success', summary: t('common.saved'), life: 2500 })
    await load()
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
}

async function archive(row: LabelInfo) {
  try {
    if (row.archivedAt) await wikiApi.adminRestoreLabel(row.id)
    else await wikiApi.adminArchiveLabel(row.id)
    await load()
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
}

async function remove(row: LabelInfo) {
  try {
    await wikiApi.adminDeleteLabel(row.id)
    await load()
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
}

async function previewMerge() {
  if (!mergeSource.value || !mergeDest.value) return
  try {
    mergePreview.value = await wikiApi.adminMergeLabelsPreview(mergeSource.value, mergeDest.value)
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
}

async function applyMerge() {
  if (!mergeSource.value || !mergeDest.value) return
  try {
    await wikiApi.adminMergeLabels(mergeSource.value, mergeDest.value)
    mergeOpen.value = false
    mergePreview.value = null
    toast.add({ severity: 'success', summary: t('wiki.labels.merged'), life: 2500 })
    await load()
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
}

onMounted(load)
</script>

<template>
  <section class="page" data-testid="labels-admin">
    <header class="head">
      <h1>{{ t('wiki.labels.adminTitle') }}</h1>
      <div class="actions">
        <InputText v-model="q" :placeholder="t('common.search')" data-testid="labels-search" @keyup.enter="load" />
        <Button :label="t('common.search')" outlined @click="load" />
        <Button :label="t('wiki.labels.create')" icon="pi pi-plus" @click="openNew" data-testid="label-create" />
        <Button :label="t('wiki.labels.merge')" outlined icon="pi pi-arrow-right-arrow-left" @click="mergeOpen = true" />
      </div>
    </header>

    <div v-if="analytics" class="stats" data-testid="label-analytics">
      <div><strong>{{ analytics.totalActive }}</strong> {{ t('wiki.labels.statActive') }}</div>
      <div><strong>{{ analytics.archived }}</strong> {{ t('wiki.labels.statArchived') }}</div>
      <div><strong>{{ analytics.unused }}</strong> {{ t('wiki.labels.statUnused') }}</div>
      <div><strong>{{ analytics.pagesWithoutLabels }}</strong> {{ t('wiki.labels.statUntagged') }}</div>
    </div>

    <p v-if="!loading && rows.length === 0" class="muted">{{ t('wiki.labels.emptyAdmin') }}</p>
    <table v-else class="table">
      <thead>
        <tr>
          <th>{{ t('wiki.labels.name') }}</th>
          <th>{{ t('wiki.labels.usage') }}</th>
          <th>{{ t('wiki.labels.status') }}</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="r in rows" :key="r.id">
          <td>
            <span v-if="r.color" class="swatch" :style="{ background: r.color }" />
            <RouterLink :to="{ name: 'label', params: { name: r.slug } }">{{ r.displayName }}</RouterLink>
            <small class="muted"> {{ r.slug }}</small>
          </td>
          <td>{{ r.usageCount }}</td>
          <td>{{ r.archivedAt ? t('wiki.labels.archived') : t('wiki.labels.active') }}</td>
          <td class="row-actions">
            <Button icon="pi pi-pencil" text rounded :aria-label="t('common.edit')" @click="openEdit(r)" />
            <Button icon="pi pi-inbox" text rounded :aria-label="t('wiki.labels.archive')" @click="archive(r)" />
            <Button icon="pi pi-trash" text rounded severity="danger" :aria-label="t('common.delete')" @click="remove(r)" />
          </td>
        </tr>
      </tbody>
    </table>

    <Dialog v-model:visible="showEdit" modal :header="editing === 'new' ? t('wiki.labels.create') : t('wiki.labels.edit')" class="dlg">
      <form class="form" @submit.prevent="save">
        <label>{{ t('wiki.labels.name') }}</label>
        <InputText v-model="form.displayName" required data-testid="label-name" />
        <label>{{ t('wiki.labels.description') }}</label>
        <Textarea v-model="form.description" rows="3" />
        <label>{{ t('wiki.labels.color') }}</label>
        <InputText v-model="form.color" dir="ltr" />
        <label>{{ t('wiki.labels.parent') }}</label>
        <Select v-model="form.parentId" :options="rows.filter((r) => r.id !== (editing !== 'new' && editing ? editing.id : ''))" option-label="displayName" option-value="id" show-clear :placeholder="t('wiki.labels.noParent')" />
        <label>{{ t('wiki.labels.aliases') }}</label>
        <InputText v-model="form.aliases" :placeholder="t('wiki.labels.aliasesHint')" />
        <Button type="submit" :label="t('common.save')" data-testid="label-save" />
      </form>
    </Dialog>

    <Dialog v-model:visible="mergeOpen" modal :header="t('wiki.labels.merge')" class="dlg">
      <div class="form">
        <label>{{ t('wiki.labels.mergeSource') }}</label>
        <Select v-model="mergeSource" :options="rows" option-label="displayName" option-value="id" />
        <label>{{ t('wiki.labels.mergeDest') }}</label>
        <Select v-model="mergeDest" :options="rows" option-label="displayName" option-value="id" />
        <Button :label="t('wiki.labels.mergePreview')" outlined @click="previewMerge" />
        <p v-if="mergePreview" class="muted" data-testid="merge-preview">
          {{ t('wiki.labels.mergeExplain', { pages: mergePreview.pageLinks, aliases: mergePreview.aliasesToMove, dup: mergePreview.duplicateLinks }) }}
        </p>
        <Button :label="t('wiki.labels.mergeConfirm')" :disabled="!mergePreview" severity="danger" @click="applyMerge" data-testid="merge-confirm" />
      </div>
    </Dialog>
  </section>
</template>

<style scoped>
.page { max-inline-size: 70rem; margin-inline: auto; display: grid; gap: var(--raavi-space-4); }
.head { display: flex; flex-wrap: wrap; justify-content: space-between; gap: var(--raavi-space-3); align-items: center; }
.actions { display: flex; flex-wrap: wrap; gap: var(--raavi-space-2); }
.stats { display: flex; flex-wrap: wrap; gap: var(--raavi-space-4); }
.table { width: 100%; border-collapse: collapse; }
.table th, .table td { text-align: start; padding: var(--raavi-space-2); border-block-end: 1px solid var(--raavi-border); }
.swatch { display: inline-block; inline-size: 0.75rem; block-size: 0.75rem; margin-inline-end: 0.35rem; border-radius: 2px; vertical-align: middle; }
.row-actions { white-space: nowrap; }
.form { display: grid; gap: var(--raavi-space-2); min-inline-size: min(24rem, 90vw); }
.muted { color: var(--raavi-text-muted); }
.dlg :deep(.p-dialog-content) { overflow: visible; }
</style>
