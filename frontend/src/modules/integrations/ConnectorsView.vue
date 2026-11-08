<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { connectorsApi, type Connector, type ConnectorKind, type IssueInfo } from '@/core/api/integrations'
import { errorMessage } from '@/core/api/errors'

/**
 * Issue trackers of the workspace (Phase 13a): read-only connections to GitHub, GitLab or Jira. The access token is write-only here — it is sent when
 * saving and never comes back. The "check a reference" panel resolves a reference through a connector exactly as members will.
 */
const { t, te } = useI18n()
const toast = useToast()
const confirm = useConfirm()

const kinds: ConnectorKind[] = ['GitHub', 'GitLab', 'Jira']
const list = ref<Connector[]>([])
const hostPolicy = ref(true)
const editing = ref(false)
const editingItem = ref<Connector | null>(null)
const saving = ref(false)
const form = ref({ name: '', kind: 'GitHub' as ConnectorKind, baseUrl: '', token: '', removeToken: false, enabled: true })
const checkConnector = ref('')
const checkReference = ref('')
const checking = ref(false)
const found = ref<IssueInfo | null>(null)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const valid = computed(() => form.value.name.trim() !== '' && form.value.baseUrl.trim() !== '')
const enabledList = computed(() => list.value.filter((c) => c.enabled))

async function load() {
  try {
    const [items, catalog] = await Promise.all([connectorsApi.list(), connectorsApi.catalog()])
    list.value = items
    hostPolicy.value = catalog.hostPolicyConfigured
    if (!enabledList.value.some((c) => c.id === checkConnector.value)) checkConnector.value = enabledList.value[0]?.id ?? ''
  } catch (e) {
    fail(e)
  }
}
onMounted(load)

function openEditor(c: Connector | null) {
  editingItem.value = c
  form.value = c
    ? { name: c.name, kind: c.kind, baseUrl: c.baseUrl, token: '', removeToken: false, enabled: c.enabled }
    : { name: '', kind: 'GitHub', baseUrl: '', token: '', removeToken: false, enabled: true }
  editing.value = true
}

async function save() {
  if (!valid.value) return
  saving.value = true
  const f = form.value
  // undefined keeps the stored token; '' removes it; a value replaces it
  const token = f.removeToken ? '' : f.token.trim() === '' ? (editingItem.value ? undefined : null) : f.token.trim()
  const body = { name: f.name.trim(), kind: f.kind, baseUrl: f.baseUrl.trim(), token, enabled: f.enabled }
  try {
    if (editingItem.value) await connectorsApi.update(editingItem.value, body)
    else await connectorsApi.create(body)
    editing.value = false
    await load()
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}

function remove(c: Connector) {
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('connectors.confirmDelete', { name: c.name }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await connectorsApi.remove(c.id)
        await load()
      } catch (e) {
        fail(e)
      }
    },
  })
}

async function lookup() {
  if (!checkConnector.value || checkReference.value.trim() === '') return
  checking.value = true
  found.value = null
  try {
    found.value = await connectorsApi.lookup(checkConnector.value, checkReference.value.trim())
  } catch (e) {
    fail(e)
  } finally {
    checking.value = false
  }
}
</script>

<template>
  <section class="connectors" data-testid="connectors">
    <header class="head">
      <h1>{{ t('connectors.title') }}</h1>
      <Button icon="pi pi-plus" :label="t('connectors.new')" :disabled="!hostPolicy" data-testid="new-connector" @click="openEditor(null)" />
    </header>
    <p v-if="!hostPolicy" class="warn" role="alert" data-testid="no-hosts">{{ t('connectors.noHosts') }}</p>
    <p class="muted">{{ t('connectors.intro') }}</p>
    <p v-if="list.length === 0" class="muted" data-testid="no-connectors">{{ t('connectors.none') }}</p>

    <ul class="list">
      <li v-for="c in list" :key="c.id" class="item" data-testid="connector">
        <div class="main">
          <strong dir="auto">{{ c.name }}</strong>
          <span class="muted">{{ c.kind }} · <span dir="ltr">{{ c.baseUrl }}</span></span>
          <span class="muted">{{ c.hasToken ? t('connectors.hasToken') : t('connectors.noToken') }}</span>
          <span v-if="!c.enabled" class="warn" data-testid="connector-off">{{ t('connectors.off') }}</span>
        </div>
        <div class="buttons">
          <Button size="small" text icon="pi pi-pencil" :label="t('common.edit')" data-testid="edit-connector" @click="openEditor(c)" />
          <Button size="small" text severity="danger" icon="pi pi-trash" :aria-label="t('common.delete')" data-testid="delete-connector" @click="remove(c)" />
        </div>
      </li>
    </ul>

    <section v-if="enabledList.length > 0" class="check" aria-labelledby="check-title">
      <h2 id="check-title">{{ t('connectors.check') }}</h2>
      <p class="muted">{{ t('connectors.checkHint') }}</p>
      <form class="inline" @submit.prevent="lookup">
        <select v-model="checkConnector" :aria-label="t('connectors.kind')" data-testid="check-connector">
          <option v-for="c in enabledList" :key="c.id" :value="c.id">{{ c.name }}</option>
        </select>
        <InputText v-model="checkReference" :aria-label="t('connectors.reference')" dir="ltr" maxlength="300" data-testid="check-reference" />
        <Button type="submit" :label="t('connectors.lookup')" :loading="checking" :disabled="checkReference.trim() === ''" data-testid="check-run" />
      </form>
      <div v-if="found" class="result" data-testid="check-result">
        <strong dir="auto">{{ found.title }}</strong>
        <span class="muted">{{ t(`connectors.type_${found.type}`) }} · {{ found.status }}</span>
        <a :href="found.url" target="_blank" rel="noopener noreferrer" data-testid="check-link">{{ t('connectors.open') }}</a>
      </div>
    </section>

    <Dialog v-model:visible="editing" modal :header="editingItem ? t('connectors.edit') : t('connectors.new')" :style="{ inlineSize: 'min(36rem, 95vw)' }">
      <form class="form" @submit.prevent="save">
        <label for="cn-name">{{ t('connectors.name') }}</label>
        <InputText id="cn-name" v-model="form.name" maxlength="100" dir="auto" data-testid="cn-name" />
        <label for="cn-kind">{{ t('connectors.kind') }}</label>
        <select id="cn-kind" v-model="form.kind" data-testid="cn-kind">
          <option v-for="k in kinds" :key="k" :value="k">{{ k }}</option>
        </select>
        <label for="cn-url">{{ t('connectors.baseUrl') }}</label>
        <InputText id="cn-url" v-model="form.baseUrl" maxlength="300" dir="ltr" placeholder="https://" data-testid="cn-url" />
        <small class="muted">{{ t('connectors.baseUrlHint') }}</small>
        <label for="cn-token">{{ t('connectors.token') }}</label>
        <InputText
          id="cn-token"
          v-model="form.token"
          type="password"
          autocomplete="new-password"
          dir="ltr"
          :disabled="form.removeToken"
          :placeholder="editingItem?.hasToken ? t('connectors.tokenKeep') : t('connectors.tokenNew')"
          data-testid="cn-token"
        />
        <label v-if="editingItem?.hasToken" class="checkbox"><input v-model="form.removeToken" type="checkbox" data-testid="cn-remove-token" /> {{ t('connectors.tokenRemove') }}</label>
        <label v-if="editingItem" class="checkbox"><input v-model="form.enabled" type="checkbox" data-testid="cn-enabled" /> {{ t('connectors.enabled') }}</label>
        <Button type="submit" :label="t('common.save')" :loading="saving" :disabled="!valid" data-testid="save-connector" />
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.connectors { display: grid; gap: var(--raavi-space-3); max-inline-size: 60rem; }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: center; }
h1 { margin: 0; font-size: 1.5rem; }
h2 { margin: 0; font-size: 1.1rem; }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.item { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.main { display: grid; gap: 0.2rem; min-inline-size: 0; overflow-wrap: anywhere; }
.buttons { display: flex; flex-wrap: wrap; gap: 0.25rem; align-items: center; }
.check { display: grid; gap: var(--raavi-space-2); padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.inline { display: flex; flex-wrap: wrap; gap: var(--raavi-space-2); align-items: center; }
.inline select, .form select { font: inherit; padding: 0.35rem; }
.result { display: grid; gap: 0.2rem; }
.form { display: grid; gap: var(--raavi-space-2); }
.checkbox { display: flex; gap: 0.5rem; align-items: center; }
.warn { color: var(--raavi-danger, #b91c1c); margin: 0; }
.muted { color: var(--raavi-text-muted); margin: 0; }
</style>
