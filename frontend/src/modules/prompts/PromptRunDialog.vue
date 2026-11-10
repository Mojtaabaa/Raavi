<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import Dialog from 'primevue/dialog'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Textarea from 'primevue/textarea'
import Select from 'primevue/select'
import MultiSelect from 'primevue/multiselect'
import Message from 'primevue/message'
import { MODE_AUTOMATIC, MODE_COPY, promptRunApi, textToDoc, type Execution, type PromptContextRequest, type PromptSummary, type RenderedPrompt } from '@/core/api/prompts'
import { wikiApi, type SpaceSummary } from '@/core/api/wiki'
import { ApiError } from '@/core/api/http'
import { errorMessage } from '@/core/api/errors'
import { useLocaleStore } from '@/stores/locale'

/**
 * Use a prompt: pick/enter the context, fill the author's inputs, see the exact text that would be used (preview — records nothing), then copy it
 * for another tool or run it through the workspace's AI after an explicit consent. Results are never written into a page without a confirmation.
 */
const props = defineProps<{ visible: boolean; prompt: PromptSummary | null; context?: PromptContextRequest | null; pageId?: string | null }>()
const emit = defineEmits<{ 'update:visible': [boolean]; insert: [text: string] }>()
const { t, te } = useI18n()
const toast = useToast()
const router = useRouter()
const locale = useLocaleStore()

const kind = ref('generic')
const ctx = ref({ selectedText: '', section: '', spaceKey: '', reference: '', text: '' })
const inputs = ref<Record<string, unknown>>({})
const rendered = ref<RenderedPrompt | null>(null)
const finalText = ref('')
const error = ref('')
const errorCode = ref('')
const truncate = ref(false)
const busy = ref(false)
const consent = ref(false)
const running = ref(false)
const result = ref<Execution | null>(null)
const copied = ref(false)
const spaces = ref<SpaceSummary[]>([])
const saveSpace = ref<string | null>(null)

const kinds = computed(() => props.prompt?.contexts ?? ['generic'])
const canRun = computed(() => !!props.prompt && (props.prompt.modes & MODE_AUTOMATIC) !== 0)
const canCopy = computed(() => !!props.prompt && (props.prompt.modes & MODE_COPY) !== 0)

function requestBody() {
  const c: PromptContextRequest = { kind: kind.value }
  const base = props.context
  if (base?.pageId) c.pageId = base.pageId
  if (props.pageId) c.pageId ??= props.pageId
  if (base?.pageIds) c.pageIds = base.pageIds
  c.selectedText = ctx.value.selectedText || base?.selectedText || null
  c.section = ctx.value.section || base?.section || null
  c.spaceKey = ctx.value.spaceKey || base?.spaceKey || null
  c.reference = ctx.value.reference || null
  c.text = ctx.value.text || null
  return { context: c, inputs: inputs.value, truncate: truncate.value, locale: locale.locale }
}

let timer: number | undefined
async function render() {
  if (!props.prompt) return
  busy.value = true
  error.value = ''
  errorCode.value = ''
  try {
    const r = await promptRunApi.render(props.prompt.id, requestBody())
    rendered.value = r
    finalText.value = r.text
    for (const d of r.inputs) if (!(d.name in inputs.value) && d.default) inputs.value[d.name] = d.type === 'boolean' ? d.default === 'true' : d.default
  } catch (e) {
    rendered.value = null
    finalText.value = ''
    errorCode.value = e instanceof ApiError ? e.code : ''
    error.value = errorMessage(e, t, te)
  } finally {
    busy.value = false
  }
}
const scheduleRender = () => {
  window.clearTimeout(timer)
  timer = window.setTimeout(render, 400)
}

watch(
  () => props.visible,
  async (open) => {
    if (!open || !props.prompt) return
    result.value = null
    consent.value = false
    copied.value = false
    truncate.value = false
    inputs.value = {}
    ctx.value = { selectedText: props.context?.selectedText ?? '', section: props.context?.section ?? '', spaceKey: props.context?.spaceKey ?? '', reference: '', text: '' }
    kind.value = props.context?.kind && kinds.value.includes(props.context.kind) ? props.context.kind : kinds.value.includes('page') && props.pageId ? 'page' : kinds.value[0]!
    spaces.value = await wikiApi.spaces().catch(() => [])
    saveSpace.value = spaces.value[0]?.key ?? null
    await render()
  },
)
watch([kind, ctx, inputs, truncate], scheduleRender, { deep: true })

async function copy() {
  if (!props.prompt || !rendered.value) return
  const text = finalText.value
  try {
    await navigator.clipboard.writeText(text)
  } catch {
    // clipboard unavailable (insecure page, permissions): select the text so Ctrl+C works, and say so
    const el = document.querySelector<HTMLTextAreaElement>('[data-testid="run-final"]')
    el?.focus()
    el?.select()
    toast.add({ severity: 'warn', summary: t('prompts.run.clipboardUnavailable'), life: 6000 })
    return
  }
  copied.value = true
  await promptRunApi.copied(props.prompt.id, rendered.value.versionId, kind.value).catch(() => undefined)
  toast.add({ severity: 'success', summary: t('prompts.run.copied'), life: 2500 })
}

async function confirmUse() {
  if (!props.prompt || !rendered.value) return
  await promptRunApi.manualUse(props.prompt.id, rendered.value.versionId, kind.value).catch(() => undefined)
  toast.add({ severity: 'success', summary: t('prompts.run.usageNoted'), life: 3000 })
}

async function run() {
  if (!props.prompt || !rendered.value) return
  running.value = true
  error.value = ''
  try {
    result.value = await promptRunApi.execute(props.prompt.id, { ...requestBody(), versionId: rendered.value.versionId }, consent.value)
  } catch (e) {
    error.value = errorMessage(e, t, te)
  } finally {
    running.value = false
  }
}

async function copyResult() {
  if (!result.value?.result) return
  try {
    await navigator.clipboard.writeText(result.value.result)
    toast.add({ severity: 'success', summary: t('prompts.run.copied'), life: 2500 })
  } catch {
    toast.add({ severity: 'warn', summary: t('prompts.run.clipboardUnavailable'), life: 5000 })
  }
}

async function saveAsPage() {
  if (!result.value?.result || !saveSpace.value || !props.prompt) return
  try {
    const page = await wikiApi.createContent(saveSpace.value, { title: `${props.prompt.title} — ${new Date().toLocaleDateString()}`, parentId: null, contentType: 'page', body: textToDoc(result.value.result), publish: false })
    toast.add({ severity: 'success', summary: t('prompts.run.savedAsDraft'), life: 4000 })
    emit('update:visible', false)
    await router.push({ name: 'page-edit', params: { key: saveSpace.value, id: page.id } })
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
}

const needs = (k: string) => kind.value === k
const hasInputs = computed(() => (rendered.value?.inputs.length ?? 0) > 0)
const askTruncate = computed(() => errorCode.value === 'prompt.context_too_large')
</script>

<template>
  <Dialog :visible="visible" modal maximizable :header="prompt ? t('prompts.run.title', { title: prompt.title }) : ''" :style="{ inlineSize: 'min(54rem, 96vw)' }" @update:visible="emit('update:visible', $event)">
    <div v-if="prompt" class="run" data-testid="run-dialog">
      <section class="ctx">
        <label v-if="kinds.length > 1" for="run-kind">{{ t('prompts.run.context') }}</label>
        <Select v-if="kinds.length > 1" v-model="kind" input-id="run-kind" :options="kinds.map((k) => ({ value: k, label: t(`prompts.contexts.${k}`) }))" option-label="label" option-value="value" data-testid="run-kind" />
        <template v-if="needs('selection')">
          <label for="run-sel">{{ t('prompts.contexts.selection') }}</label>
          <Textarea id="run-sel" v-model="ctx.selectedText" rows="3" auto-resize data-testid="run-selection" />
        </template>
        <template v-if="needs('section')">
          <label for="run-section">{{ t('prompts.run.sectionHeading') }}</label>
          <InputText id="run-section" v-model="ctx.section" />
        </template>
        <template v-if="needs('space')">
          <label for="run-space">{{ t('prompts.run.spaceKey') }}</label>
          <InputText id="run-space" v-model="ctx.spaceKey" dir="ltr" />
        </template>
        <template v-if="needs('jira-issue')">
          <label for="run-ref">{{ t('prompts.run.issueKey') }}</label>
          <InputText id="run-ref" v-model="ctx.reference" dir="ltr" placeholder="ABC-123" />
        </template>
        <template v-if="needs('generic')">
          <label for="run-text">{{ t('prompts.run.text') }}</label>
          <Textarea id="run-text" v-model="ctx.text" rows="4" auto-resize data-testid="run-text" />
        </template>
        <p v-if="needs('page') && !pageId && !context?.pageId" class="muted">{{ t('prompts.run.openFromPage') }}</p>
      </section>

      <section v-if="hasInputs" class="inputs" data-testid="run-inputs">
        <div v-for="d in rendered!.inputs" :key="d.name" class="field">
          <label :for="`in-${d.name}`">{{ d.label || d.name }}<span v-if="d.required" class="req"> *</span></label>
          <Select v-if="d.type === 'select'" v-model="inputs[d.name]" :input-id="`in-${d.name}`" :options="d.options ?? []" show-clear />
          <MultiSelect v-else-if="d.type === 'multiselect'" v-model="inputs[d.name] as string[]" :input-id="`in-${d.name}`" :options="d.options ?? []" display="chip" />
          <input v-else-if="d.type === 'boolean'" :id="`in-${d.name}`" v-model="inputs[d.name]" type="checkbox" />
          <input v-else-if="d.type === 'date'" :id="`in-${d.name}`" v-model="inputs[d.name]" type="date" class="native" />
          <InputText v-else :id="`in-${d.name}`" v-model="inputs[d.name] as string" :type="d.type === 'number' ? 'number' : 'text'" :data-testid="`input-${d.name}`" />
          <small v-if="d.description" class="muted">{{ d.description }}</small>
        </div>
      </section>

      <Message v-if="error" severity="error" :closable="false" data-testid="run-error">
        {{ error }}
        <Button v-if="askTruncate" link size="small" :label="t('prompts.run.shorten')" data-testid="run-shorten" @click="truncate = true" />
      </Message>
      <Message v-if="rendered && !rendered.ready" severity="warn" :closable="false" data-testid="run-missing">{{ t('prompts.run.missing', { names: rendered.missing.join(', ') }) }}</Message>

      <section v-if="rendered" class="preview">
        <div class="bar">
          <strong>{{ t('prompts.run.finalText') }}</strong>
          <span class="muted" data-testid="run-tokens">≈ {{ rendered.tokenEstimate }} {{ t('prompts.run.tokens') }} · {{ rendered.chars }} {{ t('prompts.run.chars') }} · {{ t('prompts.version', { n: rendered.versionNumber }) }}</span>
        </div>
        <Textarea v-model="finalText" rows="10" auto-resize :readonly="!canCopy" class="final" dir="auto" data-testid="run-final" :aria-label="t('prompts.run.finalText')" />
        <p v-if="rendered.warnings.length" class="muted">{{ t('prompts.run.shortened') }}</p>
        <details v-if="rendered.sources.length">
          <summary>{{ t('prompts.run.sources', { n: rendered.sources.length }) }}</summary>
          <ul class="sources"><li v-for="(s, i) in rendered.sources" :key="i"><RouterLink v-if="s.url && s.url.startsWith('/')" :to="s.url">{{ s.title }}</RouterLink><span v-else>{{ s.title }}</span> <span class="muted">({{ s.kind }}, {{ s.chars }})</span></li></ul>
        </details>
        <details>
          <summary>{{ t('prompts.run.variables') }}</summary>
          <ul class="sources"><li v-for="v in rendered.variables" :key="v.name"><code dir="ltr">{{ v.name }}</code> <span class="muted">{{ t(`prompts.run.origin.${v.origin}`) }} · {{ v.chars }}</span></li></ul>
        </details>
      </section>
      <p v-if="busy" class="muted" role="status">{{ t('common.loading') }}</p>

      <section class="actions">
        <Button v-if="canCopy" icon="pi pi-copy" :label="t('prompts.run.copy')" :disabled="!rendered?.ready" data-testid="run-copy" @click="copy" />
        <Button v-if="canCopy && copied" text size="small" icon="pi pi-check" :label="t('prompts.run.iUsedIt')" data-testid="run-confirm-use" @click="confirmUse" />
        <div v-if="canRun" class="auto">
          <label class="consent"><input v-model="consent" type="checkbox" data-testid="run-consent" /> {{ t('prompts.run.consent') }}</label>
          <Button severity="success" icon="pi pi-play" :label="t('prompts.run.run')" :disabled="!rendered?.ready || !consent" :loading="running" data-testid="run-execute" @click="run" />
        </div>
      </section>

      <section v-if="result" class="result" data-testid="run-result">
        <div class="bar">
          <strong>{{ t('prompts.run.result') }}</strong>
          <span class="muted">{{ result.model }} · {{ result.promptTokens }}+{{ result.completionTokens }} {{ t('prompts.run.tokens') }} · {{ result.durationMs }} ms</span>
        </div>
        <pre class="answer" dir="auto" data-testid="run-answer">{{ result.result }}</pre>
        <div class="actions">
          <Button size="small" outlined icon="pi pi-copy" :label="t('prompts.run.copyResult')" @click="copyResult" />
          <Button size="small" outlined icon="pi pi-refresh" :label="t('prompts.run.again')" :loading="running" @click="run" />
          <span v-if="spaces.length" class="save">
            <Select v-model="saveSpace" :options="spaces.map((s) => ({ value: s.key, label: s.name }))" option-label="label" option-value="value" size="small" :aria-label="t('prompts.run.saveIn')" />
            <Button size="small" outlined icon="pi pi-file-plus" :label="t('prompts.run.saveAsPage')" data-testid="run-save-page" @click="saveAsPage" />
          </span>
          <Button v-if="pageId" size="small" outlined icon="pi pi-arrow-down" :label="t('prompts.run.insert')" data-testid="run-insert" @click="emit('insert', result.result ?? '')" />
        </div>
      </section>
    </div>
  </Dialog>
</template>

<style scoped>
.run { display: grid; gap: var(--raavi-space-3); }
.ctx, .inputs { display: grid; gap: var(--raavi-space-2); }
.field { display: grid; gap: 0.25rem; }
label { font-weight: 600; font-size: 0.9rem; }
.req { color: var(--p-red-500, #dc2626); }
.bar { display: flex; justify-content: space-between; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: baseline; }
.muted { color: var(--raavi-text-muted); margin: 0; font-size: 0.85rem; }
.final { inline-size: 100%; font-family: inherit; }
.native { padding: 0.4rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: transparent; color: inherit; }
.actions { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.auto { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; margin-inline-start: auto; }
.consent { font-weight: 400; display: inline-flex; gap: 0.35rem; align-items: center; font-size: 0.85rem; max-inline-size: 28rem; }
.sources { margin: 0.3rem 0 0; padding-inline-start: 1.2rem; }
.answer { white-space: pre-wrap; word-break: break-word; margin: 0; padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: color-mix(in srgb, var(--p-primary-color) 5%, transparent); font-family: inherit; max-block-size: 22rem; overflow: auto; }
.save { display: inline-flex; gap: 0.3rem; align-items: center; }
</style>
