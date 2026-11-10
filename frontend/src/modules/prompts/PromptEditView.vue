<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Textarea from 'primevue/textarea'
import Select from 'primevue/select'
import MultiSelect from 'primevue/multiselect'
import Tag from 'primevue/tag'
import Dialog from 'primevue/dialog'
import RaaviEditor from '@/modules/editor/components/RaaviEditor.vue'
import PromptRunDialog from './PromptRunDialog.vue'
import PromptPanels from './PromptPanels.vue'
import { MODE_AUTOMATIC, MODE_COPY, MODE_PREVIEW, PROMPT_CONTEXTS, promptRunApi, promptsApi, type InputDefinition, type PromptCategory, type PromptDetail, type VariableInfo } from '@/core/api/prompts'
import type { RaaviDoc } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'

/**
 * Prompt page: create/edit (owner) or read (everybody else). The text is written in the normal Raavi editor; variables are written {{like.this}}.
 * Changes to a published prompt go into a draft (autosaved); "Publish" turns the draft into a new immutable version.
 */
const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const toast = useToast()
const locale = useLocaleStore()

const emptyDoc = (): RaaviDoc => ({ schema: 'raavi.doc', schemaVersion: 2, doc: { type: 'doc', content: [{ type: 'paragraph' }] } })
const isNew = computed(() => route.name === 'prompt-new')
const detail = ref<PromptDetail | null>(null)
const categories = ref<PromptCategory[]>([])
const form = ref({ title: '', description: '', categoryId: null as string | null, tags: '', language: 'any', contexts: ['generic'] as string[], modes: 7 })
const body = ref<RaaviDoc>(emptyDoc())
const loading = ref(true)
const saving = ref(false)
const dirty = ref(false)
const savedAt = ref<Date | null>(null)
const publishOpen = ref(false)
const runOpen = ref(false)
const editorRef = ref<InstanceType<typeof RaaviEditor> | null>(null)
const catalog = ref<VariableInfo[]>([])
const defs = ref<InputDefinition[]>([])
const newDef = ref({ name: '', label: '', type: 'text' as InputDefinition['type'], required: false, default: '', options: '' })
const typeOptions = ['text', 'number', 'boolean', 'date', 'select', 'multiselect'].map((v) => ({ value: v, label: v }))
/** Names the server accepts: the built-in catalog plus this prompt's own input variables (anything else is drawn as unknown in the editor). */
const knownNames = () => new Set([...catalog.value.map((v) => v.name), ...defs.value.map((d) => d.name)])
function insertVariable(name: string) {
  editorRef.value?.insertText(`{{${name}}}`)
}
function addDef() {
  const short = newDef.value.name.trim().replace(/^input\./, '')
  if (!/^[a-z][a-z0-9_]{0,40}$/.test(short)) {
    toast.add({ severity: 'warn', summary: t('prompts.inputs.nameHelp'), life: 5000 })
    return
  }
  const d: InputDefinition = {
    name: `input.${short}`,
    label: newDef.value.label.trim() || null,
    type: newDef.value.type,
    required: newDef.value.required,
    default: newDef.value.default.trim() || null,
    options: newDef.value.type === 'select' || newDef.value.type === 'multiselect' ? newDef.value.options.split(/[,،]/).map((s) => s.trim()).filter(Boolean) : null,
    description: null,
  }
  if (defs.value.some((x) => x.name === d.name)) return
  defs.value = [...defs.value, d]
  newDef.value = { name: '', label: '', type: 'text', required: false, default: '', options: '' }
  editorRef.value?.refreshVariables()
}
function removeDef(name: string) {
  defs.value = defs.value.filter((d) => d.name !== name)
  editorRef.value?.refreshVariables()
}
async function fork() {
  try {
    const copy = await promptRunApi.fork(detail.value!.prompt.id)
    toast.add({ severity: 'success', summary: t('prompts.forked'), life: 3000 })
    await router.push({ name: 'prompt', params: { id: copy.prompt.id } })
  } catch (e) {
    fail(e)
  }
}
const note = ref('')

const canEdit = computed(() => isNew.value || !!detail.value?.prompt.canEdit)
const prompt = computed(() => detail.value?.prompt ?? null)
const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const categoryOptions = computed(() => {
  const out: { value: string | null; label: string }[] = [{ value: null, label: t('prompts.uncategorized') }]
  const walk = (parent: string | null, depth: number) => {
    for (const c of categories.value.filter((x) => x.parentId === parent && x.status === 'Active').sort((a, b) => a.sortOrder - b.sortOrder)) {
      out.push({ value: c.id, label: `${'— '.repeat(depth)}${c.name}` })
      walk(c.id, depth + 1)
    }
  }
  walk(null, 0)
  return out
})
const contextOptions = computed(() => PROMPT_CONTEXTS.map((c) => ({ value: c, label: t(`prompts.contexts.${c}`) })))
const languageOptions = computed(() => ['any', 'fa', 'en'].map((l) => ({ value: l, label: t(`prompts.languages.${l}`) })))
const modeFlags = [
  { bit: MODE_AUTOMATIC, key: 'automatic' },
  { bit: MODE_COPY, key: 'copy' },
  { bit: MODE_PREVIEW, key: 'preview' },
]
const hasMode = (bit: number) => (form.value.modes & bit) !== 0
function toggleMode(bit: number) {
  form.value.modes = hasMode(bit) ? form.value.modes & ~bit : form.value.modes | bit
  dirty.value = true
}
const asVariable = (v: string) => "{{" + v + "}}"
/** Variables found in the text, so typos are visible before anyone runs the prompt. */
const variables = computed(() => [...new Set([...(detail.value?.contentText ?? '').matchAll(/\{\{\s*([a-zA-Z][\w.-]*)\s*\}\}/g)].map((m) => m[1]!))])

function fill(d: PromptDetail) {
  detail.value = d
  const p = d.prompt
  form.value = { title: p.title, description: p.description ?? '', categoryId: p.categoryId, tags: p.tags.join(', '), language: p.language, contexts: [...p.contexts], modes: p.modes }
  body.value = d.content
  defs.value = d.variables ?? []
  dirty.value = false
}

async function load() {
  loading.value = true
  try {
    categories.value = await promptsApi.categories()
    catalog.value = (await promptRunApi.variables().catch(() => ({ variables: [] as VariableInfo[] }))).variables
    if (!isNew.value) fill(await promptsApi.get(String(route.params.id)))
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}

const payload = () => ({
  title: form.value.title.trim(),
  description: form.value.description.trim() || null,
  categoryId: form.value.categoryId,
  tags: form.value.tags.split(/[,،]/).map((s) => s.trim()).filter(Boolean),
  language: form.value.language,
  contexts: form.value.contexts,
  modes: form.value.modes,
  content: body.value,
  variables: defs.value,
  revision: detail.value?.prompt.revision,
})

async function save(quiet = false) {
  if (saving.value || !form.value.title.trim()) return
  saving.value = true
  try {
    if (isNew.value) {
      const created = await promptsApi.create({ ...payload(), revision: undefined })
      fill(created)
      await router.replace({ name: 'prompt', params: { id: created.prompt.id } })
    } else {
      fill(await promptsApi.update(detail.value!.prompt.id, payload()))
    }
    savedAt.value = new Date()
    if (!quiet) toast.add({ severity: 'success', summary: t('prompts.saved'), life: 2500 })
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}

async function publish() {
  await save(true)
  if (!detail.value) return
  try {
    fill(await promptsApi.publish(detail.value.prompt.id, note.value.trim() || undefined))
    publishOpen.value = false
    note.value = ''
    toast.add({ severity: 'success', summary: t('prompts.published'), life: 3000 })
  } catch (e) {
    fail(e)
  }
}

async function share(visibility: 'Tenant' | 'Personal') {
  try {
    fill(await promptsApi.setVisibility(detail.value!.prompt.id, visibility))
    toast.add({ severity: 'success', summary: visibility === 'Tenant' ? t('prompts.sharedNow') : t('prompts.unsharedNow'), life: 3000 })
  } catch (e) {
    fail(e)
  }
}

// Autosave of the draft a few seconds after the last change (only for prompts that already exist).
let timer: number | undefined
watch([form, body, defs], () => {
  if (loading.value) return
  dirty.value = true
  window.clearTimeout(timer)
  if (!isNew.value && canEdit.value) timer = window.setTimeout(() => save(true), 4000)
}, { deep: true })
onMounted(load)
onBeforeUnmount(() => window.clearTimeout(timer))
watch(() => route.params.id, (id) => { if (id) load() })
</script>

<template>
  <section class="edit" data-testid="prompt-edit">
    <header class="head">
      <Button text icon="pi pi-arrow-left" :label="t('prompts.back')" @click="router.push({ name: 'prompts' })" />
      <div v-if="prompt" class="badges">
        <Tag :severity="prompt.visibility === 'Tenant' ? 'info' : 'secondary'" :value="t(`prompts.visibility.${prompt.visibility}`)" data-testid="prompt-visibility" />
        <Tag :value="t(`prompts.status.${prompt.status}`)" data-testid="prompt-status" />
        <span v-if="detail?.shown" class="muted">{{ t('prompts.version', { n: detail.shown.number }) }}{{ detail.shown.isPublished ? '' : ' · ' + t('prompts.draft') }}</span>
      </div>
    </header>
    <p v-if="prompt?.forkedFromTitle" class="muted">{{ t('prompts.forkedFrom', { title: prompt.forkedFromTitle }) }}</p>
    <p v-if="loading" class="muted" role="status">{{ t('common.loading') }}</p>

    <template v-else>
      <form v-if="canEdit" class="form" @submit.prevent="save(false)">
        <label for="p-title">{{ t('prompts.fields.title') }}</label>
        <InputText id="p-title" v-model="form.title" maxlength="200" required data-testid="prompt-title" />
        <label for="p-desc">{{ t('prompts.fields.description') }}</label>
        <Textarea id="p-desc" v-model="form.description" rows="2" maxlength="1000" auto-resize />
        <div class="grid">
          <div>
            <label for="p-cat">{{ t('prompts.fields.category') }}</label>
            <Select v-model="form.categoryId" input-id="p-cat" :options="categoryOptions" option-label="label" option-value="value" class="full" />
          </div>
          <div>
            <label for="p-lang">{{ t('prompts.fields.language') }}</label>
            <Select v-model="form.language" input-id="p-lang" :options="languageOptions" option-label="label" option-value="value" class="full" />
          </div>
          <div>
            <label for="p-ctx">{{ t('prompts.fields.contexts') }}</label>
            <MultiSelect v-model="form.contexts" input-id="p-ctx" :options="contextOptions" option-label="label" option-value="value" display="chip" class="full" />
          </div>
          <div>
            <label for="p-tags">{{ t('prompts.fields.tags') }}</label>
            <InputText id="p-tags" v-model="form.tags" class="full" :placeholder="t('prompts.fields.tagsHelp')" />
          </div>
        </div>
        <fieldset class="modes">
          <legend>{{ t('prompts.fields.modes') }}</legend>
          <label v-for="m in modeFlags" :key="m.key" class="check"><input type="checkbox" :checked="hasMode(m.bit)" :data-testid="`mode-${m.key}`" @change="toggleMode(m.bit)" /> {{ t(`prompts.modes.${m.key}`) }}</label>
        </fieldset>
        <span class="label">{{ t('prompts.fields.content') }}</span>
        <p class="muted">{{ t('prompts.variablesHelp') }}</p>
        <RaaviEditor ref="editorRef" v-model="body" editable :variable-names="knownNames" data-testid="prompt-editor" />
        <details class="varmenu" data-testid="variable-menu">
          <summary>{{ t('prompts.variables.menu') }}</summary>
          <table class="vartable">
            <thead><tr><th>{{ t('prompts.variables.name') }}</th><th>{{ t('prompts.variables.description') }}</th><th>{{ t('prompts.variables.type') }}</th><th>{{ t('prompts.variables.contexts') }}</th><th /></tr></thead>
            <tbody>
              <tr v-for="v in catalog" :key="v.name">
                <td><code dir="ltr">{{ v.name }}</code></td><td>{{ v.description }}</td><td>{{ v.type }}</td><td>{{ v.contexts.join(', ') }}</td>
                <td><Button type="button" text size="small" icon="pi pi-plus" :aria-label="t('prompts.variables.insert')" :data-testid="`insert-${v.name}`" @click="insertVariable(v.name)" /></td>
              </tr>
              <tr v-for="d in defs" :key="d.name">
                <td><code dir="ltr">{{ d.name }}</code></td><td>{{ d.label || '' }}</td><td>{{ d.type }}{{ d.required ? ' *' : '' }}</td><td>{{ t('prompts.inputs.fromUser') }}</td>
                <td>
                  <Button type="button" text size="small" icon="pi pi-plus" :aria-label="t('prompts.variables.insert')" @click="insertVariable(d.name)" />
                  <Button type="button" text size="small" severity="danger" icon="pi pi-trash" :aria-label="t('common.delete')" @click="removeDef(d.name)" />
                </td>
              </tr>
            </tbody>
          </table>
          <fieldset class="newdef">
            <legend>{{ t('prompts.inputs.add') }}</legend>
            <InputText v-model="newDef.name" dir="ltr" :placeholder="t('prompts.inputs.name')" data-testid="def-name" />
            <InputText v-model="newDef.label" :placeholder="t('prompts.inputs.label')" />
            <Select v-model="newDef.type" :options="typeOptions" option-label="label" option-value="value" :aria-label="t('prompts.variables.type')" />
            <InputText v-if="newDef.type === 'select' || newDef.type === 'multiselect'" v-model="newDef.options" :placeholder="t('prompts.inputs.options')" />
            <InputText v-model="newDef.default" :placeholder="t('prompts.inputs.default')" />
            <label class="check"><input v-model="newDef.required" type="checkbox" /> {{ t('prompts.inputs.required') }}</label>
            <Button type="button" size="small" icon="pi pi-plus" :label="t('prompts.inputs.addButton')" data-testid="def-add" @click="addDef" />
          </fieldset>
        </details>
        <p v-if="variables.length" class="vars" data-testid="prompt-variables">
          {{ t('prompts.variablesFound') }} <code v-for="v in variables" :key="v" dir="ltr">{{ asVariable(v) }}</code>
        </p>
        <div class="bar">
          <Button type="submit" icon="pi pi-save" :label="t('prompts.saveDraft')" :loading="saving" :disabled="!form.title.trim()" data-testid="prompt-save" />
          <Button v-if="!isNew" icon="pi pi-check" severity="success" :label="t('prompts.publish')" :disabled="!prompt?.hasDraft && !dirty" data-testid="prompt-publish" @click="publishOpen = true" />
          <Button v-if="!isNew && prompt?.visibility === 'Personal'" outlined icon="pi pi-users" :label="t('prompts.share')" :disabled="!detail?.published" data-testid="prompt-share" @click="share('Tenant')" />
          <Button v-if="!isNew && prompt?.visibility === 'Tenant'" outlined icon="pi pi-lock" :label="t('prompts.unshare')" data-testid="prompt-unshare" @click="share('Personal')" />
          <Button v-if="!isNew" outlined icon="pi pi-play" :label="t('prompts.run.open')" data-testid="prompt-run" @click="runOpen = true" />
          <span class="muted" role="status">{{ saving ? t('prompts.saving') : dirty ? t('prompts.unsaved') : savedAt ? t('prompts.savedAt', { time: savedAt.toLocaleTimeString() }) : '' }}</span>
        </div>
        <p v-if="detail && !detail.published && !isNew" class="muted">{{ t('prompts.shareNeedsPublish') }}</p>
      </form>

      <article v-else-if="prompt" class="read" data-testid="prompt-read">
        <h1>{{ prompt.title }}</h1>
        <p v-if="prompt.description" class="muted">{{ prompt.description }}</p>
        <p class="muted">{{ t('prompts.by', { name: prompt.ownerName ?? '—' }) }} · {{ formatDate(prompt.updatedAt, locale.locale) }}</p>
        <div class="actions-row">
          <Button icon="pi pi-play" :label="t('prompts.run.open')" data-testid="prompt-run" @click="runOpen = true" />
          <Button v-if="prompt.visibility === 'Tenant'" outlined icon="pi pi-clone" :label="t('prompts.fork')" data-testid="prompt-fork" @click="fork" />
        </div>
        <RaaviEditor :model-value="body" :editable="false" :variable-names="knownNames" />
      </article>
      <PromptPanels v-if="prompt && !isNew" :key="prompt.id + ':' + prompt.revision" :prompt="prompt" :can-stats="prompt.isOwner" @restored="load" />
      <PromptRunDialog v-if="prompt" v-model:visible="runOpen" :prompt="prompt" />
    </template>

    <Dialog v-model:visible="publishOpen" modal :header="t('prompts.publish')" :style="{ inlineSize: 'min(28rem, 95vw)' }">
      <form class="form" @submit.prevent="publish">
        <p class="muted">{{ t('prompts.publishHelp') }}</p>
        <label for="p-note">{{ t('prompts.changeNote') }}</label>
        <InputText id="p-note" v-model="note" maxlength="500" data-testid="prompt-note" />
        <Button type="submit" severity="success" :label="t('prompts.publish')" data-testid="prompt-publish-confirm" />
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.edit { max-inline-size: 60rem; margin-inline: auto; display: grid; gap: var(--raavi-space-2); }
.head { display: flex; justify-content: space-between; align-items: center; gap: var(--raavi-space-2); flex-wrap: wrap; }
.badges { display: inline-flex; gap: 0.4rem; align-items: center; }
.form { display: grid; gap: var(--raavi-space-2); }
.grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(14rem, 1fr)); gap: var(--raavi-space-3); }
.full { inline-size: 100%; }
label, .label { font-weight: 600; font-size: 0.9rem; }
.modes { border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); display: flex; gap: var(--raavi-space-4); flex-wrap: wrap; padding: var(--raavi-space-2) var(--raavi-space-3); }
.check { display: inline-flex; gap: 0.35rem; align-items: center; font-weight: 400; }
.bar { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; position: sticky; inset-block-end: 0; background: var(--raavi-surface); padding-block: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); margin: 0; }
.vars code { margin-inline-end: 0.4rem; padding: 0 0.3rem; border-radius: 4px; background: color-mix(in srgb, var(--p-primary-color) 14%, transparent); }
h1 { margin: 0; font-size: 1.4rem; }
.read { display: grid; gap: var(--raavi-space-2); }
.actions-row { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.varmenu { border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: var(--raavi-space-2) var(--raavi-space-3); }
.varmenu summary { cursor: pointer; font-weight: 600; }
.vartable { inline-size: 100%; border-collapse: collapse; font-size: 0.85rem; margin-block: var(--raavi-space-2); }
.vartable th, .vartable td { text-align: start; padding: 0.2rem 0.4rem; border-block-end: 1px solid var(--raavi-border); vertical-align: top; }
.newdef { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; border: 1px dashed var(--raavi-border); border-radius: var(--raavi-radius-sm); }
</style>
