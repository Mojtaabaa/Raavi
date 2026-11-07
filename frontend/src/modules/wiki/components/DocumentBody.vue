<script setup lang="ts">
import type { RaaviDoc } from '@/core/api/wiki'
import RaaviEditor from '@/modules/editor/components/RaaviEditor.vue'
import type { AnchorHighlight, TextQuote } from '@/modules/editor/extensions/commentAnchors'

/** Read view of a page body: the editor schema in read-only mode (text is never injected as HTML). */
withDefaults(defineProps<{ doc: RaaviDoc | null; anchors?: AnchorHighlight[] | null; activeAnchor?: string | null }>(), { anchors: null, activeAnchor: null })
defineEmits<{ quote: [quote: TextQuote | null]; activate: [id: string]; located: [ids: string[]] }>()
</script>

<template>
  <RaaviEditor
    :model-value="doc"
    :editable="false"
    :anchors="anchors"
    :active-anchor="activeAnchor"
    @quote="(q) => $emit('quote', q)"
    @activate="(id) => $emit('activate', id)"
    @located="(ids) => $emit('located', ids)"
  />
</template>
