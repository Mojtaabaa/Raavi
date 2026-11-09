<script setup lang="ts">
import UserAvatar from '@/app/UserAvatar.vue'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import { identityApi, type Group, type Member } from '@/core/api/identity'
import { errorMessage } from '@/core/api/errors'

const { t, te } = useI18n()
const confirm = useConfirm()
const toast = useToast()

const groups = ref<Group[]>([])
const members = ref<Member[]>([])
const loading = ref(true)
const createOpen = ref(false)
const name = ref('')
const description = ref('')
const selected = ref<Group | null>(null)
const selectedMemberIds = ref<string[]>([])
const toAdd = ref<string | null>(null)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })

async function load() {
  loading.value = true
  try {
    ;[groups.value, members.value] = await Promise.all([identityApi.groups(), identityApi.members()])
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}

async function create() {
  try {
    await identityApi.createGroup(name.value, description.value || null)
    createOpen.value = false
    name.value = ''
    description.value = ''
    await load()
  } catch (e) {
    fail(e)
  }
}

function remove(g: Group) {
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('groups.confirmDelete'),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await identityApi.deleteGroup(g.id)
      } catch (e) {
        fail(e)
      }
      await load()
    },
  })
}

async function open(g: Group) {
  selected.value = g
  try {
    selectedMemberIds.value = await identityApi.groupMembers(g.id)
  } catch (e) {
    fail(e)
  }
}

async function changeMembership(userId: string, add: boolean) {
  if (!selected.value) return
  try {
    if (add) await identityApi.addGroupMember(selected.value.id, userId)
    else await identityApi.removeGroupMember(selected.value.id, userId)
    selectedMemberIds.value = await identityApi.groupMembers(selected.value.id)
    toAdd.value = null
    await load()
  } catch (e) {
    fail(e)
  }
}

const inGroup = computed(() => members.value.filter((m) => selectedMemberIds.value.includes(m.userId)))
const candidates = computed(() => members.value.filter((m) => !selectedMemberIds.value.includes(m.userId)))

onMounted(load)
</script>

<template>
  <section class="page">
    <header class="page-head">
      <h1>{{ t('groups.title') }}</h1>
      <Button icon="pi pi-plus" :label="t('groups.create')" @click="createOpen = true" />
    </header>

    <DataTable :value="groups" :loading="loading" data-key="id" size="small" striped-rows>
      <template #empty>{{ t('common.empty') }}</template>
      <Column field="name" :header="t('groups.name')" />
      <Column field="description" :header="t('groups.description')" />
      <Column field="memberCount" :header="t('groups.members')" />
      <Column :header="t('common.actions')">
        <template #body="{ data }">
          <Button text size="small" :label="t('groups.members')" @click="open(data)" />
          <Button text size="small" severity="danger" :label="t('common.delete')" @click="remove(data)" />
        </template>
      </Column>
    </DataTable>

    <Dialog v-model:visible="createOpen" modal :header="t('groups.create')" :style="{ inlineSize: 'min(28rem, 95vw)' }">
      <form class="form" @submit.prevent="create">
        <label for="g-name">{{ t('groups.name') }}</label>
        <InputText id="g-name" v-model="name" required />
        <label for="g-desc">{{ t('groups.description') }}</label>
        <InputText id="g-desc" v-model="description" />
        <Button type="submit" :label="t('common.create')" />
      </form>
    </Dialog>

    <Dialog :visible="!!selected" modal :header="selected?.name" :style="{ inlineSize: 'min(32rem, 95vw)' }" @update:visible="(v) => !v && (selected = null)">
      <ul class="list">
        <li v-for="m in inGroup" :key="m.userId">
          <span class="who"><UserAvatar :user-id="m.userId" :name="m.displayName" :size="24" /> {{ m.displayName }}</span>
          <Button text size="small" severity="danger" icon="pi pi-times" :aria-label="t('common.delete')" @click="changeMembership(m.userId, false)" />
        </li>
      </ul>
      <div class="row">
        <Select v-model="toAdd" :options="candidates" option-label="displayName" option-value="userId" :placeholder="t('groups.addMember')" class="grow" filter />
        <Button icon="pi pi-plus" :disabled="!toAdd" :aria-label="t('groups.addMember')" @click="toAdd && changeMembership(toAdd, true)" />
      </div>
    </Dialog>
  </section>
</template>

<style scoped>
.page { max-inline-size: 72rem; margin-inline: auto; display: grid; gap: var(--raavi-space-4); }
.page-head { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-3); }
h1 { margin: 0; font-size: 1.4rem; }
.form { display: grid; gap: var(--raavi-space-2); }
.list { list-style: none; padding: 0; margin: 0 0 var(--raavi-space-3); display: grid; gap: var(--raavi-space-1); }
.list li { display: flex; align-items: center; justify-content: space-between; }
.row { display: flex; gap: var(--raavi-space-2); }
.grow { flex: 1; min-inline-size: 0; }
</style>
