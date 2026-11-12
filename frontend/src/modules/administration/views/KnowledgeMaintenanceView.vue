<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { errorMessage } from '@/core/api/errors'
import { wikiApi, type KnowledgeMaintenanceSuggestion } from '@/core/api/wiki'
import { tasksApi } from '@/core/api/tasks'

const { t, te } = useI18n()
const toast = useToast()
const rows = ref<KnowledgeMaintenanceSuggestion[]>([])
const loading = ref(false)
const busy = ref<string | null>(null)

async function load() {
  loading.value = true
  try {
    rows.value = await wikiApi.knowledgeMaintenance(50)
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  } finally {
    loading.value = false
  }
}

function kindLabel(kind: string) {
  const key = `wiki.maintenance.kinds.${kind}`
  return te(key) ? t(key) : kind
}

function kindSeverity(kind: string): 'warn' | 'info' | 'secondary' {
  if (kind === 'review_due') return 'warn'
  if (kind === 'near_duplicate') return 'info'
  return 'secondary'
}

async function createTask(s: KnowledgeMaintenanceSuggestion) {
  const id = `${s.kind}:${s.contentId}:${s.relatedContentId ?? ''}`
  busy.value = id
  try {
    const title =
      s.kind === 'near_duplicate'
        ? t('wiki.maintenance.taskDuplicate', { title: s.title, other: s.relatedTitle ?? '' })
        : t('wiki.maintenance.taskStale', { title: s.title })
    const notes =
      s.kind === 'near_duplicate'
        ? t('wiki.maintenance.notesDuplicate', { other: s.relatedTitle ?? '', score: s.score ?? 0 })
        : s.reason
    await tasksApi.create(s.spaceKey, {
      title,
      notes,
      assigneeId: null,
      dueDate: null,
      contentId: s.contentId,
    })
    toast.add({ severity: 'success', summary: t('wiki.maintenance.taskCreated'), life: 2500 })
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  } finally {
    busy.value = null
  }
}

onMounted(load)
</script>

<template>
  <section class="page" data-testid="knowledge-maintenance">
    <header class="head">
      <div>
        <h1>{{ t('wiki.maintenance.title') }}</h1>
        <p class="muted">{{ t('wiki.maintenance.lead') }}</p>
      </div>
      <Button :label="t('wiki.maintenance.refresh')" icon="pi pi-refresh" outlined :loading="loading" @click="load" />
    </header>

    <p v-if="!loading && rows.length === 0" class="muted" data-testid="maintenance-empty">{{ t('wiki.maintenance.empty') }}</p>
    <table v-else class="table" data-testid="maintenance-table">
      <thead>
        <tr>
          <th>{{ t('wiki.maintenance.kind') }}</th>
          <th>{{ t('wiki.maintenance.page') }}</th>
          <th>{{ t('wiki.maintenance.space') }}</th>
          <th>{{ t('wiki.maintenance.detail') }}</th>
          <th></th>
        </tr>
      </thead>
      <tbody>
        <tr v-for="s in rows" :key="`${s.kind}-${s.contentId}-${s.relatedContentId ?? ''}`">
          <td><Tag :severity="kindSeverity(s.kind)" :value="kindLabel(s.kind)" /></td>
          <td>
            <RouterLink :to="{ name: 'page', params: { key: s.spaceKey, id: s.contentId } }">{{ s.title }}</RouterLink>
          </td>
          <td>{{ s.spaceName }}</td>
          <td>
            <span v-if="s.kind === 'near_duplicate' && s.relatedContentId">
              <RouterLink :to="{ name: 'page', params: { key: s.spaceKey, id: s.relatedContentId } }">{{ s.relatedTitle }}</RouterLink>
              <span v-if="s.score != null" class="muted"> ({{ Math.round(s.score * 100) }}%)</span>
            </span>
            <span v-else class="muted">{{ s.reason }}</span>
          </td>
          <td>
            <Button
              size="small"
              text
              icon="pi pi-check-square"
              :label="t('wiki.maintenance.createTask')"
              :loading="busy === `${s.kind}:${s.contentId}:${s.relatedContentId ?? ''}`"
              :aria-label="t('wiki.maintenance.createTask')"
              @click="createTask(s)"
            />
          </td>
        </tr>
      </tbody>
    </table>
  </section>
</template>

<style scoped>
.page { max-inline-size: 70rem; margin-inline: auto; display: grid; gap: var(--raavi-space-4); }
.head { display: flex; flex-wrap: wrap; justify-content: space-between; gap: var(--raavi-space-3); align-items: flex-start; }
.table { width: 100%; border-collapse: collapse; }
.table th, .table td { text-align: start; padding: var(--raavi-space-2); border-block-end: 1px solid var(--raavi-border); vertical-align: top; }
.muted { color: var(--raavi-text-muted); }
</style>
