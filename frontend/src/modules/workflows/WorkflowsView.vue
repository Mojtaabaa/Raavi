<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import WorkflowFlow from './WorkflowFlow.vue'
import { OPERATORS, workflowsApi, type WfAction, type WfCondition, type WfField, type Workflow, type WorkflowRun } from '@/core/api/workflows'
import { wikiApi, type Space } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'

/**
 * Workflows of a space (Phase 10a): rules "when a page event happens and the conditions hold, run the actions". Administrators of the space
 * create, switch on/off, edit and delete them and read the run history. Runs happen on the server, asynchronously.
 */
const { t, te } = useI18n()
const route = useRoute()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()

const key = computed(() => String(route.params.key))
const space = ref<Space | null>(null)
const workflows = ref<Workflow[]>([])
const triggers = ref<string[]>([])
const loading = ref(true)
const editing = ref(false)
const editingId = ref<Workflow | null>(null)
const saving = ref(false)
const form = ref({ name: '', enabled: true, trigger: 'content.created', staleDays: null as number | null, conditions: [] as WfCondition[], actions: [] as WfAction[] })
const runsOf = ref<Workflow | null>(null)
const runs = ref<WorkflowRun[]>([])

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const isAdmin = computed(() => !!space.value?.rights.canAdminister)
const triggerLabel = (tr: string, days: number | null = null) => (tr === 'content.stale' ? t('workflows.triggers.content_stale', { n: days ?? '…' }) : t(`workflows.triggers.${tr.replace('.', '_')}`))
const showFlow = ref<string | null>(null)
const when = (iso: string) => formatDate(iso, locale.locale, { dateStyle: 'medium', timeStyle: 'short' })

async function load() {
  loading.value = true
  try {
    space.value = (await wikiApi.space(key.value)).value
    if (space.value.rights.canAdminister) {
      const [list, catalog] = await Promise.all([workflowsApi.list(key.value), workflowsApi.catalog()])
      workflows.value = list
      triggers.value = catalog.triggers
    }
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}
onMounted(load)
watch(key, load)

function openEditor(w: Workflow | null) {
  editingId.value = w
  form.value = w
    ? { name: w.name, enabled: w.enabled, trigger: w.trigger, staleDays: w.staleDays, conditions: w.conditions.map((c) => ({ ...c })), actions: w.actions.map((a) => ({ ...a })) }
    : { name: '', enabled: true, trigger: 'content.created', staleDays: null, conditions: [], actions: [{ type: 'AddLabel', label: '', recipients: null, message: null }] }
  editing.value = true
}

function triggerChanged() {
  form.value.staleDays = form.value.trigger === 'content.stale' ? (form.value.staleDays ?? 90) : null
}

function addCondition() {
  form.value.conditions.push({ field: 'Title', operator: 'Contains', value: '' })
}

function fieldChanged(c: WfCondition) {
  c.operator = OPERATORS[c.field][0]!
}

function addAction() {
  form.value.actions.push({ type: 'AddLabel', label: '', recipients: null, message: null })
}

function actionChanged(a: WfAction) {
  if (a.type === 'AddLabel') Object.assign(a, { recipients: null, message: null, label: a.label ?? '' })
  else Object.assign(a, { label: null, recipients: a.recipients ?? 'Watchers', message: a.message ?? '' })
}

const valid = computed(
  () =>
    form.value.name.trim() !== '' &&
    (form.value.trigger !== 'content.stale' || (!!form.value.staleDays && form.value.staleDays >= 1 && form.value.staleDays <= 3650)) &&
    form.value.actions.length > 0 &&
    form.value.conditions.every((c) => c.value.trim() !== '') &&
    form.value.actions.every((a) => (a.type === 'AddLabel' ? !!a.label?.trim() && !/\s/.test(a.label.trim()) : !!a.message?.trim())),
)

async function save() {
  if (!valid.value) return
  saving.value = true
  const body = {
    name: form.value.name.trim(),
    enabled: form.value.enabled,
    trigger: form.value.trigger,
    staleDays: form.value.trigger === 'content.stale' ? form.value.staleDays : null,
    conditions: form.value.conditions.map((c) => ({ ...c, value: c.value.trim() })),
    actions: form.value.actions.map((a) => (a.type === 'AddLabel' ? { type: a.type, label: a.label!.trim(), recipients: null, message: null } : { type: a.type, label: null, recipients: a.recipients, message: a.message!.trim() })),
  }
  try {
    if (editingId.value) await workflowsApi.update(editingId.value, body)
    else await workflowsApi.create(key.value, body)
    editing.value = false
    await load()
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}

async function toggle(w: Workflow) {
  try {
    await workflowsApi.update(w, { name: w.name, enabled: !w.enabled, trigger: w.trigger, staleDays: w.staleDays, conditions: w.conditions, actions: w.actions })
    await load()
  } catch (e) {
    fail(e)
  }
}

function removeWorkflow(w: Workflow) {
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('workflows.confirmDelete', { name: w.name }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await workflowsApi.remove(w.id)
        await load()
      } catch (e) {
        fail(e)
      }
    },
  })
}

async function showRuns(w: Workflow) {
  runsOf.value = w
  runs.value = []
  try {
    runs.value = await workflowsApi.runs(w.id)
  } catch (e) {
    fail(e)
  }
}

const runMessage = (m: string) => (te(`workflows.reasons.${m}`) ? t(`workflows.reasons.${m}`) : m)
const fields: WfField[] = ['Title', 'ContentType', 'Label']
</script>

<template>
  <section class="workflows" data-testid="workflows">
    <header class="head">
      <h1>{{ t('workflows.spaceTitle', { name: space?.name ?? key }) }}</h1>
      <Button v-if="isAdmin" icon="pi pi-plus" :label="t('workflows.new')" data-testid="new-workflow" @click="openEditor(null)" />
    </header>

    <p v-if="!loading && !isAdmin" class="muted" role="alert" data-testid="not-admin">{{ t('workflows.adminOnly') }}</p>
    <p v-else-if="!loading && workflows.length === 0" class="muted" data-testid="no-workflows">{{ t('workflows.none') }}</p>

    <ul class="list">
      <li v-for="w in workflows" :key="w.id" class="item" data-testid="workflow">
        <div class="main">
          <strong dir="auto">{{ w.name }}</strong>
          <span class="state" :class="{ on: w.enabled }">{{ w.enabled ? t('workflows.enabled') : t('workflows.disabled') }}</span>
          <span class="muted">{{ t('workflows.when') }} {{ triggerLabel(w.trigger, w.staleDays) }} · {{ t('workflows.summary', { conditions: w.conditions.length, actions: w.actions.length }) }}</span>
        </div>
        <div class="buttons">
          <Button size="small" text :label="w.enabled ? t('workflows.turnOff') : t('workflows.turnOn')" data-testid="toggle-workflow" @click="toggle(w)" />
          <Button size="small" text icon="pi pi-sitemap" :label="showFlow === w.id ? t('workflows.hideFlow') : t('workflows.showFlow')" :aria-expanded="showFlow === w.id" data-testid="toggle-flow" @click="showFlow = showFlow === w.id ? null : w.id" />
          <Button size="small" text icon="pi pi-history" :label="t('workflows.runs')" data-testid="show-runs" @click="showRuns(w)" />
          <Button size="small" text icon="pi pi-pencil" :label="t('common.edit')" data-testid="edit-workflow" @click="openEditor(w)" />
          <Button size="small" text severity="danger" icon="pi pi-trash" :aria-label="t('common.delete')" data-testid="delete-workflow" @click="removeWorkflow(w)" />
        </div>
        <WorkflowFlow v-if="showFlow === w.id" class="flowbox" :trigger="w.trigger" :stale-days="w.staleDays" :conditions="w.conditions" :actions="w.actions" />
      </li>
    </ul>

    <Dialog v-model:visible="editing" modal :header="editingId ? t('workflows.edit') : t('workflows.new')" :style="{ inlineSize: 'min(44rem, 95vw)' }">
      <form class="form" @submit.prevent="save">
        <label for="wf-name">{{ t('workflows.name') }}</label>
        <InputText id="wf-name" v-model="form.name" maxlength="200" dir="auto" data-testid="wf-name" />
        <label class="check"><input v-model="form.enabled" type="checkbox" data-testid="wf-enabled" /> {{ t('workflows.enabled') }}</label>

        <label for="wf-trigger">{{ t('workflows.trigger') }}</label>
        <select id="wf-trigger" v-model="form.trigger" data-testid="wf-trigger" @change="triggerChanged">
          <option v-for="tr in triggers" :key="tr" :value="tr">{{ triggerLabel(tr) }}</option>
        </select>
        <template v-if="form.trigger === 'content.stale'">
          <label for="wf-days">{{ t('workflows.staleDays') }}</label>
          <input id="wf-days" v-model.number="form.staleDays" type="number" min="1" max="3650" data-testid="wf-stale-days" />
          <p class="muted">{{ t('workflows.staleHint') }}</p>
        </template>

        <fieldset>
          <legend>{{ t('workflows.conditions') }}</legend>
          <p v-if="form.conditions.length === 0" class="muted">{{ t('workflows.noConditions') }}</p>
          <div v-for="(c, i) in form.conditions" :key="i" class="row" data-testid="wf-condition">
            <select v-model="c.field" :aria-label="t('workflows.field')" @change="fieldChanged(c)">
              <option v-for="f in fields" :key="f" :value="f">{{ t(`workflows.fields.${f}`) }}</option>
            </select>
            <select v-model="c.operator" :aria-label="t('workflows.operator')">
              <option v-for="o in OPERATORS[c.field]" :key="o" :value="o">{{ t(`workflows.operators.${o}`) }}</option>
            </select>
            <InputText v-model="c.value" maxlength="200" dir="auto" :aria-label="t('workflows.value')" data-testid="wf-condition-value" />
            <Button type="button" text rounded severity="danger" icon="pi pi-times" :aria-label="t('common.delete')" @click="form.conditions.splice(i, 1)" />
          </div>
          <Button type="button" size="small" severity="secondary" outlined icon="pi pi-plus" :label="t('workflows.addCondition')" data-testid="add-condition" :disabled="form.conditions.length >= 10" @click="addCondition" />
        </fieldset>

        <fieldset>
          <legend>{{ t('workflows.actions') }}</legend>
          <div v-for="(a, i) in form.actions" :key="i" class="row" data-testid="wf-action">
            <select v-model="a.type" :aria-label="t('workflows.actionType')" data-testid="wf-action-type" @change="actionChanged(a)">
              <option value="AddLabel">{{ t('workflows.actionTypes.AddLabel') }}</option>
              <option value="Notify">{{ t('workflows.actionTypes.Notify') }}</option>
            </select>
            <InputText v-if="a.type === 'AddLabel'" v-model="a.label" maxlength="100" :placeholder="t('workflows.labelName')" :aria-label="t('workflows.labelName')" data-testid="wf-action-label" />
            <template v-else>
              <select v-model="a.recipients" :aria-label="t('workflows.recipients')">
                <option value="Watchers">{{ t('workflows.recipientsWatchers') }}</option>
                <option value="Actor">{{ t('workflows.recipientsActor') }}</option>
              </select>
              <InputText v-model="a.message" maxlength="200" dir="auto" :placeholder="t('workflows.message')" :aria-label="t('workflows.message')" data-testid="wf-action-message" />
            </template>
            <Button type="button" text rounded severity="danger" icon="pi pi-times" :aria-label="t('common.delete')" :disabled="form.actions.length <= 1" @click="form.actions.splice(i, 1)" />
          </div>
          <Button type="button" size="small" severity="secondary" outlined icon="pi pi-plus" :label="t('workflows.addAction')" data-testid="add-action" :disabled="form.actions.length >= 5" @click="addAction" />
        </fieldset>

        <section class="preview" :aria-label="t('workflows.flow')">
          <h3>{{ t('workflows.flow') }}</h3>
          <WorkflowFlow :trigger="form.trigger" :stale-days="form.staleDays" :conditions="form.conditions" :actions="form.actions" />
        </section>

        <Button type="submit" :label="t('common.save')" :loading="saving" :disabled="!valid" data-testid="save-workflow" />
      </form>
    </Dialog>

    <Dialog :visible="!!runsOf" modal :header="t('workflows.runsOf', { name: runsOf?.name ?? '' })" :style="{ inlineSize: 'min(48rem, 95vw)' }" @update:visible="runsOf = null">
      <p v-if="runs.length === 0" class="muted" data-testid="no-runs">{{ t('workflows.noRuns') }}</p>
      <table v-else class="runs">
        <thead>
          <tr>
            <th scope="col">{{ t('workflows.time') }}</th>
            <th scope="col">{{ t('workflows.page') }}</th>
            <th scope="col">{{ t('common.status') }}</th>
            <th scope="col">{{ t('workflows.result') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="r in runs" :key="r.id" data-testid="run">
            <td>{{ when(r.startedAt) }}</td>
            <td dir="auto">{{ r.contentTitle ?? '—' }}</td>
            <td><span class="badge" :class="r.status.toLowerCase()">{{ t(`workflows.status.${r.status}`) }}</span><span v-if="r.attempts > 1" class="muted"> ×{{ r.attempts }}</span></td>
            <td dir="auto">{{ runMessage(r.message) }}</td>
          </tr>
        </tbody>
      </table>
    </Dialog>
  </section>
</template>

<style scoped>
.workflows { display: grid; gap: var(--raavi-space-3); max-inline-size: 60rem; }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: center; }
h1 { margin: 0; font-size: 1.5rem; }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.item { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.main { display: grid; gap: 0.2rem; min-inline-size: 0; }
.state { font-size: 0.8rem; color: var(--raavi-text-muted); }
.state.on { color: var(--raavi-success, #15803d); }
.flowbox { inline-size: 100%; }
.preview h3 { margin: 0 0 0.5rem; font-size: 0.95rem; }
.form input[type='number'] { font: inherit; padding: 0.35rem; max-inline-size: 8rem; }
.buttons { display: flex; flex-wrap: wrap; gap: 0.25rem; align-items: center; }
.form { display: grid; gap: var(--raavi-space-2); }
.form select, .row select { font: inherit; padding: 0.35rem; }
.check { display: flex; gap: 0.5rem; align-items: center; }
fieldset { border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); display: grid; gap: var(--raavi-space-2); padding: var(--raavi-space-2); }
.row { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.row :deep(input[type='text']), .row :deep(.p-inputtext) { flex: 1 1 10rem; }
.runs { inline-size: 100%; border-collapse: collapse; }
.runs th, .runs td { padding: 0.35rem 0.5rem; border-block-end: 1px solid var(--raavi-border); text-align: start; overflow-wrap: anywhere; }
.badge { padding: 0.1rem 0.5rem; border-radius: 999px; font-size: 0.8rem; background: var(--raavi-surface-muted, #eee); }
.badge.succeeded { color: #166534; background: #dcfce7; }
.badge.failed { color: #991b1b; background: #fee2e2; }
.muted { color: var(--raavi-text-muted); margin: 0; }
</style>
