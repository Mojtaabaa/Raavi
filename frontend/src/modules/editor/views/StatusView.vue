<script setup lang="ts">
import { ref } from 'vue'
import { NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'

const props = defineProps(nodeViewProps)
const { t } = useI18n()
const colors = ['neutral', 'blue', 'green', 'yellow', 'red', 'purple'] as const
const editing = ref(false)
</script>

<template>
  <NodeViewWrapper as="span" class="status-wrap">
    <span :class="['raavi-status', `raavi-status-${node.attrs.color}`]" contenteditable="false" @dblclick="props.editor.isEditable && (editing = !editing)">
      {{ node.attrs.text || t('editor.status.placeholder') }}
    </span>
    <span v-if="editing && props.editor.isEditable" class="status-editor" contenteditable="false">
      <input
        :value="node.attrs.text"
        maxlength="60"
        :aria-label="t('editor.status.text')"
        @input="(e) => props.updateAttributes({ text: (e.target as HTMLInputElement).value.toUpperCase() })"
        @keydown.enter.prevent="editing = false"
      />
      <select :value="node.attrs.color" :aria-label="t('editor.status.color')" @change="(e) => props.updateAttributes({ color: (e.target as HTMLSelectElement).value })">
        <option v-for="c in colors" :key="c" :value="c">{{ t(`editor.status.colors.${c}`) }}</option>
      </select>
      <button type="button" @click="editing = false">{{ t('common.close') }}</button>
    </span>
  </NodeViewWrapper>
</template>

<style scoped>
.raavi-status { display: inline-block; padding: 0 0.4em; border-radius: 3px; font-size: 0.75em; font-weight: 700; letter-spacing: 0.02em; vertical-align: middle; cursor: default; }
.raavi-status-neutral { background: #e5e7eb; color: #374151; }
.raavi-status-blue { background: #dbeafe; color: #1e40af; }
.raavi-status-green { background: #dcfce7; color: #166534; }
.raavi-status-yellow { background: #fef9c3; color: #854d0e; }
.raavi-status-red { background: #fee2e2; color: #991b1b; }
.raavi-status-purple { background: #ede9fe; color: #5b21b6; }
.status-editor { display: inline-flex; gap: 4px; margin-inline-start: 4px; font-size: 0.8rem; }
.status-editor input { inline-size: 9rem; }
</style>
