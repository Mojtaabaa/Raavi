<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { webhooksApi, type Webhook, type WebhookDelivery } from '@/core/api/integrations'
import { wikiApi, type SpaceSummary } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'

/**
 * Outgoing webhooks of the workspace (Phase 11d): where Raavi calls, for which events and (optionally) which space, with a signing secret that is
 * shown once. Events carry identifiers only. Administrators can test a webhook, rotate its secret and read the delivery log.
 */
const { t, te } = useI18n()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()

const hooks = ref<Webhook[]>([])
const events = ref<string[]>([])
const hostPolicy = ref(true)
const spaces = ref<SpaceSummary[]>([])
const editing = ref(false)
const editingHook = ref<Webhook | null>(null)
const saving = ref(false)
const form = ref({ name: '', url: '', events: [] as string[], spaceId: '' as string, enabled: true })
const secret = ref<string | null>(null)
const logOf = ref<Webhook | null>(null)
const log = ref<WebhookDelivery[]>([])

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const when = (iso: string) => formatDate(iso, locale.locale, { dateStyle: 'medium', timeStyle: 'short' })
const spaceName = (id: string | null) => (id ? (spaces.value.find((s) => s.id === id)?.name ?? id) : t('webhooks.allSpaces'))
const valid = computed(() => form.value.name.trim() !== '' && form.value.url.trim() !== '' && form.value.events.length > 0)
const groups = computed(() => {
  const g = new Map<string, string[]>()
  for (const e of events.value) g.set(e.split('.')[0]!, [...(g.get(e.split('.')[0]!) ?? []), e])
  return [...g.entries()]
})

async function load() {
  try {
    const [list, catalog, sp] = await Promise.all([webhooksApi.list(), webhooksApi.catalog(), wikiApi.spaces({ includeArchived: false }).catch(() => [])])
    hooks.value = list
    events.value = catalog.events
    hostPolicy.value = catalog.hostPolicyConfigured
    spaces.value = sp
  } catch (e) {
    fail(e)
  }
}
onMounted(load)

function openEditor(w: Webhook | null) {
  editingHook.value = w
  form.value = w ? { name: w.name, url: w.url, events: [...w.events], spaceId: w.spaceId ?? '', enabled: w.enabled } : { name: '', url: '', events: [], spaceId: '', enabled: true }
  editing.value = true
}

function toggleEvent(e: string) {
  form.value.events = form.value.events.includes(e) ? form.value.events.filter((x) => x !== e) : [...form.value.events, e]
}

async function save() {
  if (!valid.value) return
  saving.value = true
  const body = { name: form.value.name.trim(), url: form.value.url.trim(), events: form.value.events, spaceId: form.value.spaceId || null, enabled: form.value.enabled }
  try {
    if (editingHook.value) await webhooksApi.update(editingHook.value, body)
    else secret.value = (await webhooksApi.create(body)).secret
    editing.value = false
    await load()
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}

async function rotate(w: Webhook) {
  try {
    secret.value = (await webhooksApi.rotateSecret(w.id)).secret
    await load()
  } catch (e) {
    fail(e)
  }
}

async function test(w: Webhook) {
  try {
    const r = await webhooksApi.test(w.id)
    toast.add(r.success ? { severity: 'success', summary: t('webhooks.testOk', { status: r.httpStatus ?? '' }), life: 4000 } : { severity: 'warn', summary: t('webhooks.testFailed', { reason: r.error ?? '' }), life: 6000 })
  } catch (e) {
    fail(e)
  }
}

async function showLog(w: Webhook) {
  logOf.value = w
  log.value = []
  try {
    log.value = await webhooksApi.deliveries(w.id)
  } catch (e) {
    fail(e)
  }
}

function remove(w: Webhook) {
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('webhooks.confirmDelete', { name: w.name }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await webhooksApi.remove(w.id)
        await load()
      } catch (e) {
        fail(e)
      }
    },
  })
}

async function copySecret() {
  if (secret.value) await navigator.clipboard?.writeText(secret.value).catch(() => undefined)
}
</script>

<template>
  <section class="hooks" data-testid="webhooks">
    <header class="head">
      <h1>{{ t('webhooks.title') }}</h1>
      <Button icon="pi pi-plus" :label="t('webhooks.new')" :disabled="!hostPolicy" data-testid="new-webhook" @click="openEditor(null)" />
    </header>
    <p v-if="!hostPolicy" class="warn" role="alert" data-testid="no-hosts">{{ t('webhooks.noHosts') }}</p>
    <p class="muted">{{ t('webhooks.intro') }}</p>
    <p v-if="hooks.length === 0" class="muted" data-testid="no-webhooks">{{ t('webhooks.none') }}</p>

    <ul class="list">
      <li v-for="w in hooks" :key="w.id" class="item" data-testid="webhook">
        <div class="main">
          <strong dir="auto">{{ w.name }}</strong>
          <span class="muted url" dir="ltr">{{ w.url }}</span>
          <span class="muted">{{ spaceName(w.spaceId) }} · {{ t('webhooks.eventCount', { n: w.events.length }) }}</span>
          <span v-if="!w.enabled" class="warn" data-testid="webhook-off">{{ w.disabledReason === 'repeated_failures' ? t('webhooks.offFailures') : t('webhooks.off') }}</span>
          <span v-else-if="w.consecutiveFailures > 0" class="warn">{{ t('webhooks.failing', { n: w.consecutiveFailures }) }}</span>
          <span v-if="w.lastSuccessAt" class="muted">{{ t('webhooks.lastOk', { time: when(w.lastSuccessAt) }) }}</span>
        </div>
        <div class="buttons">
          <Button size="small" text icon="pi pi-send" :label="t('webhooks.test')" data-testid="test-webhook" @click="test(w)" />
          <Button size="small" text icon="pi pi-history" :label="t('webhooks.log')" data-testid="log-webhook" @click="showLog(w)" />
          <Button size="small" text icon="pi pi-pencil" :label="t('common.edit')" data-testid="edit-webhook" @click="openEditor(w)" />
          <Button size="small" text icon="pi pi-key" :label="t('webhooks.rotate')" data-testid="rotate-webhook" @click="rotate(w)" />
          <Button size="small" text severity="danger" icon="pi pi-trash" :aria-label="t('common.delete')" data-testid="delete-webhook" @click="remove(w)" />
        </div>
      </li>
    </ul>

    <Dialog v-model:visible="editing" modal :header="editingHook ? t('webhooks.edit') : t('webhooks.new')" :style="{ inlineSize: 'min(40rem, 95vw)' }">
      <form class="form" @submit.prevent="save">
        <label for="wh-name">{{ t('webhooks.name') }}</label>
        <InputText id="wh-name" v-model="form.name" maxlength="100" dir="auto" data-testid="wh-name" />
        <label for="wh-url">{{ t('webhooks.url') }}</label>
        <InputText id="wh-url" v-model="form.url" maxlength="500" dir="ltr" placeholder="https://example.com/raavi-hook" data-testid="wh-url" />
        <label for="wh-space">{{ t('webhooks.space') }}</label>
        <select id="wh-space" v-model="form.spaceId" data-testid="wh-space">
          <option value="">{{ t('webhooks.allSpaces') }}</option>
          <option v-for="s in spaces" :key="s.id" :value="s.id">{{ s.name }}</option>
        </select>
        <fieldset>
          <legend>{{ t('webhooks.events') }}</legend>
          <div v-for="[group, list] in groups" :key="group" class="group">
            <strong>{{ t(`webhooks.groups.${group}`) }}</strong>
            <label v-for="e in list" :key="e" class="check">
              <input type="checkbox" :checked="form.events.includes(e)" :data-testid="`ev-${e}`" @change="toggleEvent(e)" /> <code>{{ e }}</code>
            </label>
          </div>
        </fieldset>
        <label v-if="editingHook" class="check"><input v-model="form.enabled" type="checkbox" data-testid="wh-enabled" /> {{ t('webhooks.enabled') }}</label>
        <Button type="submit" :label="t('common.save')" :loading="saving" :disabled="!valid" data-testid="save-webhook" />
      </form>
    </Dialog>

    <Dialog :visible="!!secret" modal :closable="false" :header="t('webhooks.secretTitle')" :style="{ inlineSize: 'min(36rem, 95vw)' }">
      <p>{{ t('webhooks.secretOnce') }}</p>
      <code class="secret" dir="ltr" data-testid="secret">{{ secret }}</code>
      <div class="buttons">
        <Button icon="pi pi-copy" :label="t('common.copy')" severity="secondary" data-testid="copy-secret" @click="copySecret" />
        <Button :label="t('webhooks.secretDone')" data-testid="secret-done" @click="secret = null" />
      </div>
      <p class="muted">{{ t('webhooks.verify') }}</p>
    </Dialog>

    <Dialog :visible="!!logOf" modal :header="t('webhooks.logOf', { name: logOf?.name ?? '' })" :style="{ inlineSize: 'min(48rem, 95vw)' }" @update:visible="logOf = null">
      <p v-if="log.length === 0" class="muted" data-testid="no-log">{{ t('webhooks.noLog') }}</p>
      <table v-else class="log">
        <thead>
          <tr>
            <th scope="col">{{ t('workflows.time') }}</th>
            <th scope="col">{{ t('webhooks.event') }}</th>
            <th scope="col">{{ t('common.status') }}</th>
            <th scope="col">{{ t('webhooks.attempts') }}</th>
          </tr>
        </thead>
        <tbody>
          <tr v-for="d in log" :key="d.id" data-testid="delivery">
            <td>{{ when(d.at) }}</td>
            <td><code>{{ d.eventType }}</code></td>
            <td><span :class="d.status === 'Succeeded' ? 'ok' : 'bad'">{{ d.status === 'Succeeded' ? t('webhooks.delivered') : t('webhooks.deliveryFailed') }}</span> <span class="muted">{{ d.httpStatus ?? d.error ?? '' }}</span></td>
            <td>{{ d.attempts }}</td>
          </tr>
        </tbody>
      </table>
    </Dialog>
  </section>
</template>

<style scoped>
.hooks { display: grid; gap: var(--raavi-space-3); max-inline-size: 60rem; }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: center; }
h1 { margin: 0; font-size: 1.5rem; }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.item { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.main { display: grid; gap: 0.2rem; min-inline-size: 0; }
.url { overflow-wrap: anywhere; }
.buttons { display: flex; flex-wrap: wrap; gap: 0.25rem; align-items: center; }
.form { display: grid; gap: var(--raavi-space-2); }
.form select { font: inherit; padding: 0.35rem; }
fieldset { border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); display: grid; gap: var(--raavi-space-2); padding: var(--raavi-space-2); }
.group { display: grid; gap: 0.15rem; }
.check { display: flex; gap: 0.5rem; align-items: center; }
.secret { display: block; padding: 0.75rem; background: var(--raavi-surface-muted, #f3f4f6); border-radius: var(--raavi-radius-sm); overflow-wrap: anywhere; margin-block: 0.75rem; }
.log { inline-size: 100%; border-collapse: collapse; }
.log th, .log td { padding: 0.35rem 0.5rem; border-block-end: 1px solid var(--raavi-border); text-align: start; }
.ok { color: #166534; }
.bad { color: #991b1b; }
.warn { color: var(--raavi-danger, #b91c1c); margin: 0; }
.muted { color: var(--raavi-text-muted); margin: 0; }
</style>
