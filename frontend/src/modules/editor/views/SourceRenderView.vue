<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'
import { renderMath, renderMermaid } from '../renderers'

/**
 * Mermaid diagrams and KaTeX formulas: source is stored, rendering happens client-side in strict/sandboxed mode
 * (KaTeX `trust: false`; Mermaid `securityLevel: 'sandbox'` renders inside a sandboxed iframe).
 */
const props = defineProps(nodeViewProps)
const { t } = useI18n()
const kind = computed(() => props.node.type.name as 'mermaid' | 'mathInline' | 'mathBlock')
const attr = computed(() => (kind.value === 'mermaid' ? 'source' : 'latex'))
const value = computed(() => String(props.node.attrs[attr.value] ?? ''))
const output = ref<HTMLElement | null>(null)
const error = ref(false)
const editing = ref(props.editor.isEditable && value.value === '')

async function render() {
  await nextTick()
  if (!output.value) return
  error.value = false
  try {
    if (kind.value === 'mermaid') await renderMermaid(output.value, value.value)
    else renderMath(output.value, value.value, kind.value === 'mathBlock')
  } catch {
    error.value = true
  }
}

onMounted(render)
watch(value, render)
</script>

<template>
  <NodeViewWrapper :as="kind === 'mathInline' ? 'span' : 'div'" :class="['raavi-source', `raavi-${kind}`]" contenteditable="false" dir="ltr">
    <span v-if="!value && !editing" class="placeholder" @click="editing = props.editor.isEditable">{{ t(`editor.${kind}.empty`) }}</span>
    <span ref="output" class="output" :title="props.editor.isEditable ? t('editor.source.editHint') : undefined" @dblclick="editing = props.editor.isEditable" />
    <span v-if="error" class="error" role="alert">{{ t('editor.source.invalid') }}</span>
    <span v-if="editing" class="source-editor">
      <textarea
        :value="value"
        :rows="kind === 'mathInline' ? 1 : 6"
        :aria-label="t(`editor.${kind}.source`)"
        spellcheck="false"
        @input="(e) => props.updateAttributes({ [attr]: (e.target as HTMLTextAreaElement).value })"
      />
      <button type="button" @click="editing = false">{{ t('common.close') }}</button>
    </span>
  </NodeViewWrapper>
</template>

<style scoped>
.raavi-mermaid, .raavi-mathBlock { display: block; margin-block: var(--raavi-space-2); text-align: center; }
.raavi-mathInline { display: inline; }
.placeholder { color: var(--raavi-text-muted); cursor: pointer; font-style: italic; }
.error { color: #b91c1c; font-size: 0.85rem; }
.source-editor { display: flex; gap: 4px; align-items: flex-start; text-align: start; }
.source-editor textarea { flex: 1; font-family: ui-monospace, monospace; font-size: 0.85rem; min-inline-size: 12rem; }
.output :deep(iframe) { border: 0; inline-size: 100%; min-block-size: 200px; }
</style>
