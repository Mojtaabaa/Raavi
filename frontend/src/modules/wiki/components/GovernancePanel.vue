<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import Select from 'primevue/select'
import DatePicker from 'primevue/datepicker'
import Tag from 'primevue/tag'
import Message from 'primevue/message'
import { wikiApi, type Page } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import PrincipalPicker from './PrincipalPicker.vue'
import UserAvatar from '@/app/UserAvatar.vue'

const props = defineProps<{ page: Page; canEdit: boolean }>()
const emit = defineEmits<{ updated: [page: Page] }>()
const { t, te } = useI18n()
const toast = useToast()
const locale = useLocaleStore()

const open = ref(false)
const saving = ref(false)
const ownerId = ref<string | null>(null)
const ownerName = ref<string | null>(null)
const reviewOwnerId = ref<string | null>(null)
const reviewOwnerName = ref<string | null>(null)
const reviewAt = ref<Date | null>(null)
const classification = ref('Internal')
const lifecycleStatus = ref('Current')
const markReviewed = ref(false)

const classifications = ['Public', 'Internal', 'Confidential', 'Restricted'] as const
const lifecycles = ['Draft', 'Current', 'InReview', 'Approved', 'Archived', 'Expired'] as const
const classOptions = computed(() => classifications.map((c) => ({ value: c, label: t(`wiki.governance.class.${c}`) })))
const lifeOptions = computed(() => lifecycles.map((c) => ({ value: c, label: t(`wiki.governance.life.${c}`) })))
function labelOf(prefix: string, value: string) {
  const key = `${prefix}.${value}`
  return te(key) ? t(key) : value
}

const g = computed(() => props.page.governance)

watch(
  () => props.page.governance,
  (gov) => {
    if (!gov) return
    ownerId.value = gov.ownerUserId
    ownerName.value = gov.ownerName
    reviewOwnerId.value = gov.reviewOwnerUserId
    reviewOwnerName.value = gov.reviewOwnerName
    reviewAt.value = gov.reviewAt ? new Date(gov.reviewAt) : null
    classification.value = gov.classification
    lifecycleStatus.value = gov.lifecycleStatus
    markReviewed.value = false
  },
  { immediate: true },
)

function openEdit() {
  open.value = true
}

async function save() {
  if (!ownerId.value) return
  saving.value = true
  try {
    const body = {
      ownerUserId: ownerId.value,
      reviewOwnerUserId: reviewOwnerId.value,
      clearReviewOwner: !reviewOwnerId.value,
      reviewAt: reviewAt.value ? reviewAt.value.toISOString() : null,
      clearReviewAt: !reviewAt.value,
      classification: classification.value,
      lifecycleStatus: lifecycleStatus.value,
      markReviewed: markReviewed.value,
    }
    const updated = await wikiApi.updateGovernance(props.page.id, body)
    emit('updated', updated)
    open.value = false
    toast.add({ severity: 'success', summary: t('wiki.governance.saved'), life: 2500 })
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
  } finally {
    saving.value = false
  }
}

function classSeverity(c: string | undefined) {
  if (c === 'Restricted' || c === 'Confidential') return 'danger'
  if (c === 'Internal') return 'warn'
  return 'success'
}
</script>

<template>
  <section v-if="g" class="gov" :aria-label="t('wiki.governance.title')" data-testid="governance-panel">
    <header class="gov-head">
      <h2>{{ t('wiki.governance.title') }}</h2>
      <Button v-if="canEdit" size="small" text icon="pi pi-pencil" :label="t('wiki.governance.edit')" data-testid="governance-edit" @click="openEdit" />
    </header>
    <Message v-if="g.reviewDue" severity="warn" :closable="false" data-testid="governance-review-due">{{ t('wiki.governance.reviewDueNotice') }}</Message>
    <dl class="grid">
      <div>
        <dt>{{ t('wiki.governance.owner') }}</dt>
        <dd class="who">
          <UserAvatar :user-id="g.ownerUserId" :name="g.ownerName" :size="22" />
          <span>{{ g.ownerName ?? t('wiki.unknownUser') }}</span>
        </dd>
      </div>
      <div>
        <dt>{{ t('wiki.governance.reviewOwner') }}</dt>
        <dd class="who">
          <template v-if="g.reviewOwnerUserId">
            <UserAvatar :user-id="g.reviewOwnerUserId" :name="g.reviewOwnerName" :size="22" />
            <span>{{ g.reviewOwnerName ?? t('wiki.unknownUser') }}</span>
          </template>
          <span v-else class="muted">{{ t('wiki.governance.none') }}</span>
        </dd>
      </div>
      <div>
        <dt>{{ t('wiki.governance.classification') }}</dt>
        <dd><Tag :severity="classSeverity(g.classification)" :value="labelOf('wiki.governance.class', g.classification)" /></dd>
      </div>
      <div>
        <dt>{{ t('wiki.governance.lifecycle') }}</dt>
        <dd><Tag severity="secondary" :value="labelOf('wiki.governance.life', g.lifecycleStatus)" /></dd>
      </div>
      <div>
        <dt>{{ t('wiki.governance.reviewAt') }}</dt>
        <dd>{{ g.reviewAt ? formatDate(g.reviewAt, locale.locale, { dateStyle: 'medium' }) : t('wiki.governance.none') }}</dd>
      </div>
      <div>
        <dt>{{ t('wiki.governance.lastReviewedAt') }}</dt>
        <dd>{{ g.lastReviewedAt ? formatDate(g.lastReviewedAt, locale.locale, { dateStyle: 'medium' }) : t('wiki.governance.none') }}</dd>
      </div>
    </dl>
    <p class="hint muted">{{ t('wiki.governance.ownerHint') }}</p>

    <Dialog v-model:visible="open" modal :header="t('wiki.governance.edit')" :style="{ inlineSize: 'min(32rem, 95vw)' }" data-testid="governance-dialog">
      <div class="form">
        <label>{{ t('wiki.governance.owner') }}</label>
        <div class="who">
          <UserAvatar v-if="ownerId" :user-id="ownerId" :name="ownerName" :size="22" />
          <span>{{ ownerName ?? t('wiki.unknownUser') }}</span>
        </div>
        <PrincipalPicker input-id="gov-owner" users-only @pick="(s) => { if (s.subjectId) { ownerId = s.subjectId; ownerName = s.name } }" />

        <label>{{ t('wiki.governance.reviewOwner') }}</label>
        <div class="row">
          <span class="muted">{{ reviewOwnerName ?? t('wiki.governance.none') }}</span>
          <Button size="small" text :label="t('wiki.governance.clear')" :disabled="!reviewOwnerId" @click="reviewOwnerId = null; reviewOwnerName = null" />
        </div>
        <PrincipalPicker input-id="gov-review-owner" users-only @pick="(s) => { if (s.subjectId) { reviewOwnerId = s.subjectId; reviewOwnerName = s.name } }" />

        <label for="gov-class">{{ t('wiki.governance.classification') }}</label>
        <Select id="gov-class" v-model="classification" :options="classOptions" option-label="label" option-value="value" data-testid="gov-class" />

        <label for="gov-life">{{ t('wiki.governance.lifecycle') }}</label>
        <Select id="gov-life" v-model="lifecycleStatus" :options="lifeOptions" option-label="label" option-value="value" data-testid="gov-life" />

        <label for="gov-review-at">{{ t('wiki.governance.reviewAt') }}</label>
        <div class="row">
          <DatePicker id="gov-review-at" v-model="reviewAt" show-icon date-format="yy-mm-dd" data-testid="gov-review-at" />
          <Button size="small" text :label="t('wiki.governance.clear')" :disabled="!reviewAt" @click="reviewAt = null" />
        </div>

        <label class="check">
          <input v-model="markReviewed" type="checkbox" data-testid="gov-mark-reviewed" />
          {{ t('wiki.governance.markReviewed') }}
        </label>
        <p class="hint muted">{{ t('wiki.governance.classificationHint') }}</p>
      </div>
      <template #footer>
        <Button :label="t('common.cancel')" text @click="open = false" />
        <Button :label="t('common.save')" :loading="saving" data-testid="governance-save" @click="save" />
      </template>
    </Dialog>
  </section>
</template>

<style scoped>
.gov { display: grid; gap: var(--raavi-space-2); border-block-start: 1px solid var(--raavi-border); padding-block-start: var(--raavi-space-3); }
.gov-head { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-2); }
.gov-head h2 { margin: 0; font-size: 1rem; }
.grid { display: grid; grid-template-columns: repeat(auto-fit, minmax(12rem, 1fr)); gap: var(--raavi-space-3); margin: 0; }
dt { font-size: 0.8rem; color: var(--raavi-text-muted); margin-block-end: 0.25rem; }
dd { margin: 0; }
.who { display: inline-flex; align-items: center; gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); }
.hint { margin: 0; font-size: 0.85rem; }
.form { display: grid; gap: var(--raavi-space-2); }
.row { display: flex; align-items: center; gap: var(--raavi-space-2); flex-wrap: wrap; }
.check { display: flex; align-items: center; gap: var(--raavi-space-2); }
</style>
