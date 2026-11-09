<script setup lang="ts">
import { prefixed } from '@/core/runtime/base'
import { computed } from 'vue'
import { NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'

/** Images are page attachments served by the API after a View check; no external URLs (no tracking pixels). */
const props = defineProps(nodeViewProps)
const { t } = useI18n()
const src = computed(() => (props.node.attrs.attachmentId ? prefixed(`/api/v1/attachments/${encodeURIComponent(String(props.node.attrs.attachmentId))}`) : null))
const widths = [null, 160, 320, 480, 640, 960]
</script>

<template>
  <NodeViewWrapper class="raavi-image" :class="{ selected: props.selected }">
    <img v-if="src" :src="src" :alt="node.attrs.alt" :width="node.attrs.width ?? undefined" loading="lazy" draggable="false" />
    <span v-else class="missing">{{ t('editor.image.missing') }}</span>
    <span v-if="props.editor.isEditable && props.selected" class="image-tools" contenteditable="false">
      <input :value="node.attrs.alt" maxlength="500" :placeholder="t('editor.image.alt')" :aria-label="t('editor.image.alt')" dir="auto" @input="(e) => props.updateAttributes({ alt: (e.target as HTMLInputElement).value })" />
      <select :value="node.attrs.width ?? ''" :aria-label="t('editor.image.width')" @change="(e) => props.updateAttributes({ width: (e.target as HTMLSelectElement).value ? Number((e.target as HTMLSelectElement).value) : null })">
        <option v-for="w in widths" :key="String(w)" :value="w ?? ''">{{ w ? `${w}px` : t('editor.image.original') }}</option>
      </select>
    </span>
  </NodeViewWrapper>
</template>

<style scoped>
.raavi-image { margin-block: var(--raavi-space-2); }
.raavi-image img { max-inline-size: 100%; block-size: auto; border-radius: var(--raavi-radius-sm); }
.raavi-image.selected img { outline: 2px solid var(--p-primary-color); }
.image-tools { display: flex; gap: 4px; margin-block-start: 4px; }
.missing { color: var(--raavi-text-muted); }
</style>
