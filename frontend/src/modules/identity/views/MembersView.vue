<script setup lang="ts">
import UserAvatar from '@/app/UserAvatar.vue'
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Checkbox from 'primevue/checkbox'
import ToggleSwitch from 'primevue/toggleswitch'
import { identityApi, type Invitation, type Member } from '@/core/api/identity'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import { useAuthStore } from '@/stores/auth'
import OneTimeLinkDialog from '../components/OneTimeLinkDialog.vue'

const { t, te } = useI18n()
const confirm = useConfirm()
const toast = useToast()
const localeStore = useLocaleStore()
const auth = useAuthStore()

const members = ref<Member[]>([])
const invitations = ref<Invitation[]>([])
const loading = ref(true)
const inviteOpen = ref(false)
const inviteEmail = ref('')
const inviteAdmin = ref(false)
const link = ref<string | null>(null)

async function load() {
  loading.value = true
  try {
    ;[members.value, invitations.value] = await Promise.all([identityApi.members(), identityApi.invitations()])
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}

function fail(e: unknown) {
  toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
}

async function run(action: () => Promise<unknown>) {
  try {
    await action()
    await load()
  } catch (e) {
    fail(e)
    await load()
  }
}

async function invite() {
  try {
    const created = await identityApi.invite(inviteEmail.value, inviteAdmin.value)
    inviteOpen.value = false
    inviteEmail.value = ''
    inviteAdmin.value = false
    link.value = created.acceptUrl
    await load()
  } catch (e) {
    fail(e)
  }
}

function remove(m: Member) {
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('members.confirmRemove'),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: () => run(() => identityApi.removeMember(m.userId)),
  })
}

const date = (iso: string | null) => (iso ? formatDate(iso, localeStore.locale) : '—')
const isSelf = (m: Member) => m.userId === auth.me?.id

onMounted(load)
</script>

<template>
  <section class="page">
    <header class="page-head">
      <h1>{{ t('members.title') }}</h1>
      <Button icon="pi pi-user-plus" :label="t('members.invite')" data-testid="invite-open" @click="inviteOpen = true" />
    </header>

    <DataTable :value="members" :loading="loading" data-key="userId" size="small" striped-rows data-testid="members-table">
      <template #empty>{{ t('common.empty') }}</template>
      <Column field="displayName" :header="t('common.displayName')">
        <template #body="{ data }"><span class="who"><UserAvatar :user-id="data.userId" :name="data.displayName" :size="28" /> {{ data.displayName }}</span></template>
      </Column>
      <Column field="email" :header="t('common.email')">
        <template #body="{ data }"><span dir="ltr">{{ data.email }}</span></template>
      </Column>
      <Column :header="t('common.status')">
        <template #body="{ data }">
          <Tag :severity="data.status === 'Active' ? 'success' : 'warn'" :value="t(`members.statuses.${data.status}`)" />
        </template>
      </Column>
      <Column :header="t('members.admin')">
        <template #body="{ data }">
          <ToggleSwitch :model-value="data.isTenantAdministrator" :aria-label="t('members.admin')" @update:model-value="(v: boolean) => run(() => identityApi.setAdmin(data.userId, v))" />
        </template>
      </Column>
      <Column :header="t('members.lastLogin')">
        <template #body="{ data }">{{ date(data.lastLoginAt) }}</template>
      </Column>
      <Column :header="t('common.actions')">
        <template #body="{ data }">
          <div class="actions" v-if="!isSelf(data)">
            <Button v-if="data.status === 'Active'" text size="small" :label="t('members.suspend')" @click="run(() => identityApi.suspendMember(data.userId))" />
            <Button v-else text size="small" :label="data.status === 'PendingApproval' ? t('members.approve') : t('members.activate')" @click="run(() => identityApi.activateMember(data.userId))" />
            <Button text size="small" severity="danger" :label="t('members.remove')" @click="remove(data)" />
          </div>
        </template>
      </Column>
    </DataTable>

    <h2>{{ t('members.invitations') }}</h2>
    <DataTable :value="invitations" data-key="id" size="small">
      <template #empty>{{ t('common.empty') }}</template>
      <Column field="email" :header="t('common.email')">
        <template #body="{ data }"><span dir="ltr">{{ data.email }}</span></template>
      </Column>
      <Column :header="t('common.status')">
        <template #body="{ data }"><Tag severity="secondary" :value="t(`members.statuses.${data.status}`)" /></template>
      </Column>
      <Column :header="t('common.actions')">
        <template #body="{ data }">
          <Button v-if="data.status === 'pending'" text size="small" :label="t('members.revoke')" @click="run(() => identityApi.revokeInvitation(data.id))" />
        </template>
      </Column>
    </DataTable>

    <Dialog v-model:visible="inviteOpen" modal :header="t('members.invite')" :style="{ inlineSize: 'min(28rem, 95vw)' }">
      <form class="form" @submit.prevent="invite">
        <label for="invite-email">{{ t('common.email') }}</label>
        <InputText id="invite-email" v-model="inviteEmail" type="email" dir="ltr" required data-testid="invite-email" />
        <label class="check"><Checkbox v-model="inviteAdmin" binary input-id="invite-admin" /> {{ t('members.inviteAdmin') }}</label>
        <Button type="submit" :label="t('members.invite')" data-testid="invite-submit" />
      </form>
    </Dialog>

    <OneTimeLinkDialog :link="link" @close="link = null" />
  </section>
</template>

<style scoped>
.page { max-inline-size: 72rem; margin-inline: auto; display: grid; gap: var(--raavi-space-4); }
.page-head { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; }
h1 { margin: 0; font-size: 1.4rem; }
h2 { margin: var(--raavi-space-4) 0 0; font-size: 1.1rem; }
.actions { display: flex; gap: var(--raavi-space-1); flex-wrap: wrap; }
.form { display: grid; gap: var(--raavi-space-2); }
.check { display: flex; align-items: center; gap: var(--raavi-space-2); }
</style>
