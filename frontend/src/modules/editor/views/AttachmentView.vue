<script setup lang="ts">
import { computed } from 'vue'
import { NodeViewWrapper, nodeViewProps } from '@tiptap/vue-3'
import { useI18n } from 'vue-i18n'

const props = defineProps(nodeViewProps)
const { t, n } = useI18n()
const href = computed(() => (props.node.attrs.attachmentId ? `/api/v1/attachments/${encodeURIComponent(String(props.node.attrs.attachmentId))}` : null))
const size = computed(() => {
  const bytes = Number(props.node.attrs.size ?? 0)
  return bytes >= 1048576 ? `${n(bytes / 1048576, { maximumFractionDigits: 1 })} MB` : `${n(Math.max(1, Math.round(bytes / 1024)))} KB`
})
</script>

<template>
  <NodeViewWrapper class="raavi-attachment" contenteditable="false">
    <i class="pi pi-paperclip" aria-hidden="true" />
    <a v-if="href" :href="href" download dir="auto">{{ node.attrs.fileName }}</a>
    <span v-else>{{ node.attrs.fileName }}</span>
    <small class="muted">{{ size }} · {{ node.attrs.mediaType }}</small>
    <span class="sr-only">{{ t('editor.attachment.label') }}</span>
  </NodeViewWrapper>
</template>

<style scoped>
.raavi-attachment { display: inline-flex; align-items: center; gap: var(--raavi-space-2); padding: var(--raavi-space-1) var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); margin-block: var(--raavi-space-1); }
.muted { color: var(--raavi-text-muted); }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); }
</style>
