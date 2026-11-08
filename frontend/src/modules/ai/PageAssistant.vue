<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import { aiApi, type AiAnswer } from '@/core/api/ai'
import { errorMessage } from '@/core/api/errors'

/**
 * AI help on one page (Phase 11a): summarise it or ask a question about it. It only appears when the workspace has AI switched on. The answer is
 * plain text (shown as text, never as HTML) and is generated from this page alone, with the reader's own access.
 */
const props = defineProps<{ pageId: string }>()
const { t, te } = useI18n()

const available = ref(false)
const open = ref(false)
const busy = ref(false)
const question = ref('')
const answer = ref<AiAnswer | null>(null)
const error = ref('')

onMounted(async () => {
  try {
    available.value = (await aiApi.status()).available
  } catch {
    available.value = false
  }
})
watch(() => props.pageId, () => {
  answer.value = null
  error.value = ''
})

async function run(action: () => Promise<AiAnswer>) {
  busy.value = true
  error.value = ''
  answer.value = null
  try {
    answer.value = await action()
  } catch (e) {
    error.value = errorMessage(e, t, te)
  } finally {
    busy.value = false
  }
}

const summarize = () => run(() => aiApi.summarize(props.pageId))
const ask = () => question.value.trim() && run(() => aiApi.ask(props.pageId, question.value.trim()))
</script>

<template>
  <section v-if="available" class="assistant" :aria-label="t('ai.assistant')" data-testid="assistant">
    <Button size="small" severity="secondary" outlined icon="pi pi-sparkles" :label="t('ai.assistant')" :aria-expanded="open" data-testid="assistant-toggle" @click="open = !open" />
    <div v-if="open" class="panel">
      <div class="row">
        <Button size="small" :label="t('ai.summarize')" :loading="busy" data-testid="summarize" @click="summarize" />
      </div>
      <form class="row" @submit.prevent="ask">
        <label for="ai-question" class="sr-only">{{ t('ai.question') }}</label>
        <input id="ai-question" v-model="question" type="text" maxlength="1000" dir="auto" :placeholder="t('ai.question')" data-testid="question" />
        <Button type="submit" size="small" severity="secondary" :label="t('ai.ask')" :loading="busy" :disabled="!question.trim()" data-testid="ask" />
      </form>
      <p v-if="error" class="error" role="alert" data-testid="ai-error">{{ error }}</p>
      <div v-if="answer" class="answer" data-testid="answer">
        <p class="text" dir="auto">{{ answer.text }}</p>
        <p class="muted small">
          {{ t('ai.generatedBy', { model: answer.model }) }}<template v-if="answer.truncated"> · {{ t('ai.truncated') }}</template> · {{ t('ai.verify') }}
        </p>
      </div>
    </div>
  </section>
</template>

<style scoped>
.assistant { display: grid; gap: var(--raavi-space-2); justify-items: start; }
.panel { display: grid; gap: var(--raavi-space-2); inline-size: 100%; padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.row { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.row input { flex: 1 1 14rem; font: inherit; padding: 0.4rem; }
.answer { border-inline-start: 3px solid var(--raavi-primary, #2563eb); padding-inline-start: var(--raavi-space-2); }
.text { margin: 0; white-space: pre-wrap; overflow-wrap: anywhere; }
.error { color: var(--raavi-danger, #b91c1c); margin: 0; }
.muted { color: var(--raavi-text-muted); margin: 0; }
.small { font-size: 0.85rem; }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
</style>
