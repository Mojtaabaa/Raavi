<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import ToggleSwitch from 'primevue/toggleswitch'
import RadioButton from 'primevue/radiobutton'
import Select from 'primevue/select'
import InputText from 'primevue/inputtext'
import Tag from 'primevue/tag'
import { identityApi, type ProviderHealth, type ProviderPolicy, type ProviderSummary, type ProvisioningMode } from '@/core/api/identity'
import { errorMessage } from '@/core/api/errors'
import { useLocaleStore } from '@/stores/locale'

/** Tenant administrators choose which globally defined sign-in methods this workspace uses (ADR-006). */
const { t, te } = useI18n()
const toast = useToast()
const localeStore = useLocaleStore()

type Row = ProviderPolicy & { domainsText: string; groupsText: string }
const rows = ref<Row[]>([])
const names = ref<Record<string, ProviderSummary>>({})
const health = ref<Record<string, ProviderHealth | 'testing'>>({})
const saving = ref(false)
const modes: { value: ProvisioningMode; label: () => string }[] = [
  { value: 'ExistingUsersOnly', label: () => t('providers.modes.ExistingUsersOnly') },
  { value: 'AutomaticCreation', label: () => t('providers.modes.AutomaticCreation') },
  { value: 'AdministratorApproval', label: () => t('providers.modes.AdministratorApproval') },
]
const defaultId = ref<string | null>(null)

const split = (s: string) => s.split(',').map((x) => x.trim()).filter(Boolean)
const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })

async function load() {
  try {
    const [policies, enabled] = await Promise.all([identityApi.providerPolicies(), identityApi.providers()])
    rows.value = policies.map((p) => ({ ...p, domainsText: p.allowedEmailDomains.join(', '), groupsText: p.allowedExternalGroups.join(', ') }))
    names.value = Object.fromEntries(enabled.map((p) => [p.id, p]))
    defaultId.value = policies.find((p) => p.isDefault)?.providerId ?? null
  } catch (e) {
    fail(e)
  }
}

async function save() {
  saving.value = true
  try {
    await identityApi.saveProviderPolicies(
      rows.value.map((r, i) => ({
        providerId: r.providerId,
        enabled: r.enabled,
        isDefault: r.enabled && r.providerId === defaultId.value,
        displayOrder: i,
        provisioning: r.provisioning,
        allowedEmailDomains: split(r.domainsText),
        allowedExternalGroups: split(r.groupsText),
      })),
    )
    toast.add({ severity: 'success', summary: t('providers.saved'), life: 3000 })
    await load()
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}

async function test(id: string) {
  health.value[id] = 'testing'
  try {
    health.value[id] = await identityApi.testProvider(id)
  } catch (e) {
    delete health.value[id]
    fail(e)
  }
}

const title = (id: string) => {
  const p = names.value[id]
  if (!p) return id
  return localeStore.locale === 'fa' ? p.displayName.fa || p.displayName.en : p.displayName.en || p.displayName.fa
}

onMounted(load)
</script>

<template>
  <section class="page">
    <header class="page-head">
      <h1>{{ t('providers.title') }}</h1>
      <Button icon="pi pi-check" :label="t('common.save')" :loading="saving" data-testid="providers-save" @click="save" />
    </header>
    <p class="muted">{{ t('providers.intro') }}</p>

    <article v-for="r in rows" :key="r.providerId" class="provider" :data-testid="`policy-${r.providerId}`">
      <div class="row">
        <ToggleSwitch v-model="r.enabled" :input-id="`en-${r.providerId}`" />
        <label :for="`en-${r.providerId}`" class="name">{{ title(r.providerId) }} <code dir="ltr">{{ r.providerId }}</code></label>
        <label class="row small"><RadioButton v-model="defaultId" :value="r.providerId" :disabled="!r.enabled" /> {{ t('providers.default') }}</label>
        <Button text size="small" icon="pi pi-heart" :label="t('providers.test')" @click="test(r.providerId)" />
        <Tag v-if="health[r.providerId] && health[r.providerId] !== 'testing'" :severity="(health[r.providerId] as ProviderHealth).isHealthy ? 'success' : 'danger'" :value="(health[r.providerId] as ProviderHealth).statusCode" />
      </div>
      <div v-if="r.enabled && r.providerId !== 'local'" class="grid">
        <label :for="`mode-${r.providerId}`">{{ t('providers.provisioning') }}</label>
        <Select :input-id="`mode-${r.providerId}`" v-model="r.provisioning" :options="modes" option-value="value">
          <template #value="{ value }">{{ modes.find((m) => m.value === value)?.label() }}</template>
          <template #option="{ option }">{{ option.label() }}</template>
        </Select>
        <label :for="`dom-${r.providerId}`">{{ t('providers.domains') }}</label>
        <InputText :id="`dom-${r.providerId}`" v-model="r.domainsText" dir="ltr" placeholder="company.com, partner.com" />
        <label :for="`grp-${r.providerId}`">{{ t('providers.groups') }}</label>
        <InputText :id="`grp-${r.providerId}`" v-model="r.groupsText" dir="ltr" placeholder="engineering, raavi-users" />
      </div>
    </article>
  </section>
</template>

<style scoped>
.page { max-inline-size: 60rem; margin-inline: auto; display: grid; gap: var(--raavi-space-4); }
.page-head { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-3); }
h1 { margin: 0; font-size: 1.4rem; }
.provider { padding: var(--raavi-space-4); background: var(--raavi-surface); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-md); display: grid; gap: var(--raavi-space-3); }
.row { display: flex; align-items: center; gap: var(--raavi-space-3); flex-wrap: wrap; }
.name { font-weight: 600; flex: 1; }
.grid { display: grid; grid-template-columns: minmax(10rem, max-content) 1fr; gap: var(--raavi-space-2) var(--raavi-space-3); align-items: center; }
@media (max-width: 640px) { .grid { grid-template-columns: 1fr; } }
.muted { color: var(--raavi-text-muted); }
.small { font-size: 0.85rem; }
</style>
