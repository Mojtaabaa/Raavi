<script setup lang="ts">
import { computed, inject, onMounted, provide, ref, watch, type Ref } from 'vue'
import { NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'
import { RouterLink } from 'vue-router'
import { contentRoute, wikiApi, type Page, type RaaviDoc } from '@/core/api/wiki'
import DocumentBody from '@/modules/wiki/components/DocumentBody.vue'

/**
 * Full-page include (Confluence `include` macro). Loads the published body of the target page for the current viewer
 * (View required → 404 becomes "unavailable"). Nesting is capped at 3 and cycles are stopped by page-id chain.
 */
const props = defineProps(nodeViewProps)
const { t } = useI18n()

const MAX_DEPTH = 3
const parentChain = inject<Ref<string[]>>('raaviIncludeChain', ref([]))
const pageId = computed(() => {
  const raw = props.node.attrs.pageId
  return typeof raw === 'string' && raw.length > 0 ? raw : null
})
const chain = computed(() => parentChain.value)
const blocked = computed(() => {
  const id = pageId.value
  if (!id) return false
  return chain.value.includes(id) || chain.value.length >= MAX_DEPTH
})
const nextChain = computed(() => (pageId.value ? [...chain.value, pageId.value] : chain.value))
provide('raaviIncludeChain', nextChain)

const page = ref<Page | null>(null)
const state = ref<'idle' | 'loading' | 'ready' | 'error' | 'blocked'>('idle')
const editing = ref(false)
const draftId = ref(pageId.value ?? '')

async function load() {
  page.value = null
  if (blocked.value) {
    state.value = 'blocked'
    return
  }
  const id = pageId.value
  if (!id) {
    state.value = 'idle'
    return
  }
  state.value = 'loading'
  try {
    page.value = (await wikiApi.content(id)).value
    state.value = 'ready'
  } catch {
    state.value = 'error'
  }
}

onMounted(load)
watch(() => [pageId.value, blocked.value], load)

const body = computed<RaaviDoc | null>(() => page.value?.body ?? null)
const link = computed(() => {
  if (!page.value) return null
  return contentRoute({ id: page.value.id, slug: page.value.slug, spaceKey: page.value.spaceKey })
})

function apply() {
  const id = draftId.value.trim()
  if (!/^[0-9a-fA-F-]{36}$/.test(id)) return
  props.updateAttributes({ pageId: id, spaceKey: '', title: '' })
  editing.value = false
}
</script>

<script lang="ts">
export default { name: 'IncludeView' }
</script>

<template>
  <NodeViewWrapper class="raavi-include" contenteditable="false" data-testid="page-include">
    <header class="head">
      <strong>{{ t('editor.include.title') }}</strong>
      <RouterLink v-if="link && page" class="src" :to="link" dir="auto">{{ page.title }}</RouterLink>
      <span v-else-if="node.attrs.title" class="muted" dir="auto">{{ node.attrs.title }}</span>
    </header>
    <div v-if="editing && editor.isEditable" class="editor">
      <input
        v-model="draftId"
        dir="ltr"
        maxlength="36"
        :placeholder="t('editor.include.pageId')"
        data-testid="include-page-id"
        @keydown.enter.prevent="apply"
      />
      <button type="button" :disabled="!draftId.trim()" data-testid="include-apply" @click="apply">{{ t('common.save') }}</button>
    </div>
    <p v-else-if="state === 'idle'" class="muted">{{ t('editor.include.placeholder') }}</p>
    <p v-else-if="state === 'blocked'" class="muted">{{ t('editor.include.blocked') }}</p>
    <p v-else-if="state === 'error'" class="muted">{{ t('editor.include.unavailable') }}</p>
    <p v-else-if="state === 'loading'" class="muted">{{ t('editor.include.loading') }}</p>
    <div v-else-if="state === 'ready' && body" class="body" data-testid="include-body">
      <DocumentBody :doc="body" />
    </div>
    <button
      v-if="editor.isEditable && !editing"
      type="button"
      class="pick"
      data-testid="include-edit"
      @click="editing = true; draftId = pageId ?? ''"
    >
      {{ t('editor.include.choose') }}
    </button>
  </NodeViewWrapper>
</template>

<style scoped>
.raavi-include {
  border: 1px solid var(--raavi-border);
  border-radius: var(--raavi-radius-sm);
  padding: var(--raavi-space-3);
  margin-block: var(--raavi-space-3);
  background: var(--raavi-surface);
}
.head { display: flex; flex-wrap: wrap; gap: var(--raavi-space-2); align-items: baseline; margin-block-end: var(--raavi-space-2); }
.src { font-weight: 600; color: inherit; }
.muted { color: var(--raavi-text-muted); margin: var(--raavi-space-1) 0; }
.body { border-block-start: 1px solid var(--raavi-border); padding-block-start: var(--raavi-space-2); }
.editor { display: flex; gap: 0.5rem; flex-wrap: wrap; }
.editor input { inline-size: min(24rem, 100%); }
.pick { margin-block-start: var(--raavi-space-2); font-size: 0.85rem; }
</style>
