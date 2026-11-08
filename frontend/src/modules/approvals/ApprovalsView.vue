<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import { approvalsApi, type Approval } from '@/core/api/approvals'
import { errorMessage } from '@/core/api/errors'
import ApprovalCard from './ApprovalCard.vue'

/** My approvals (Phase 10b): what waits for my decision, and what I asked others to approve. */
const { t, te } = useI18n()
const toast = useToast()

const scope = ref<'inbox' | 'requested'>('inbox')
const items = ref<Approval[]>([])
const loading = ref(true)

async function load() {
  loading.value = true
  try {
    items.value = await approvalsApi.list(scope.value)
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
  } finally {
    loading.value = false
  }
}
onMounted(load)
watch(scope, load)

// A decided request leaves the inbox; in "requested" it stays with its new state.
function changed(updated: Approval) {
  items.value = scope.value === 'inbox' && !updated.canDecide ? items.value.filter((a) => a.id !== updated.id) : items.value.map((a) => (a.id === updated.id ? updated : a))
}
</script>

<template>
  <section class="approvals" data-testid="approvals">
    <h1>{{ t('approvals.myTitle') }}</h1>
    <div class="tabs" role="tablist" :aria-label="t('approvals.myTitle')">
      <button type="button" role="tab" :aria-selected="scope === 'inbox'" :class="{ on: scope === 'inbox' }" data-testid="tab-inbox" @click="scope = 'inbox'">{{ t('approvals.inbox') }}</button>
      <button type="button" role="tab" :aria-selected="scope === 'requested'" :class="{ on: scope === 'requested' }" data-testid="tab-requested" @click="scope = 'requested'">{{ t('approvals.requested') }}</button>
    </div>
    <p v-if="!loading && items.length === 0" class="muted" data-testid="no-items">{{ scope === 'inbox' ? t('approvals.inboxEmpty') : t('approvals.requestedEmpty') }}</p>
    <ApprovalCard v-for="a in items" :key="a.id" :approval="a" show-page @changed="changed" />
  </section>
</template>

<style scoped>
.approvals { display: grid; gap: var(--raavi-space-3); max-inline-size: 52rem; }
h1 { margin: 0; font-size: 1.5rem; }
.tabs { display: flex; gap: var(--raavi-space-1); border-block-end: 1px solid var(--raavi-border); }
.tabs button { all: unset; cursor: pointer; padding: 0.5rem 1rem; border-block-end: 3px solid transparent; }
.tabs button:focus-visible { outline: 2px solid var(--raavi-primary, #2563eb); }
.tabs button.on { border-block-end-color: var(--raavi-primary, #2563eb); font-weight: 700; }
.muted { color: var(--raavi-text-muted); margin: 0; }
</style>
