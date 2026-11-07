<script setup lang="ts">
import { onMounted, reactive, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { identityApi, type Tenant } from '@/core/api/identity'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import OneTimeLinkDialog from '@/modules/identity/components/OneTimeLinkDialog.vue'

const { t, te } = useI18n()
const toast = useToast()
const localeStore = useLocaleStore()

const tenants = ref<Tenant[]>([])
const loading = ref(true)
const createOpen = ref(false)
const form = reactive({ code: '', slug: '', displayNameFa: '', displayNameEn: '', defaultLocale: 'fa' })
const suspendTarget = ref<Tenant | null>(null)
const reason = ref('')
const inviteTarget = ref<Tenant | null>(null)
const inviteEmail = ref('')
const link = ref<string | null>(null)
const locales = [
  { value: 'fa', label: 'فارسی' },
  { value: 'en', label: 'English' },
]

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })

async function load() {
  loading.value = true
  try {
    tenants.value = (await identityApi.tenants()).items
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}

async function create() {
  try {
    await identityApi.createTenant({ ...form })
    createOpen.value = false
    Object.assign(form, { code: '', slug: '', displayNameFa: '', displayNameEn: '', defaultLocale: 'fa' })
    await load()
  } catch (e) {
    fail(e)
  }
}

async function suspend() {
  if (!suspendTarget.value) return
  try {
    await identityApi.suspendTenant(suspendTarget.value.id, reason.value)
    suspendTarget.value = null
    reason.value = ''
    await load()
  } catch (e) {
    fail(e)
  }
}

async function activate(tenant: Tenant) {
  try {
    await identityApi.activateTenant(tenant.id)
    await load()
  } catch (e) {
    fail(e)
  }
}

async function invite() {
  if (!inviteTarget.value) return
  try {
    const created = await identityApi.inviteIntoTenant(inviteTarget.value.id, inviteEmail.value, true)
    inviteTarget.value = null
    inviteEmail.value = ''
    link.value = created.acceptUrl
  } catch (e) {
    fail(e)
  }
}

const severity = (s: Tenant['status']) => (s === 'Active' ? 'success' : s === 'Provisioning' ? 'info' : 'warn')
const name = (x: Tenant) => (localeStore.locale === 'fa' ? x.displayNameFa : x.displayNameEn)

onMounted(load)
</script>

<template>
  <section class="page">
    <header class="page-head">
      <h1>{{ t('tenants.title') }}</h1>
      <Button icon="pi pi-plus" :label="t('tenants.create')" @click="createOpen = true" />
    </header>

    <DataTable :value="tenants" :loading="loading" data-key="id" size="small" striped-rows data-testid="tenants-table">
      <template #empty>{{ t('common.empty') }}</template>
      <Column field="code" :header="t('tenants.code')"><template #body="{ data }"><code dir="ltr">{{ data.code }}</code></template></Column>
      <Column :header="t('common.displayName')"><template #body="{ data }">{{ name(data) }}</template></Column>
      <Column field="slug" :header="t('tenants.slug')"><template #body="{ data }"><span dir="ltr">{{ data.slug }}</span></template></Column>
      <Column :header="t('tenants.created')"><template #body="{ data }">{{ formatDate(data.createdAt, localeStore.locale) }}</template></Column>
      <Column :header="t('common.status')">
        <template #body="{ data }"><Tag :severity="severity(data.status)" :value="t(`tenants.statuses.${data.status}`)" /></template>
      </Column>
      <Column :header="t('common.actions')">
        <template #body="{ data }">
          <div class="actions">
            <Button text size="small" :label="t('tenants.inviteAdmin')" @click="inviteTarget = data" />
            <Button v-if="data.status === 'Active'" text size="small" severity="warn" :label="t('tenants.suspend')" @click="suspendTarget = data" />
            <Button v-else-if="data.status === 'Suspended' || data.status === 'Deactivated'" text size="small" :label="t('tenants.activate')" @click="activate(data)" />
          </div>
        </template>
      </Column>
    </DataTable>

    <Dialog v-model:visible="createOpen" modal :header="t('tenants.create')" :style="{ inlineSize: 'min(30rem, 95vw)' }">
      <form class="form" @submit.prevent="create">
        <label for="t-code">{{ t('tenants.code') }}</label>
        <InputText id="t-code" v-model="form.code" dir="ltr" placeholder="T001" />
        <label for="t-slug">{{ t('tenants.slug') }}</label>
        <InputText id="t-slug" v-model="form.slug" dir="ltr" placeholder="acme" />
        <label for="t-fa">{{ t('tenants.nameFa') }}</label>
        <InputText id="t-fa" v-model="form.displayNameFa" dir="rtl" />
        <label for="t-en">{{ t('tenants.nameEn') }}</label>
        <InputText id="t-en" v-model="form.displayNameEn" dir="ltr" />
        <label for="t-locale">{{ t('tenants.defaultLocale') }}</label>
        <Select input-id="t-locale" v-model="form.defaultLocale" :options="locales" option-label="label" option-value="value" />
        <Button type="submit" :label="t('common.create')" />
      </form>
    </Dialog>

    <Dialog :visible="!!suspendTarget" modal :header="t('tenants.suspend')" :style="{ inlineSize: 'min(28rem, 95vw)' }" @update:visible="(v) => !v && (suspendTarget = null)">
      <form class="form" @submit.prevent="suspend">
        <label for="reason">{{ t('tenants.reason') }}</label>
        <InputText id="reason" v-model="reason" />
        <Button type="submit" severity="warn" :label="t('tenants.suspend')" />
      </form>
    </Dialog>

    <Dialog :visible="!!inviteTarget" modal :header="t('tenants.inviteAdmin')" :style="{ inlineSize: 'min(28rem, 95vw)' }" @update:visible="(v) => !v && (inviteTarget = null)">
      <form class="form" @submit.prevent="invite">
        <label for="inv-email">{{ t('common.email') }}</label>
        <InputText id="inv-email" v-model="inviteEmail" type="email" dir="ltr" />
        <Button type="submit" :label="t('tenants.inviteAdmin')" />
      </form>
    </Dialog>

    <OneTimeLinkDialog :link="link" @close="link = null" />
  </section>
</template>

<style scoped>
.page { max-inline-size: 72rem; margin-inline: auto; display: grid; gap: var(--raavi-space-4); }
.page-head { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-3); }
h1 { margin: 0; font-size: 1.4rem; }
.actions { display: flex; gap: var(--raavi-space-1); flex-wrap: wrap; }
.form { display: grid; gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); }
.small { font-size: 0.8rem; }
</style>
