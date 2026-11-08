<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import { approvalsApi, type Approval } from '@/core/api/approvals'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'

/** One approval request: status, who decided what, and — when allowed — the approve / reject / cancel actions. */
const props = defineProps<{ approval: Approval; showPage?: boolean }>()
const emit = defineEmits<{ changed: [approval: Approval] }>()
const { t, te } = useI18n()
const toast = useToast()
const locale = useLocaleStore()

const rejecting = ref(false)
const comment = ref('')
const busy = ref(false)
const when = (iso: string) => formatDate(iso, locale.locale, { dateStyle: 'medium', timeStyle: 'short' })

async function run(action: () => Promise<Approval>) {
  busy.value = true
  try {
    emit('changed', await action())
    rejecting.value = false
    comment.value = ''
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
  } finally {
    busy.value = false
  }
}

const approve = () => run(() => approvalsApi.decide(props.approval.id, 'Approved', comment.value))
const reject = () => run(() => approvalsApi.decide(props.approval.id, 'Rejected', comment.value))
const cancel = () => run(() => approvalsApi.cancel(props.approval.id))
</script>

<template>
  <article class="card" :class="approval.status.toLowerCase()" data-testid="approval">
    <header class="top">
      <span class="status" data-testid="approval-status">{{ t(`approvals.status.${approval.status}`) }}</span>
      <RouterLink v-if="showPage" :to="{ name: 'page', params: { key: approval.spaceKey, id: approval.contentId } }" class="page" dir="auto">{{ approval.contentTitle }}</RouterLink>
      <span class="muted">{{ t('approvals.requestedBy', { name: approval.requestedByName ?? t('wiki.unknownUser'), time: when(approval.requestedAt) }) }}</span>
    </header>
    <p v-if="approval.note" class="note" dir="auto">{{ approval.note }}</p>
    <p class="muted meta">
      {{ t(`approvals.modes.${approval.mode}`) }}
      <template v-if="approval.dueAt"> · <span :class="{ overdue: approval.overdue }" data-testid="approval-due">{{ t('approvals.due', { time: when(approval.dueAt) }) }}<template v-if="approval.overdue"> — {{ t('approvals.overdue') }}</template></span></template>
      <template v-if="approval.pageChanged"> · <span data-testid="page-changed">{{ t('approvals.pageChanged') }}</span></template>
    </p>
    <ul class="approvers">
      <li v-for="a in approval.approvers" :key="a.userId" data-testid="approver">
        <i :class="a.decision === 'Approved' ? 'pi pi-check-circle ok' : a.decision === 'Rejected' ? 'pi pi-times-circle no' : 'pi pi-clock'" aria-hidden="true" />
        <span>{{ a.name ?? t('wiki.unknownUser') }}</span>
        <span class="muted">{{ t(`approvals.decision.${a.decision}`) }}</span>
        <q v-if="a.comment" dir="auto">{{ a.comment }}</q>
      </li>
    </ul>

    <div v-if="approval.canDecide" class="decide">
      <template v-if="!rejecting">
        <Button size="small" icon="pi pi-check" :label="t('approvals.approve')" :loading="busy" data-testid="approve" @click="approve" />
        <Button size="small" severity="danger" outlined icon="pi pi-times" :label="t('approvals.reject')" data-testid="start-reject" @click="rejecting = true" />
      </template>
      <form v-else class="reject" @submit.prevent="reject">
        <label :for="`c-${approval.id}`">{{ t('approvals.rejectReason') }}</label>
        <textarea :id="`c-${approval.id}`" v-model="comment" rows="3" maxlength="1000" dir="auto" data-testid="reject-comment" />
        <div class="buttons">
          <Button type="submit" size="small" severity="danger" :label="t('approvals.confirmReject')" :loading="busy" :disabled="!comment.trim()" data-testid="confirm-reject" />
          <Button type="button" size="small" text :label="t('common.cancel')" @click="rejecting = false" />
        </div>
      </form>
    </div>
    <Button v-if="approval.canCancel" size="small" text severity="secondary" icon="pi pi-ban" :label="t('approvals.cancel')" :loading="busy" data-testid="cancel-approval" @click="cancel" />
  </article>
</template>

<style scoped>
.card { display: grid; gap: var(--raavi-space-2); padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.top { display: flex; flex-wrap: wrap; gap: var(--raavi-space-2); align-items: baseline; }
.status { font-weight: 700; padding: 0.1rem 0.6rem; border-radius: 999px; background: var(--raavi-surface-muted, #eee); }
.pending .status { background: #fef3c7; color: #92400e; }
.approved .status { background: #dcfce7; color: #166534; }
.rejected .status { background: #fee2e2; color: #991b1b; }
.page { font-weight: 600; overflow-wrap: anywhere; }
.note { margin: 0; overflow-wrap: anywhere; }
.meta { margin: 0; font-size: 0.9rem; }
.overdue { color: var(--raavi-danger, #b91c1c); font-weight: 600; }
.approvers { list-style: none; margin: 0; padding: 0; display: grid; gap: 0.25rem; }
.approvers li { display: flex; gap: var(--raavi-space-2); align-items: baseline; flex-wrap: wrap; }
.approvers q { color: var(--raavi-text-muted); overflow-wrap: anywhere; }
.ok { color: #16a34a; }
.no { color: #dc2626; }
.decide, .buttons { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.reject { display: grid; gap: var(--raavi-space-1); inline-size: 100%; }
.reject textarea { font: inherit; padding: 0.4rem; }
.muted { color: var(--raavi-text-muted); }
</style>
