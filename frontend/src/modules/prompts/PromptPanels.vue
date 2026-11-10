<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Tabs from 'primevue/tabs'
import TabList from 'primevue/tablist'
import Tab from 'primevue/tab'
import TabPanels from 'primevue/tabpanels'
import TabPanel from 'primevue/tabpanel'
import Button from 'primevue/button'
import Select from 'primevue/select'
import Textarea from 'primevue/textarea'
import { promptRunApi, type PromptStats, type PromptSummary, type PromptVersionInfo, type Ratings, type VersionDiff } from '@/core/api/prompts'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'

/** Ratings and feedback, usage statistics and version history of one prompt (ADR-052). */
const props = defineProps<{ prompt: PromptSummary; canStats: boolean }>()
const emit = defineEmits<{ restored: []; changed: [] }>()
const { t, te } = useI18n()
const toast = useToast()
const locale = useLocaleStore()

const ratings = ref<Ratings | null>(null)
const stats = ref<PromptStats | null>(null)
const versions = ref<PromptVersionInfo[]>([])
const diff = ref<VersionDiff | null>(null)
const stars = ref(0)
const hover = ref(0)
const feedback = ref<string | null>(null)
const comment = ref('')
const isPublic = computed(() => props.prompt.visibility === 'Tenant')
const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
const feedbackOptions = computed(() => ['useful', 'accurate', 'needs-improvement', 'outdated'].map((k) => ({ value: k, label: t(`prompts.feedback.${k}`) })))

async function load() {
  try {
    ratings.value = await promptRunApi.ratings(props.prompt.id)
    const mine = ratings.value.entries.find((e) => e.mine)
    stars.value = ratings.value.mine ?? 0
    feedback.value = mine?.feedback ?? null
    comment.value = mine?.comment ?? ''
    versions.value = await promptRunApi.versions(props.prompt.id)
    stats.value = props.canStats ? await promptRunApi.stats(props.prompt.id) : null
  } catch (e) {
    fail(e)
  }
}

async function rate(n: number) {
  try {
    ratings.value = await promptRunApi.rate(props.prompt.id, n, feedback.value, comment.value || null)
    stars.value = n
    toast.add({ severity: 'success', summary: t('prompts.rating.thanks'), life: 2500 })
    emit('changed')
  } catch (e) {
    fail(e)
  }
}

async function unrate() {
  try {
    ratings.value = await promptRunApi.unrate(props.prompt.id)
    stars.value = 0
    feedback.value = null
    comment.value = ''
    emit('changed')
  } catch (e) {
    fail(e)
  }
}

async function compare(v: PromptVersionInfo) {
  const index = versions.value.findIndex((x) => x.id === v.id)
  const older = versions.value[index + 1]
  if (!older) {
    diff.value = null
    toast.add({ severity: 'info', summary: t('prompts.versions.first'), life: 3000 })
    return
  }
  try {
    diff.value = await promptRunApi.diff(props.prompt.id, older.id, v.id)
  } catch (e) {
    fail(e)
  }
}

async function restore(v: PromptVersionInfo) {
  try {
    await promptRunApi.restore(props.prompt.id, v.id, props.prompt.hasDraft)
    toast.add({ severity: 'success', summary: t('prompts.versions.restored', { n: v.number }), life: 4000 })
    emit('restored')
  } catch (e) {
    fail(e)
  }
}

const pct = (n: number) => (ratings.value && ratings.value.count ? Math.round((100 * n) / ratings.value.count) : 0)
const usageOf = (id: string) => stats.value?.versions.find((x) => x.versionId === id)
watch(() => props.prompt.id, load)
onMounted(load)
</script>

<template>
  <Tabs value="ratings" class="panels" data-testid="prompt-panels">
    <TabList>
      <Tab value="ratings">{{ t('prompts.rating.title') }}</Tab>
      <Tab v-if="canStats" value="stats">{{ t('prompts.stats.title') }}</Tab>
      <Tab value="versions">{{ t('prompts.versions.title') }}</Tab>
    </TabList>
    <TabPanels>
      <TabPanel value="ratings">
        <div v-if="ratings" class="rating" data-testid="rating-panel">
          <p class="avg" data-testid="rating-average">
            <strong>{{ ratings.average ?? '—' }}</strong> <span class="muted">/ 5 · {{ t('prompts.rating.count', { n: ratings.count }) }}</span>
          </p>
          <ul class="dist">
            <li v-for="n in [5, 4, 3, 2, 1]" :key="n"><span>{{ n }}★</span><span class="bar"><span :style="{ inlineSize: pct(ratings.distribution[n - 1]!) + '%' }" /></span><span class="muted">{{ ratings.distribution[n - 1] }}</span></li>
          </ul>
          <template v-if="isPublic && !prompt.isOwner">
            <p class="muted">{{ t('prompts.rating.yours') }}</p>
            <div class="stars" role="radiogroup" :aria-label="t('prompts.rating.yours')">
              <button v-for="n in 5" :key="n" type="button" role="radio" :aria-checked="stars === n" :aria-label="t('prompts.rating.stars', { n })" :class="['star', { on: n <= (hover || stars) }]" :data-testid="`star-${n}`" @mouseenter="hover = n" @mouseleave="hover = 0" @click="rate(n)">★</button>
            </div>
            <Select v-model="feedback" :options="feedbackOptions" option-label="label" option-value="value" show-clear :placeholder="t('prompts.rating.feedback')" />
            <Textarea v-model="comment" rows="2" maxlength="1000" :placeholder="t('prompts.rating.comment')" auto-resize />
            <div class="row">
              <Button size="small" :label="t('prompts.rating.save')" :disabled="!stars" data-testid="rating-save" @click="rate(stars)" />
              <Button v-if="ratings.mine" text size="small" severity="secondary" :label="t('prompts.rating.remove')" @click="unrate" />
            </div>
          </template>
          <p v-else-if="prompt.isOwner" class="muted">{{ t('prompts.rating.ownerNote') }}</p>
          <p v-else class="muted">{{ t('prompts.rating.onlyPublic') }}</p>
          <ul v-if="ratings.entries.length && (prompt.isOwner || ratings.entries.some((e) => e.mine))" class="entries">
            <li v-for="e in ratings.entries" :key="e.userId"><strong>{{ e.userName ?? '—' }}</strong> {{ '★'.repeat(e.stars) }} <span v-if="e.feedback" class="chip">{{ t(`prompts.feedback.${e.feedback}`) }}</span> <span v-if="e.comment">{{ e.comment }}</span></li>
          </ul>
        </div>
      </TabPanel>

      <TabPanel v-if="canStats" value="stats">
        <dl v-if="stats" class="stats" data-testid="stats-panel">
          <dt>{{ t('prompts.stats.totalUses') }}</dt><dd data-testid="stat-total">{{ stats.totalUses }}</dd>
          <dt>{{ t('prompts.stats.automatic') }}</dt><dd>{{ stats.automaticExecutions }}</dd>
          <dt>{{ t('prompts.stats.successful') }}</dt><dd>{{ stats.successfulExecutions }}</dd>
          <dt>{{ t('prompts.stats.failed') }}</dt><dd>{{ stats.failedExecutions }}</dd>
          <dt>{{ t('prompts.stats.copies') }}</dt><dd data-testid="stat-copies">{{ stats.manualCopies }}</dd>
          <dt>{{ t('prompts.stats.confirmed') }}</dt><dd>{{ stats.confirmedManualUses }}</dd>
          <dt>{{ t('prompts.stats.users') }}</dt><dd>{{ stats.uniqueUsers }}</dd>
          <dt>{{ t('prompts.stats.duration') }}</dt><dd>{{ stats.averageDurationMs === null ? '—' : stats.averageDurationMs + ' ms' }}</dd>
          <dt>{{ t('prompts.stats.tokens') }}</dt><dd>{{ stats.tokensUsed }}</dd>
          <dt>{{ t('prompts.stats.cost') }}</dt><dd>{{ stats.estimatedCost === null ? t('prompts.stats.noCost') : stats.estimatedCost }}</dd>
          <dt>{{ t('prompts.stats.lastUsed') }}</dt><dd>{{ stats.lastUsedAt ? formatDate(stats.lastUsedAt, locale.locale) : '—' }}</dd>
        </dl>
        <p class="muted">{{ t('prompts.stats.copyNote') }}</p>
      </TabPanel>

      <TabPanel value="versions">
        <ul class="versions" data-testid="versions-panel">
          <li v-for="v in versions" :key="v.id">
            <div>
              <strong>{{ t('prompts.version', { n: v.number }) }}</strong>
              <span class="chip">{{ v.isPublished ? t('prompts.status.Published') : t('prompts.draft') }}</span>
              <span class="muted">{{ v.createdByName ?? '—' }} · {{ formatDate(v.createdAt, locale.locale) }}</span>
              <span v-if="canStats && usageOf(v.id)" class="muted">· {{ t('prompts.versions.runs', { n: usageOf(v.id)!.started }) }}</span>
            </div>
            <p v-if="v.changeNote" class="muted">{{ v.changeNote }}</p>
            <div class="row">
              <Button text size="small" icon="pi pi-arrows-h" :label="t('prompts.versions.compare')" :data-testid="`compare-${v.number}`" @click="compare(v)" />
              <Button v-if="prompt.isOwner" text size="small" icon="pi pi-replay" :label="t('prompts.versions.restore')" :data-testid="`restore-${v.number}`" @click="restore(v)" />
            </div>
          </li>
        </ul>
        <div v-if="diff" class="diff" data-testid="diff">
          <p class="muted">{{ t('prompts.versions.diffTitle', { a: diff.fromNumber, b: diff.toNumber, added: diff.added, removed: diff.removed }) }}</p>
          <pre><span v-for="(l, i) in diff.lines" :key="i" :class="['line', l.kind]">{{ l.kind === 'add' ? '+ ' : l.kind === 'del' ? '- ' : '  ' }}{{ l.text }}
</span></pre>
        </div>
      </TabPanel>
    </TabPanels>
  </Tabs>
</template>

<style scoped>
.panels { margin-block-start: var(--raavi-space-4); }
.muted { color: var(--raavi-text-muted); margin: 0; font-size: 0.85rem; }
.avg { margin: 0 0 var(--raavi-space-2); font-size: 1.2rem; }
.dist { list-style: none; padding: 0; margin: 0 0 var(--raavi-space-3); display: grid; gap: 2px; }
.dist li { display: grid; grid-template-columns: 2.2rem 1fr 2rem; gap: 0.4rem; align-items: center; font-size: 0.85rem; }
.bar { background: var(--raavi-border); border-radius: 4px; block-size: 0.5rem; overflow: hidden; }
.bar > span { display: block; block-size: 100%; background: var(--p-primary-color); }
.rating { display: grid; gap: var(--raavi-space-2); max-inline-size: 36rem; }
.stars { display: flex; gap: 0.15rem; }
.star { font-size: 1.7rem; line-height: 1; background: transparent; border: 0; cursor: pointer; color: var(--raavi-border); padding: 0; }
.star.on { color: var(--p-yellow-500, #eab308); }
.row { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.entries { list-style: none; padding: 0; margin: 0; display: grid; gap: 0.3rem; font-size: 0.9rem; }
.chip { border: 1px solid var(--raavi-border); border-radius: 999px; padding: 0 0.5rem; font-size: 0.78rem; margin-inline: 0.3rem; }
.stats { display: grid; grid-template-columns: max-content 1fr; gap: 0.3rem 1.2rem; margin: 0 0 var(--raavi-space-2); }
.stats dt { color: var(--raavi-text-muted); }
.stats dd { margin: 0; font-weight: 600; }
.versions { list-style: none; padding: 0; margin: 0; display: grid; gap: var(--raavi-space-2); }
.versions li { border-block-end: 1px solid var(--raavi-border); padding-block-end: var(--raavi-space-2); }
.diff pre { margin: 0; padding: var(--raavi-space-2); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); max-block-size: 18rem; overflow: auto; white-space: pre-wrap; font-size: 0.85rem; }
.line.add { background: color-mix(in srgb, var(--p-green-500, #16a34a) 18%, transparent); display: block; }
.line.del { background: color-mix(in srgb, var(--p-red-500, #dc2626) 18%, transparent); display: block; }
.line.same { display: block; color: var(--raavi-text-muted); }
</style>
