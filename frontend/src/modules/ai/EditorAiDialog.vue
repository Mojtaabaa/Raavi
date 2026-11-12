<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Dialog from 'primevue/dialog'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import { aiApi, type AiAnswer } from '@/core/api/ai'
import { errorMessage } from '@/core/api/errors'

const props = defineProps<{
  pageId: string
  selection: string
  from: number
  to: number
  action: 'summarize' | 'rewrite' | null
}>()

const emit = defineEmits<{ close: []; apply: [text: string, from: number, to: number] }>()
const { t, te } = useI18n()

const open = computed({
  get: () => props.action !== null,
  set: (v: boolean) => { if (!v) emit('close') },
})

const consent = ref(false)
const instruction = ref('')
const busy = ref(false)
const error = ref('')
const answer = ref<AiAnswer | null>(null)

watch(() => props.action, () => {
  consent.value = false
  instruction.value = ''
  error.value = ''
  answer.value = null
})

async function run() {
  if (!props.action || !consent.value) return
  busy.value = true
  error.value = ''
  answer.value = null
  try {
    answer.value = props.action === 'summarize'
      ? await aiApi.editorSummarize(props.pageId, props.selection, true)
      : await aiApi.editorRewrite(props.pageId, props.selection, instruction.value || undefined, true)
  } catch (e) {
    error.value = errorMessage(e, t, te)
  } finally {
    busy.value = false
  }
}

function apply() {
  if (!answer.value?.text) return
  emit('apply', answer.value.text, props.from, props.to)
}
</script>

<template>
  <Dialog
    v-model:visible="open"
    modal
    :header="action === 'rewrite' ? t('editor.ai.rewriteTitle') : t('editor.ai.summarizeTitle')"
    :style="{ inlineSize: 'min(36rem, 95vw)' }"
    data-testid="editor-ai-dialog"
  >
    <div class="wrap">
      <p class="muted" dir="auto">{{ t('editor.ai.selectionPreview', { n: selection.length }) }}</p>
      <pre class="sel" dir="auto">{{ selection.slice(0, 400) }}{{ selection.length > 400 ? '…' : '' }}</pre>
      <label v-if="action === 'rewrite'" for="ai-instruction">{{ t('editor.ai.instruction') }}</label>
      <InputText
        v-if="action === 'rewrite'"
        id="ai-instruction"
        v-model="instruction"
        maxlength="500"
        :placeholder="t('editor.ai.instructionHint')"
        data-testid="editor-ai-instruction"
      />
      <div class="row">
        <Checkbox v-model="consent" input-id="ai-consent" binary data-testid="editor-ai-consent" />
        <label for="ai-consent">{{ t('editor.ai.consent') }}</label>
      </div>
      <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>
      <div v-if="answer" class="preview" data-testid="editor-ai-preview">
        <strong>{{ t('editor.ai.suggestion') }}</strong>
        <p dir="auto">{{ answer.text }}</p>
      </div>
      <div class="actions">
        <Button :label="t('editor.ai.generate')" :disabled="!consent || busy" :loading="busy" data-testid="editor-ai-run" @click="run" />
        <Button v-if="answer" :label="t('editor.ai.apply')" severity="success" data-testid="editor-ai-apply" @click="apply" />
        <Button text :label="t('common.cancel')" @click="emit('close')" />
      </div>
    </div>
  </Dialog>
</template>

<style scoped>
.wrap { display: grid; gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); margin: 0; font-size: 0.9rem; }
.sel { margin: 0; max-block-size: 8rem; overflow: auto; white-space: pre-wrap; background: var(--raavi-surface-muted, rgba(127,127,127,0.08)); padding: var(--raavi-space-2); border-radius: var(--raavi-radius-sm); font: inherit; }
.row { display: flex; align-items: flex-start; gap: var(--raavi-space-2); }
.preview { border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: var(--raavi-space-2); }
.preview p { margin: var(--raavi-space-1) 0 0; white-space: pre-wrap; }
.actions { display: flex; flex-wrap: wrap; gap: var(--raavi-space-2); }
</style>
