<script setup lang="ts">
import { NodeViewContent, NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'

const props = defineProps(nodeViewProps)
const { t } = useI18n()
const types = ['info', 'note', 'warning', 'success', 'error'] as const
const icons: Record<string, string> = { info: 'pi pi-info-circle', note: 'pi pi-pencil', warning: 'pi pi-exclamation-triangle', success: 'pi pi-check-circle', error: 'pi pi-times-circle' }
</script>

<template>
  <NodeViewWrapper :class="['raavi-panel', `raavi-panel-${node.attrs.type}`]" role="note">
    <div class="panel-head" contenteditable="false">
      <i :class="icons[node.attrs.type] ?? icons.info" aria-hidden="true" />
      <select
        v-if="props.editor.isEditable"
        :value="node.attrs.type"
        :aria-label="t('editor.panel.type')"
        @change="(e) => props.updateAttributes({ type: (e.target as HTMLSelectElement).value })"
      >
        <option v-for="ty in types" :key="ty" :value="ty">{{ t(`editor.panel.types.${ty}`) }}</option>
      </select>
    </div>
    <NodeViewContent class="panel-body" />
  </NodeViewWrapper>
</template>

<style scoped>
.raavi-panel { display: grid; grid-template-columns: auto minmax(0, 1fr); gap: var(--raavi-space-2); padding: var(--raavi-space-3); border-radius: var(--raavi-radius-sm); border-inline-start: 4px solid var(--panel-accent); background: var(--panel-bg); margin-block: var(--raavi-space-2); }
.panel-head { display: flex; flex-direction: column; align-items: center; gap: var(--raavi-space-1); color: var(--panel-accent); }
.panel-head select { font-size: 0.75rem; }
.raavi-panel-info { --panel-accent: #2563eb; --panel-bg: #eff6ff; }
.raavi-panel-note { --panel-accent: #7c3aed; --panel-bg: #f5f3ff; }
.raavi-panel-warning { --panel-accent: #d97706; --panel-bg: #fffbeb; }
.raavi-panel-success { --panel-accent: #16a34a; --panel-bg: #f0fdf4; }
.raavi-panel-error { --panel-accent: #dc2626; --panel-bg: #fef2f2; }
.panel-body :deep(p:last-child) { margin-block-end: 0; }
</style>
