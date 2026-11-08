<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import { approvalsApi, type Approval, type ApprovalMode } from '@/core/api/approvals'
import { errorMessage } from '@/core/api/errors'
import PrincipalPicker from '@/modules/wiki/components/PrincipalPicker.vue'
import ApprovalCard from './ApprovalCard.vue'

/** Approvals of one page (Phase 10b): the history, the decision buttons for approvers and the "request approval" dialog for editors. */
const props = defineProps<{ contentId: string; canRequest: boolean }>()
const { t, te } = useI18n()
const toast = useToast()

const approvals = ref<Approval[]>([])
const loading = ref(true)
const open = ref(false)
const saving = ref(false)
const note = ref('')
const mode = ref<ApprovalMode>('All')
const due = ref('')
const approvers = ref<{ id: string; name: string }[]>([])

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const hasPending = computed(() => approvals.value.some((a) => a.status === 'Pending'))

async function load() {
  loading.value = true
  try {
    approvals.value = await approvalsApi.forPage(props.contentId)
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}
onMounted(load)
watch(() => props.contentId, load)

function openDialog() {
  note.value = ''
  mode.value = 'All'
  due.value = ''
  approvers.value = []
  open.value = true
}

function pick(s: { subjectType: string; subjectId: string | null; name: string }) {
  if (s.subjectType === 'User' && s.subjectId && !approvers.value.some((a) => a.id === s.subjectId) && approvers.value.length < 10) approvers.value.push({ id: s.subjectId, name: s.name })
}

async function submit() {
  if (approvers.value.length === 0) return
  saving.value = true
  try {
    await approvalsApi.request(props.contentId, {
      note: note.value.trim(),
      mode: mode.value,
      approverIds: approvers.value.map((a) => a.id),
      dueAt: due.value ? new Date(due.value).toISOString() : null,
    })
    open.value = false
    await load()
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}

function changed(updated: Approval) {
  approvals.value = approvals.value.map((a) => (a.id === updated.id ? updated : a))
}
</script>

<template>
  <section v-if="canRequest || approvals.length" class="approvals" :aria-label="t('approvals.title')" data-testid="page-approvals">
    <header class="head">
      <h2>{{ t('approvals.title') }}</h2>
      <Button v-if="canRequest" size="small" severity="secondary" outlined icon="pi pi-verified" :label="t('approvals.request')" :disabled="hasPending" data-testid="request-approval" @click="openDialog" />
    </header>
    <p v-if="!loading && approvals.length === 0" class="muted" data-testid="no-approvals">{{ t('approvals.none') }}</p>
    <ApprovalCard v-for="a in approvals" :key="a.id" :approval="a" @changed="changed" />

    <Dialog v-model:visible="open" modal :header="t('approvals.request')" :style="{ inlineSize: 'min(34rem, 95vw)' }">
      <form class="form" @submit.prevent="submit">
        <label for="appr-who">{{ t('approvals.approvers') }}</label>
        <PrincipalPicker input-id="appr-who" users-only @pick="pick" />
        <ul class="chosen" data-testid="chosen-approvers">
          <li v-for="a in approvers" :key="a.id">
            <span>{{ a.name }}</span>
            <Button type="button" text rounded size="small" icon="pi pi-times" :aria-label="t('common.delete')" @click="approvers = approvers.filter((x) => x.id !== a.id)" />
          </li>
        </ul>
        <label for="appr-mode">{{ t('approvals.rule') }}</label>
        <select id="appr-mode" v-model="mode" data-testid="approval-mode">
          <option value="All">{{ t('approvals.modes.All') }}</option>
          <option value="Any">{{ t('approvals.modes.Any') }}</option>
        </select>
        <label for="appr-due">{{ t('approvals.dueOptional') }}</label>
        <input id="appr-due" v-model="due" type="datetime-local" data-testid="approval-due-input" />
        <label for="appr-note">{{ t('approvals.noteOptional') }}</label>
        <textarea id="appr-note" v-model="note" rows="3" maxlength="500" dir="auto" data-testid="approval-note" />
        <Button type="submit" :label="t('approvals.send')" :loading="saving" :disabled="approvers.length === 0" data-testid="send-approval" />
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.approvals { display: grid; gap: var(--raavi-space-2); border-block-start: 1px solid var(--raavi-border); padding-block-start: var(--raavi-space-3); }
.head { display: flex; justify-content: space-between; align-items: center; gap: var(--raavi-space-2); flex-wrap: wrap; }
h2 { margin: 0; font-size: 1.1rem; }
.form { display: grid; gap: var(--raavi-space-2); }
.form select, .form input, .form textarea { font: inherit; padding: 0.4rem; }
.chosen { list-style: none; margin: 0; padding: 0; display: flex; flex-wrap: wrap; gap: 0.35rem; }
.chosen li { display: inline-flex; align-items: center; padding-inline-start: 0.6rem; border: 1px solid var(--raavi-border); border-radius: 999px; }
.muted { color: var(--raavi-text-muted); margin: 0; }
</style>
