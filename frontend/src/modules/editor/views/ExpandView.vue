<script setup lang="ts">
import { ref } from 'vue'
import { NodeViewContent, NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'

const props = defineProps(nodeViewProps)
const { t } = useI18n()
// Always open while editing; collapsed by default when reading.
const open = ref(props.editor.isEditable)
</script>

<template>
  <NodeViewWrapper class="raavi-expand">
    <div class="expand-head" contenteditable="false">
      <button type="button" class="toggle" :aria-expanded="open" :aria-label="open ? t('editor.expand.collapse') : t('editor.expand.expand')" @click="open = !open">
        <i :class="open ? 'pi pi-chevron-down' : 'pi pi-chevron-right'" class="flip" aria-hidden="true" />
      </button>
      <input
        v-if="props.editor.isEditable"
        class="title-input"
        :value="node.attrs.title"
        :placeholder="t('editor.expand.titlePlaceholder')"
        maxlength="200"
        dir="auto"
        :aria-label="t('editor.expand.title')"
        @input="(e) => props.updateAttributes({ title: (e.target as HTMLInputElement).value })"
      />
      <span v-else class="title" dir="auto" @click="open = !open">{{ node.attrs.title || t('editor.expand.untitled') }}</span>
    </div>
    <NodeViewContent v-show="open" class="expand-body" />
  </NodeViewWrapper>
</template>

<style scoped>
.raavi-expand { border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: var(--raavi-space-2) var(--raavi-space-3); margin-block: var(--raavi-space-2); }
.expand-head { display: flex; align-items: center; gap: var(--raavi-space-2); }
.toggle { background: none; border: 0; cursor: pointer; padding: 0; color: inherit; }
.title { font-weight: 600; cursor: pointer; }
.title-input { flex: 1; border: 0; border-block-end: 1px dashed var(--raavi-border); font-weight: 600; background: transparent; font: inherit; }
.expand-body { padding-inline-start: var(--raavi-space-5); }
:dir(rtl) .flip.pi-chevron-right { transform: scaleX(-1); }
</style>
