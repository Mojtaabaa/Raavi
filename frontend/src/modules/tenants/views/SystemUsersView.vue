<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Checkbox from 'primevue/checkbox'
import Tag from 'primevue/tag'
import { opsApi, type SystemUser } from '@/core/api/ops'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'

/**
 * Accounts of the whole installation with the number of workspaces each belongs to (ADR-051). Accounts that belong to none (left over after workspaces
 * were deleted) can be removed one by one, selected, or all at once; the server refuses anyone who still has a workspace or a system role.
 */
const { t, te } = useI18n()
const toast = useToast()
const localeStore = useLocaleStore()

const users = ref<SystemUser[]>([])
const total = ref(0)
const without = ref(0)
const loading = ref(true)
const search = ref('')
const onlyWithout = ref(false)
const selected = ref<SystemUser[]>([])
const confirm = ref<{ all: boolean; count: number } | null>(null)
const busy = ref(false)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
const deletable = (u: SystemUser) => u.workspaces === 0 && !u.isSystemAdministrator
const selectable = computed(() => users.value.filter(deletable))

async function load() {
  loading.value = true
  try {
    const page = await opsApi.users(search.value.trim(), onlyWithout.value)
    users.value = page.items
    total.value = page.total
    without.value = page.withoutWorkspace
    selected.value = selected.value.filter((s) => page.items.some((u) => u.id === s.id))
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}

let timer: number | undefined
watch(search, () => {
  window.clearTimeout(timer)
  timer = window.setTimeout(load, 300)
})
watch(onlyWithout, load)
onMounted(load)

async function run() {
  const c = confirm.value
  if (!c) return
  busy.value = true
  try {
    const result = await opsApi.deleteUsers(c.all ? { allWithoutWorkspace: true } : { ids: selected.value.map((u) => u.id) })
    toast.add({ severity: 'success', summary: t('systemUsers.deleted', { n: result.deleted }) + (result.skipped ? ' ' + t('systemUsers.skipped', { n: result.skipped }) : ''), life: 6000 })
    confirm.value = null
    selected.value = []
    await load()
  } catch (e) {
    fail(e)
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="page" data-testid="system-users">
    <header class="page-head">
      <h1>{{ t('systemUsers.title') }}</h1>
      <div class="actions">
        <Button
          severity="danger"
          outlined
          icon="pi pi-trash"
          :label="t('systemUsers.deleteSelected', { n: selected.length })"
          :disabled="!selected.length"
          data-testid="delete-selected"
          @click="confirm = { all: false, count: selected.length }"
        />
        <Button
          severity="danger"
          icon="pi pi-trash"
          :label="t('systemUsers.deleteAll', { n: without })"
          :disabled="!without"
          data-testid="delete-all"
          @click="confirm = { all: true, count: without }"
        />
      </div>
    </header>
    <p class="muted">{{ t('systemUsers.intro') }}</p>

    <div class="filters">
      <InputText v-model="search" :placeholder="t('systemUsers.search')" :aria-label="t('systemUsers.search')" data-testid="user-search" />
      <div class="row">
        <Checkbox v-model="onlyWithout" input-id="only-without" binary data-testid="only-without" />
        <label for="only-without">{{ t('systemUsers.onlyWithout', { n: without }) }}</label>
      </div>
      <span class="muted">{{ t('systemUsers.count', { n: users.length, total }) }}</span>
    </div>

    <DataTable v-model:selection="selected" :value="users" :loading="loading" data-key="id" size="small" striped-rows :is-row-selectable="(e: { data: SystemUser }) => deletable(e.data)" data-testid="users-table">
      <template #empty>{{ t('common.empty') }}</template>
      <Column selection-mode="multiple" header-style="inline-size: 3rem" />
      <Column field="displayName" :header="t('common.displayName')" />
      <Column :header="t('common.email')"><template #body="{ data }"><span dir="ltr">{{ data.email ?? '—' }}</span></template></Column>
      <Column :header="t('systemUsers.workspaces')">
        <template #body="{ data }">
          <Tag v-if="data.isSystemAdministrator" severity="info" :value="t('systemUsers.systemAdmin')" />
          <Tag v-else :severity="data.workspaces === 0 ? 'danger' : 'secondary'" :value="String(data.workspaces)" :data-testid="`ws-${data.email}`" />
        </template>
      </Column>
      <Column :header="t('tenants.created')"><template #body="{ data }">{{ formatDate(data.createdAt, localeStore.locale) }}</template></Column>
      <Column :header="t('systemUsers.lastLogin')"><template #body="{ data }">{{ data.lastLoginAt ? formatDate(data.lastLoginAt, localeStore.locale) : '—' }}</template></Column>
    </DataTable>
    <p v-if="selectable.length === 0 && !loading && users.length" class="muted">{{ t('systemUsers.noneDeletable') }}</p>

    <Dialog :visible="!!confirm" modal :header="t('systemUsers.confirmTitle')" :style="{ inlineSize: 'min(30rem, 95vw)' }" @update:visible="(v) => !v && (confirm = null)">
      <div v-if="confirm" class="form" data-testid="users-confirm">
        <p>{{ confirm.all ? t('systemUsers.confirmAll', { n: confirm.count }) : t('systemUsers.confirmSome', { n: confirm.count }) }}</p>
        <Button severity="danger" icon="pi pi-trash" :label="t('common.delete')" :loading="busy" data-testid="users-confirm-ok" @click="run" />
      </div>
    </Dialog>
  </section>
</template>

<style scoped>
.page { max-inline-size: 72rem; margin-inline: auto; display: grid; gap: var(--raavi-space-3); }
.page-head { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; }
h1 { margin: 0; font-size: 1.4rem; }
.actions { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.filters { display: flex; gap: var(--raavi-space-3); align-items: center; flex-wrap: wrap; }
.row { display: flex; align-items: center; gap: var(--raavi-space-1); }
.muted { color: var(--raavi-text-muted); margin: 0; }
.form { display: grid; gap: var(--raavi-space-3); }
</style>
