<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { whiteboardsApi, type Whiteboard } from '@/core/api/whiteboards'
import { errorMessage } from '@/core/api/errors'

const props = defineProps(nodeViewProps)
const { t, te } = useI18n()
const router = useRouter()
const board = ref<Whiteboard | null>(null)
const error = ref('')
const loading = ref(false)

const whiteboardId = computed(() => String(props.node.attrs.whiteboardId ?? ''))
const editing = ref(!whiteboardId.value)

async function load() {
  error.value = ''
  board.value = null
  if (!whiteboardId.value) return
  loading.value = true
  try {
    board.value = await whiteboardsApi.get(whiteboardId.value)
  } catch (e) {
    error.value = errorMessage(e, t, te)
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(whiteboardId, load)

function open() {
  if (whiteboardId.value) void router.push({ name: 'whiteboard', params: { id: whiteboardId.value } })
}

function apply() {
  const id = draft.value.trim()
  if (!id) return
  props.updateAttributes({ whiteboardId: id })
  editing.value = false
}

const draft = ref(whiteboardId.value)
</script>

<template>
  <NodeViewWrapper as="div" class="wb-embed" data-testid="whiteboard-embed">
    <div v-if="editing && editor.isEditable" class="editor">
      <input v-model="draft" dir="ltr" maxlength="36" :placeholder="t('editor.whiteboard.id')" data-testid="whiteboard-id" @keydown.enter.prevent="apply" />
      <button type="button" :disabled="!draft.trim()" data-testid="whiteboard-apply" @click="apply">{{ t('common.save') }}</button>
    </div>
    <button v-else type="button" class="card" data-testid="whiteboard-card" @click="open" @dblclick="editing = editor.isEditable">
      <span class="icon pi pi-objects-column" aria-hidden="true" />
      <span v-if="loading" class="muted">{{ t('editor.whiteboard.loading') }}</span>
      <span v-else-if="error" class="muted">{{ t('editor.whiteboard.unavailable') }}</span>
      <span v-else-if="board" class="title" dir="auto">{{ board.title }}</span>
      <span v-else class="muted">{{ t('editor.whiteboard.placeholder') }}</span>
    </button>
  </NodeViewWrapper>
</template>

<style scoped>
.wb-embed { margin-block: 0.5rem; }
.card { display: flex; align-items: center; gap: 0.6rem; inline-size: 100%; padding: 0.75rem 1rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: var(--raavi-surface); color: inherit; cursor: pointer; text-align: start; }
.card:hover { border-color: var(--raavi-accent, #3b82f6); }
.icon { color: var(--raavi-icon, inherit); }
.title { font-weight: 600; overflow-wrap: anywhere; }
.muted { color: var(--raavi-text-muted); }
.editor { display: flex; gap: 0.5rem; flex-wrap: wrap; }
.editor input { inline-size: min(24rem, 100%); }
</style>
