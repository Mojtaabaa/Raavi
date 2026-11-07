<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import ToggleSwitch from 'primevue/toggleswitch'
import InputText from 'primevue/inputtext'
import { wikiApi, type PublishRequest } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import { useSpaceContext } from '../spaceContext'

/** Approval foundation (ADR-019): require approval in this space; approve or reject pending publication requests. */
const { t, te } = useI18n()
const toast = useToast()
const locale = useLocaleStore()
const ctx = useSpaceContext()
const requests = ref<PublishRequest[]>([])
const comments = ref<Record<string, string>>({})
const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })

async function load() {
  if (!ctx.space.value?.rights.canView) return
  requests.value = await wikiApi.publishRequests(ctx.space.value.key).catch(() => [])
}
onMounted(load)

async function toggle(value: boolean) {
  const space = ctx.space.value
  if (!space) return
  try {
    await wikiApi.setPublishing(space.key, ctx.etag.value, value)
    await ctx.reloadSpace()
  } catch (e) {
    fail(e)
  }
}

async function decide(r: PublishRequest, approve: boolean) {
  try {
    await wikiApi.decide(r.id, approve, comments.value[r.id] || null)
    toast.add({ severity: 'success', summary: approve ? t('wiki.approvals.approved') : t('wiki.approvals.rejected'), life: 3000 })
    await load()
  } catch (e) {
    fail(e)
  }
}
</script>

<template>
  <div v-if="ctx.space.value" class="publishing">
    <div class="row">
      <ToggleSwitch input-id="require-approval" :model-value="ctx.space.value.requirePublishApproval" :disabled="ctx.space.value.status !== 'Active'" @update:model-value="toggle" />
      <label for="require-approval">{{ t('wiki.approvals.require') }}</label>
    </div>
    <p class="muted">{{ t('wiki.approvals.help') }}</p>

    <h3>{{ t('wiki.approvals.pending') }}</h3>
    <p v-if="requests.length === 0" class="muted">{{ t('wiki.approvals.none') }}</p>
    <ul class="requests">
      <li v-for="r in requests" :key="r.id">
        <div>
          <RouterLink :to="{ name: 'page-history', params: { key: ctx.space.value.key, id: r.contentId } }" dir="auto">{{ r.title }}</RouterLink>
          <small class="muted"> · {{ r.requestedByName ?? t('wiki.unknownUser') }} · {{ formatDate(r.requestedAt, locale.locale, { dateStyle: 'medium', timeStyle: 'short' }) }}</small>
          <div v-if="r.changeSummary" class="summary" dir="auto">{{ r.changeSummary }}</div>
        </div>
        <div class="actions">
          <InputText v-model="comments[r.id]" :placeholder="t('wiki.approvals.comment')" :aria-label="t('wiki.approvals.comment')" size="small" />
          <Button size="small" :label="t('wiki.approvals.approve')" data-testid="approve" @click="decide(r, true)" />
          <Button size="small" severity="danger" outlined :label="t('wiki.approvals.reject')" @click="decide(r, false)" />
        </div>
      </li>
    </ul>
  </div>
</template>

<style scoped>
.publishing { display: grid; gap: var(--raavi-space-2); }
.row { display: flex; align-items: center; gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); margin: 0; }
h3 { margin: var(--raavi-space-3) 0 0; font-size: 1rem; }
.requests { list-style: none; padding: 0; margin: 0; display: grid; gap: var(--raavi-space-3); }
.requests li { display: grid; gap: var(--raavi-space-1); border-block-end: 1px solid var(--raavi-border); padding-block-end: var(--raavi-space-2); }
.actions { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.summary { font-style: italic; }
</style>
