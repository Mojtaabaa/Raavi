<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { contentRoute, wikiApi, type ContentListItem } from '@/core/api/wiki'

/**
 * "Pages with label" list (Confluence `contentbylabel` macro). Fetched through the labels API when the page is viewed, so
 * the list contains only pages the viewer may open and is always current.
 */
const props = defineProps(nodeViewProps)
const { t } = useI18n()
const route = useRoute()
const items = ref<ContentListItem[]>([])
const state = ref<'loading' | 'ready' | 'error'>('loading')

const labels = computed(() => String(props.node.attrs.labels ?? '').split(',').map((l) => l.trim()).filter(Boolean))

async function load() {
  if (labels.value.length === 0) {
    items.value = []
    state.value = 'ready'
    return
  }
  state.value = 'loading'
  try {
    const lists = await Promise.all(labels.value.map((l) => wikiApi.labelContent(l)))
    const seen = new Map<string, ContentListItem>()
    for (const item of lists.flat()) if (!seen.has(item.id)) seen.set(item.id, item)
    const key = typeof route.params.key === 'string' ? route.params.key : null
    items.value = [...seen.values()]
      .filter((i) => !props.node.attrs.currentSpaceOnly || !key || i.spaceKey === key)
      .sort((a, b) => a.title.localeCompare(b.title))
      .slice(0, Math.min(100, Math.max(1, Number(props.node.attrs.maxItems) || 20)))
    state.value = 'ready'
  } catch {
    state.value = 'error'
  }
}
onMounted(load)
watch(() => [props.node.attrs.labels, props.node.attrs.maxItems, props.node.attrs.currentSpaceOnly], load)
</script>

<script lang="ts">
export default { name: 'ContentByLabelView' }
</script>

<template>
  <NodeViewWrapper class="raavi-by-label" contenteditable="false" data-testid="by-label-list">
    <nav :aria-label="t('editor.byLabel.title')">
      <strong>{{ t('editor.byLabel.title') }}: <span dir="auto">{{ labels.join(', ') }}</span></strong>
      <p v-if="state === 'error'" class="muted">{{ t('editor.byLabel.error') }}</p>
      <p v-else-if="state === 'ready' && items.length === 0" class="muted">{{ t('editor.byLabel.empty') }}</p>
      <ul v-if="items.length">
        <li v-for="i in items" :key="i.id">
          <RouterLink :to="contentRoute(i)" dir="auto">{{ i.title }}</RouterLink>
          <span v-if="!node.attrs.currentSpaceOnly" class="muted"> — {{ i.spaceName }}</span>
        </li>
      </ul>
      <div v-if="props.editor.isEditable" class="opt">
        <label>
          {{ t('editor.byLabel.labels') }}
          <input dir="ltr" :value="node.attrs.labels" @change="(e) => props.updateAttributes({ labels: (e.target as HTMLInputElement).value })" />
        </label>
        <label>
          <input type="checkbox" :checked="node.attrs.currentSpaceOnly" @change="(e) => props.updateAttributes({ currentSpaceOnly: (e.target as HTMLInputElement).checked })" />
          {{ t('editor.byLabel.currentSpaceOnly') }}
        </label>
      </div>
    </nav>
  </NodeViewWrapper>
</template>

<style scoped>
.raavi-by-label { border-inline-start: 3px solid var(--raavi-border); padding-inline-start: var(--raavi-space-3); margin-block: var(--raavi-space-3); }
ul { margin: var(--raavi-space-1) 0; padding-inline-start: 1.2rem; }
.muted { color: var(--raavi-text-muted); margin: var(--raavi-space-1) 0; }
.opt { display: flex; gap: var(--raavi-space-3); flex-wrap: wrap; font-size: 0.8rem; color: var(--raavi-text-muted); }
</style>
