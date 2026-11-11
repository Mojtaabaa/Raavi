<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import Message from 'primevue/message'
import { jiraLinksApi, type PageJiraLink, type TextQuoteInput } from '@/core/api/integrations'

const props = defineProps<{ contentId: string; canEdit: boolean; pendingQuote: TextQuoteInput | null }>()
const emit = defineEmits<{ locate: [exact: string, prefix: string | null, suffix: string | null]; clearQuote: [] }>()

const { t } = useI18n()
const links = ref<PageJiraLink[]>([])
const loading = ref(false)
const error = ref<string | null>(null)
const createOpen = ref(false)
const linkOpen = ref(false)
const connectors = ref<{ id: string; name: string }[]>([])
const projects = ref<{ id: string; key: string; name: string }[]>([])
const types = ref<{ id: string; name: string; subtask?: boolean }[]>([])
const connectorId = ref<string | null>(null)
const projectKey = ref<string | null>(null)
const issueTypeId = ref<string | null>(null)
const summary = ref('')
const description = ref('')
const searchQ = ref('')
const searchHits = ref<{ id: string; key: string; summary: string; status: string; url: string }[]>([])
const busy = ref(false)

async function load() {
  loading.value = true
  error.value = null
  try {
    links.value = await jiraLinksApi.list(props.contentId)
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    loading.value = false
  }
}

async function openCreate() {
  createOpen.value = true
  summary.value = props.pendingQuote?.exact?.slice(0, 255) ?? ''
  description.value = ''
  connectors.value = await jiraLinksApi.jiraConnectors()
  connectorId.value = connectors.value[0]?.id ?? null
  if (connectorId.value) await loadProjects()
}

async function loadProjects() {
  if (!connectorId.value) return
  projects.value = await jiraLinksApi.projects(connectorId.value)
  projectKey.value = projects.value[0]?.key ?? null
  if (projectKey.value) await loadTypes()
}

async function loadTypes() {
  if (!connectorId.value || !projectKey.value) return
  types.value = await jiraLinksApi.issueTypes(connectorId.value, projectKey.value)
  issueTypeId.value = types.value.find((x) => !x.subtask)?.id ?? types.value[0]?.id ?? null
}

async function submitCreate() {
  if (!connectorId.value || !projectKey.value || !issueTypeId.value || !summary.value.trim()) return
  busy.value = true
  try {
    await jiraLinksApi.create(props.contentId, {
      connectorId: connectorId.value,
      projectKey: projectKey.value,
      issueTypeId: issueTypeId.value,
      summary: summary.value.trim(),
      description: description.value || undefined,
      quote: props.pendingQuote ?? undefined,
    })
    createOpen.value = false
    emit('clearQuote')
    await load()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
  }
}

async function openLink() {
  linkOpen.value = true
  searchQ.value = ''
  searchHits.value = []
  connectors.value = await jiraLinksApi.jiraConnectors()
  connectorId.value = connectors.value[0]?.id ?? null
}

async function runSearch() {
  if (!connectorId.value || !searchQ.value.trim()) return
  searchHits.value = await jiraLinksApi.search(connectorId.value, searchQ.value.trim())
}

async function pickHit(key: string) {
  if (!connectorId.value) return
  busy.value = true
  try {
    await jiraLinksApi.link(props.contentId, { connectorId: connectorId.value, issueKeyOrId: key, quote: props.pendingQuote ?? undefined })
    linkOpen.value = false
    emit('clearQuote')
    await load()
  } catch (e) {
    error.value = e instanceof Error ? e.message : String(e)
  } finally {
    busy.value = false
  }
}

async function unlink(link: PageJiraLink) {
  if (!confirm(t('jiraLinks.confirmUnlink', { key: link.issueKey }))) return
  await jiraLinksApi.unlink(props.contentId, link.id)
  await load()
}

watch(() => props.contentId, load)
onMounted(load)

defineExpose({ openCreate, openLink, reload: load })
</script>

<template>
  <section class="jira-links" :aria-label="t('jiraLinks.title')">
    <header>
      <h2>{{ t('jiraLinks.title') }}</h2>
      <div v-if="canEdit" class="actions">
        <Button size="small" icon="pi pi-plus" :label="t('jiraLinks.create')" data-testid="jira-create" @click="openCreate" />
        <Button size="small" text icon="pi pi-link" :label="t('jiraLinks.linkExisting')" data-testid="jira-link" @click="openLink" />
      </div>
    </header>
    <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>
    <p v-if="loading" class="muted">{{ t('jiraLinks.loading') }}</p>
    <p v-else-if="links.length === 0" class="muted">{{ t('jiraLinks.empty') }}</p>
    <ul v-else class="list">
      <li v-for="l in links" :key="l.id" class="row">
        <div class="main">
          <a v-if="l.issueUrl" :href="l.issueUrl" target="_blank" rel="noopener noreferrer">{{ l.issueKey }}</a>
          <strong v-else>{{ l.issueKey }}</strong>
          <span class="muted">{{ l.summary }}</span>
          <span class="chip">{{ l.status }}</span>
          <span class="chip">{{ l.source === 'CreatedFromRaavi' ? t('jiraLinks.createdHere') : t('jiraLinks.linkedHere') }}</span>
          <span class="chip muted">{{ l.syncState }}</span>
        </div>
        <div class="row-actions">
          <Button v-if="l.contexts[0]" size="small" text :label="t('jiraLinks.showQuote')" @click="emit('locate', l.contexts[0].exact, l.contexts[0].prefix, l.contexts[0].suffix)" />
          <Button size="small" text icon="pi pi-refresh" :aria-label="t('jiraLinks.refresh')" @click="jiraLinksApi.refresh(contentId, l.id).then(load)" />
          <Button v-if="canEdit" size="small" text severity="danger" icon="pi pi-times" :aria-label="t('jiraLinks.unlink')" @click="unlink(l)" />
        </div>
      </li>
    </ul>

    <Dialog v-model:visible="createOpen" modal :header="t('jiraLinks.create')" class="jira-dialog">
      <div class="form">
        <label v-if="connectors.length > 1">{{ t('jiraLinks.connector') }}
          <Select v-model="connectorId" :options="connectors" option-label="name" option-value="id" @change="loadProjects" />
        </label>
        <label>{{ t('jiraLinks.project') }}
          <Select v-model="projectKey" :options="projects" option-label="name" option-value="key" @change="loadTypes" />
        </label>
        <label>{{ t('jiraLinks.issueType') }}
          <Select v-model="issueTypeId" :options="types" option-label="name" option-value="id" />
        </label>
        <label>{{ t('jiraLinks.summary') }}
          <InputText v-model="summary" class="w-full" />
        </label>
        <label>{{ t('jiraLinks.description') }}
          <Textarea v-model="description" rows="5" class="w-full" auto-resize />
        </label>
      </div>
      <template #footer>
        <Button text :label="t('common.cancel')" @click="createOpen = false" />
        <Button :label="t('jiraLinks.create')" :loading="busy" data-testid="jira-create-submit" @click="submitCreate" />
      </template>
    </Dialog>

    <Dialog v-model:visible="linkOpen" modal :header="t('jiraLinks.linkExisting')" class="jira-dialog">
      <div class="form">
        <label v-if="connectors.length > 1">{{ t('jiraLinks.connector') }}
          <Select v-model="connectorId" :options="connectors" option-label="name" option-value="id" />
        </label>
        <form class="search" @submit.prevent="runSearch">
          <InputText v-model="searchQ" :placeholder="t('jiraLinks.searchHint')" />
          <Button type="submit" icon="pi pi-search" :label="t('jiraLinks.search')" />
        </form>
        <ul class="hits">
          <li v-for="h in searchHits" :key="h.id">
            <button type="button" @click="pickHit(h.key)">
              <strong>{{ h.key }}</strong> {{ h.summary }} <span class="muted">{{ h.status }}</span>
            </button>
          </li>
        </ul>
      </div>
    </Dialog>
  </section>
</template>

<style scoped>
.jira-links { display: grid; gap: var(--raavi-space-2); border-block-start: 1px solid var(--raavi-border); padding-block-start: var(--raavi-space-3); }
header { display: flex; flex-wrap: wrap; justify-content: space-between; gap: var(--raavi-space-2); align-items: center; }
h2 { margin: 0; font-size: 1rem; }
.actions { display: flex; gap: var(--raavi-space-2); }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.row { display: flex; flex-wrap: wrap; justify-content: space-between; gap: var(--raavi-space-2); align-items: center; }
.main { display: flex; flex-wrap: wrap; gap: var(--raavi-space-2); align-items: baseline; }
.chip { font-size: 0.85rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding-inline: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); }
.form { display: grid; gap: var(--raavi-space-3); }
.form label { display: grid; gap: var(--raavi-space-1); }
.search { display: flex; gap: var(--raavi-space-2); }
.hits { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-1); }
.hits button { inline-size: 100%; text-align: start; background: transparent; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: var(--raavi-space-2); cursor: pointer; }
.w-full { inline-size: 100%; }
</style>
