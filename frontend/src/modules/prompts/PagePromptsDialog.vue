<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Dialog from 'primevue/dialog'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Tag from 'primevue/tag'
import { promptsApi, type PromptContextRequest, type PromptSummary } from '@/core/api/prompts'
import { errorMessage } from '@/core/api/errors'
import PromptRunDialog from './PromptRunDialog.vue'

/**
 * "AI Prompts" on a page: your own prompts and the workspace's public ones that fit this page (and the selected text, if any), searchable,
 * each marked Personal or Public. Choosing one opens the run dialog with the page as context.
 */
const props = defineProps<{ visible: boolean; pageId: string; spaceKey: string; selectedText?: string | null }>()
const emit = defineEmits<{ 'update:visible': [boolean]; insert: [text: string] }>()
const { t, te } = useI18n()
const toast = useToast()

const q = ref('')
const items = ref<PromptSummary[]>([])
const loading = ref(false)
const chosen = ref<PromptSummary | null>(null)
const runOpen = ref(false)

const hasSelection = computed(() => !!props.selectedText?.trim())
const fits = (p: PromptSummary) => p.contexts.some((c) => c === 'page' || c === 'section' || c === 'space' || (c === 'selection' && hasSelection.value))

async function load() {
  loading.value = true
  try {
    const [mine, pub] = await Promise.all([
      promptsApi.list({ scope: 'mine', q: q.value.trim() || undefined, sort: 'updated', take: 50 }),
      promptsApi.list({ scope: 'public', q: q.value.trim() || undefined, sort: 'newest', take: 50 }),
    ])
    const seen = new Set<string>()
    items.value = [...mine.items.filter((p) => p.status !== 'Draft' || p.hasDraft), ...pub.items].filter((p) => !seen.has(p.id) && seen.add(p.id) && fits(p))
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  } finally {
    loading.value = false
  }
}

let timer: number | undefined
watch(q, () => {
  window.clearTimeout(timer)
  timer = window.setTimeout(load, 300)
})
watch(() => props.visible, (open) => open && load())

function context(p: PromptSummary): PromptContextRequest {
  const useSelection = hasSelection.value && p.contexts.includes('selection')
  const kind = useSelection ? 'selection' : p.contexts.includes('page') ? 'page' : p.contexts.includes('space') ? 'space' : 'section'
  return { kind, pageId: props.pageId, selectedText: useSelection ? props.selectedText : null, spaceKey: props.spaceKey }
}

function choose(p: PromptSummary) {
  chosen.value = p
  runOpen.value = true
}
</script>

<template>
  <Dialog :visible="visible && !runOpen" modal :header="t('prompts.page.title')" :style="{ inlineSize: 'min(40rem, 96vw)' }" @update:visible="emit('update:visible', $event)">
    <div class="list" data-testid="page-prompts">
      <InputText v-model="q" :placeholder="t('prompts.search')" :aria-label="t('prompts.search')" data-testid="page-prompt-search" />
      <p v-if="hasSelection" class="muted">{{ t('prompts.page.selection') }}</p>
      <p v-if="loading" class="muted" role="status">{{ t('common.loading') }}</p>
      <p v-else-if="!items.length" class="muted" data-testid="page-prompts-empty">{{ t('prompts.page.none') }}</p>
      <ul v-else>
        <li v-for="p in items" :key="p.id" :data-testid="`page-prompt-${p.title}`">
          <div class="main">
            <strong>{{ p.title }}</strong>
            <Tag :severity="p.visibility === 'Tenant' ? 'info' : 'secondary'" :value="t(`prompts.visibility.${p.visibility}`)" />
            <span v-if="(p.ratingCount ?? 0) > 0" class="rating">★ {{ p.ratingAverage }}</span>
            <p v-if="p.description" class="muted">{{ p.description }}</p>
          </div>
          <Button size="small" icon="pi pi-play" :label="t('prompts.page.use')" :data-testid="`use-${p.title}`" @click="choose(p)" />
        </li>
      </ul>
      <RouterLink :to="{ name: 'prompts' }" class="muted">{{ t('prompts.page.openHub') }}</RouterLink>
    </div>
  </Dialog>
  <PromptRunDialog v-model:visible="runOpen" :prompt="chosen" :context="chosen ? context(chosen) : null" :page-id="pageId" @insert="emit('insert', $event)" @update:visible="(v) => !v && emit('update:visible', false)" />
</template>

<style scoped>
.list { display: grid; gap: var(--raavi-space-2); }
ul { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
li { display: flex; justify-content: space-between; gap: var(--raavi-space-2); align-items: center; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: var(--raavi-space-2) var(--raavi-space-3); }
.main { display: grid; gap: 0.15rem; }
.muted { color: var(--raavi-text-muted); margin: 0; font-size: 0.85rem; }
.rating { color: var(--p-yellow-600, #ca8a04); font-weight: 600; }
</style>
