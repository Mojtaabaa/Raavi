<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import type { RaaviDoc } from '@/core/api/wiki'
import RaaviEditor from '@/modules/editor/components/RaaviEditor.vue'
import { SCHEMA_VERSION } from '@/modules/editor/registry'

/** Comment editor: compact Raavi editor (bold/italic/code/links via shortcuts, @mentions). Ctrl/⌘+Enter submits. */
const props = withDefaults(defineProps<{ initial?: RaaviDoc | null; submitLabel?: string; busy?: boolean; cancellable?: boolean; label?: string }>(), {
  initial: null,
  submitLabel: undefined,
  busy: false,
  cancellable: false,
  label: undefined,
})
const emit = defineEmits<{ submit: [doc: RaaviDoc]; cancel: [] }>()
const { t } = useI18n()

const empty = (): RaaviDoc => ({ schema: 'raavi.doc', schemaVersion: SCHEMA_VERSION, doc: { type: 'doc', content: [{ type: 'paragraph' }] } })
const doc = ref<RaaviDoc>(props.initial ?? empty())

/** Something worth sending: text or at least one mention. */
const hasContent = computed(() => {
  let found = false
  const walk = (n: { type?: string; text?: string; content?: unknown[] }) => {
    if (found) return
    if ((n.type === 'text' && (n.text ?? '').trim()) || n.type === 'mention') found = true
    ;(n.content as typeof n[] | undefined)?.forEach(walk)
  }
  walk(doc.value.doc as never)
  return found
})

function submit() {
  if (!hasContent.value || props.busy) return
  emit('submit', doc.value)
}

/** Called by the parent after a successful submit. */
function clear() {
  doc.value = empty()
}
defineExpose({ clear })
</script>

<template>
  <form class="composer" @submit.prevent="submit" @keydown.enter.ctrl.prevent="submit" @keydown.enter.meta.prevent="submit">
    <RaaviEditor v-model="doc" editable compact :label="label ?? t('comments.composer.label')" :placeholder="t('comments.composer.placeholder')" />
    <div class="row">
      <Button type="submit" size="small" :label="submitLabel ?? t('comments.composer.send')" :loading="busy" :disabled="!hasContent" data-testid="comment-submit" />
      <Button v-if="cancellable" type="button" size="small" text :label="t('common.cancel')" @click="emit('cancel')" />
      <small class="hint">{{ t('comments.composer.hint') }}</small>
    </div>
  </form>
</template>

<style scoped>
.composer { display: grid; gap: var(--raavi-space-2); }
.row { display: flex; align-items: center; gap: var(--raavi-space-2); flex-wrap: wrap; }
.hint { color: var(--raavi-text-muted); }
</style>
