<script setup lang="ts">
import { NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'

/** Imported content Raavi cannot represent yet: the raw source is shown as escaped text, never as HTML. */
defineProps(nodeViewProps)
const { t } = useI18n()
</script>

<template>
  <NodeViewWrapper class="raavi-unsupported" contenteditable="false">
    <details>
      <summary><i class="pi pi-exclamation-circle" aria-hidden="true" /> {{ t('editor.unsupported.title', { type: node.attrs.sourceType || '?' }) }}</summary>
      <pre dir="ltr">{{ node.attrs.source }}</pre>
    </details>
  </NodeViewWrapper>
</template>

<style scoped>
.raavi-unsupported { border: 1px dashed var(--raavi-border); padding: var(--raavi-space-2); border-radius: var(--raavi-radius-sm); color: var(--raavi-text-muted); margin-block: var(--raavi-space-2); }
pre { white-space: pre-wrap; overflow-wrap: anywhere; font-size: 0.8rem; }
</style>
