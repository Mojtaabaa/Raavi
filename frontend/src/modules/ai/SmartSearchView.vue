<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import Button from 'primevue/button'
import ErrorHelp from '@/app/ErrorHelp.vue'
import { aiApi, type AskAnswer, type SearchHit } from '@/core/api/ai'
import { ApiError } from '@/core/api/http'
import { errorMessage } from '@/core/api/errors'
import { wikiApi, type SpaceSummary } from '@/core/api/wiki'
import { normalizeSearchText } from '@/core/runtime/textNormalize'
import { useAuthStore } from '@/stores/auth'
import { storeToRefs } from 'pinia'

/**
 * Smart search: finds passages by words (and by meaning when the workspace has an embedding model) among the pages the person may read,
 * and — when AI is on — answers a question from those pages with numbered sources. Everything shown has passed the person's own permissions.
 */
const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const { me } = storeToRefs(useAuthStore())

const mode = ref<'search' | 'ask'>(route.query.mode === 'ask' ? 'ask' : 'search')
const q = ref(typeof route.query.q === 'string' ? route.query.q : '')
const space = ref(typeof route.query.space === 'string' ? route.query.space : '')
const labelFilter = ref(typeof route.query.tags === 'string' ? route.query.tags : '')
const tagMode = ref<'any' | 'all'>(route.query.tagMode === 'all' ? 'all' : 'any')
const includeDescendants = ref(route.query.includeDescendants === 'true')
const classification = ref(typeof route.query.classification === 'string' ? route.query.classification : '')
const lifecycleStatus = ref(typeof route.query.lifecycle === 'string' ? route.query.lifecycle : '')
const reviewDue = ref(route.query.reviewDue === 'true')
const relationshipType = ref(typeof route.query.relationshipType === 'string' ? route.query.relationshipType : '')
const ownedByMe = ref(route.query.ownedByMe === 'true')
const reviewOwnerMe = ref(route.query.reviewOwnerMe === 'true')
const spaces = ref<SpaceSummary[]>([])
const aiAvailable = ref(false)
const hits = ref<SearchHit[]>([])
const searchMode = ref<'keyword' | 'hybrid' | null>(null)
const answer = ref<AskAnswer | null>(null)
const busy = ref(false)
const searched = ref(false)
const error = ref<unknown>(null)
const input = ref<HTMLInputElement | null>(null)

const tokens = computed(() => [...normalizeSearchText(q.value).matchAll(/[\p{L}\p{N}]{2,}/gu)].map((m) => m[0]).slice(0, 8))

/**
 * Marks the query words in a passage. The words are looked for in the FOLDED text (so کتاب finds كتاب, ۱۴۰۵ finds 1405) and the marks go on the
 * original characters; this lines up as long as folding keeps the length (it does, except for text with diacritics: then nothing is marked).
 * Built from text pieces, never from HTML, so page text cannot inject markup.
 */
function parts(text: string): { text: string; hit: boolean }[] {
  const folded = normalizeSearchText(text)
  if (tokens.value.length === 0 || folded.length !== text.length) return [{ text, hit: false }]
  const re = new RegExp(tokens.value.map((x) => x.replace(/[.*+?^${'$'}{}()|[\]\\]/g, '\\$&')).join('|'), 'gu')
  const out: { text: string; hit: boolean }[] = []
  let last = 0
  for (const m of folded.matchAll(re)) {
    if (m.index === undefined || m[0].length === 0) continue
    if (m.index > last) out.push({ text: text.slice(last, m.index), hit: false })
    out.push({ text: text.slice(m.index, m.index + m[0].length), hit: true })
    last = m.index + m[0].length
  }
  if (last < text.length) out.push({ text: text.slice(last), hit: false })
  return out.length ? out : [{ text, hit: false }]
}

let seq = 0
async function run() {
  const text = q.value.trim()
  error.value = null
  if (text.length < 2) return
  const mine = ++seq
  busy.value = true
  void router.replace({
    query: {
      q: text,
      mode: mode.value,
      ...(space.value ? { space: space.value } : {}),
      ...(labelFilter.value.trim() ? { tags: labelFilter.value.trim(), tagMode: tagMode.value, ...(includeDescendants.value ? { includeDescendants: 'true' } : {}) } : {}),
      ...(classification.value ? { classification: classification.value } : {}),
      ...(lifecycleStatus.value ? { lifecycle: lifecycleStatus.value } : {}),
      ...(reviewDue.value ? { reviewDue: 'true' } : {}),
      ...(relationshipType.value ? { relationshipType: relationshipType.value } : {}),
      ...(ownedByMe.value ? { ownedByMe: 'true' } : {}),
      ...(reviewOwnerMe.value ? { reviewOwnerMe: 'true' } : {}),
    },
  })
  try {
    if (mode.value === 'search') {
      const r = await aiApi.search(text, {
        space: space.value || undefined,
        classification: classification.value || undefined,
        lifecycleStatus: lifecycleStatus.value || undefined,
        reviewDue: reviewDue.value ? true : undefined,
        relationshipType: relationshipType.value || undefined,
        ownerUserId: ownedByMe.value && me.value?.id ? me.value.id : undefined,
        reviewOwnerUserId: reviewOwnerMe.value && me.value?.id ? me.value.id : undefined,
      })
      if (mine !== seq) return
      let next = r.hits
      const tags = labelFilter.value.trim()
      if (tags) {
        const labeled = await wikiApi.labelSearch({
          tags,
          tagMode: tagMode.value,
          includeDescendants: includeDescendants.value,
          spaceKey: space.value || undefined,
          take: 100,
        }).catch(() => ({ items: [], total: 0 }))
        const allow = new Set(labeled.items.map((i) => i.id))
        next = next.filter((h) => allow.has(h.contentId))
      }
      hits.value = next
      searchMode.value = r.mode
      answer.value = null
    } else {
      const r = await aiApi.askWiki(text, space.value || undefined)
      if (mine !== seq) return
      answer.value = r
      hits.value = []
    }
    searched.value = true
  } catch (e) {
    if (mine === seq) {
      error.value = e
      searched.value = true
      if (mode.value === 'search') hits.value = []
      else answer.value = null
    }
  } finally {
    if (mine === seq) busy.value = false
  }
}

let timer: ReturnType<typeof setTimeout> | undefined
watch(q, () => {
  if (mode.value !== 'search') return
  clearTimeout(timer)
  timer = setTimeout(run, 350)
})
watch([mode, space, classification, lifecycleStatus, relationshipType, reviewDue, ownedByMe, reviewOwnerMe], () => {
  answer.value = null
  hits.value = []
  searched.value = false
  if (q.value.trim().length >= 2 && mode.value === 'search') void run()
})
onBeforeUnmount(() => clearTimeout(timer))

onMounted(async () => {
  input.value?.focus()
  aiAvailable.value = (await aiApi.status().catch(() => ({ available: false }))).available
  if (!aiAvailable.value && mode.value === 'ask') mode.value = 'search'
  spaces.value = await wikiApi.spaces().catch(() => [])
  if (q.value.trim().length >= 2) void run()
})

const known = computed(() => (error.value instanceof ApiError ? errorMessage(error.value, t, te) : error.value ? t('errors.generic') : ''))
const pageLink = (h: { contentId: string; spaceKey: string }) => ({ name: 'page', params: { key: h.spaceKey, id: h.contentId } })

/** [1] [2] in an answer become links to the matching source (text pieces again, not HTML). */
function answerParts(text: string): { text: string; n?: number }[] {
  return text.split(/(\[\d{1,2}\])/g).filter(Boolean).map((p) => (/^\[\d{1,2}\]$/.test(p) ? { text: p, n: Number(p.slice(1, -1)) } : { text: p }))
}
</script>

<template>
  <section class="page smart" data-testid="smart-search">
    <header class="head">
      <h1>{{ t('smartSearch.title') }}</h1>
      <p class="muted">{{ t('smartSearch.intro') }}</p>
    </header>

    <form class="bar" role="search" @submit.prevent="run">
      <div class="seg" role="group" :aria-label="t('smartSearch.mode')">
        <button type="button" :class="{ on: mode === 'search' }" :aria-pressed="mode === 'search'" data-testid="mode-search" @click="mode = 'search'"><i class="pi pi-search" aria-hidden="true" /> {{ t('smartSearch.search') }}</button>
        <button type="button" :class="{ on: mode === 'ask' }" :aria-pressed="mode === 'ask'" :disabled="!aiAvailable" :title="aiAvailable ? undefined : t('smartSearch.askOff')" data-testid="mode-ask" @click="mode = 'ask'"><i class="pi pi-sparkles" aria-hidden="true" /> {{ t('smartSearch.ask') }}</button>
      </div>
      <input ref="input" v-model="q" type="search" class="q" maxlength="200" :placeholder="mode === 'ask' ? t('smartSearch.askPlaceholder') : t('smartSearch.placeholder')" :aria-label="t('smartSearch.title')" dir="auto" data-testid="smart-q" />
      <select v-model="space" :aria-label="t('smartSearch.inSpace')" data-testid="smart-space">
        <option value="">{{ t('smartSearch.allSpaces') }}</option>
        <option v-for="s in spaces" :key="s.key" :value="s.key">{{ s.name }}</option>
      </select>
      <input v-if="mode === 'search'" v-model="labelFilter" type="text" class="tags" maxlength="200" :placeholder="t('smartSearch.tagsPlaceholder')" :aria-label="t('smartSearch.tags')" dir="ltr" data-testid="smart-tags" />
      <select v-if="mode === 'search' && labelFilter.trim()" v-model="tagMode" :aria-label="t('wiki.labels.matchAny')" data-testid="smart-tag-mode">
        <option value="any">{{ t('wiki.labels.matchAny') }}</option>
        <option value="all">{{ t('wiki.labels.matchAll') }}</option>
      </select>
      <label v-if="mode === 'search' && labelFilter.trim()" class="desc">
        <input v-model="includeDescendants" type="checkbox" data-testid="smart-descendants" />
        {{ t('wiki.labels.includeDescendants') }}
      </label>
      <select v-if="mode === 'search'" v-model="classification" :aria-label="t('wiki.governance.classification')" data-testid="smart-class">
        <option value="">{{ t('smartSearch.anyClassification') }}</option>
        <option v-for="c in ['Public', 'Internal', 'Confidential', 'Restricted']" :key="c" :value="c">{{ t(`wiki.governance.class.${c}`) }}</option>
      </select>
      <select v-if="mode === 'search'" v-model="lifecycleStatus" :aria-label="t('wiki.governance.lifecycle')" data-testid="smart-life">
        <option value="">{{ t('smartSearch.anyLifecycle') }}</option>
        <option v-for="c in ['Draft', 'Current', 'InReview', 'Approved', 'Archived', 'Expired']" :key="c" :value="c">{{ t(`wiki.governance.life.${c}`) }}</option>
      </select>
      <select v-if="mode === 'search'" v-model="relationshipType" :aria-label="t('wiki.relationships.type')" data-testid="smart-rel">
        <option value="">{{ t('smartSearch.anyRelationship') }}</option>
        <option v-for="c in ['RelatedTo', 'References', 'DependsOn', 'Documents', 'Implements', 'Supersedes']" :key="c" :value="c">{{ t(`wiki.relationships.types.${c}`) }}</option>
      </select>
      <label v-if="mode === 'search'" class="desc">
        <input v-model="reviewDue" type="checkbox" data-testid="smart-review-due" />
        {{ t('smartSearch.reviewDueOnly') }}
      </label>
      <label v-if="mode === 'search'" class="desc">
        <input v-model="ownedByMe" type="checkbox" data-testid="smart-owned-by-me" />
        {{ t('smartSearch.ownedByMe') }}
      </label>
      <label v-if="mode === 'search'" class="desc">
        <input v-model="reviewOwnerMe" type="checkbox" data-testid="smart-review-owner-me" />
        {{ t('smartSearch.reviewOwnerMe') }}
      </label>
      <Button v-if="mode === 'ask'" type="submit" icon="pi pi-send" :label="t('smartSearch.send')" :loading="busy" :disabled="q.trim().length < 2" data-testid="smart-send" />
    </form>

    <p v-if="mode === 'search' && searchMode" class="mode muted small" data-testid="search-mode">
      <i :class="['pi', searchMode === 'hybrid' ? 'pi-sparkles' : 'pi-align-left']" aria-hidden="true" />
      {{ searchMode === 'hybrid' ? t('smartSearch.hybrid') : t('smartSearch.keyword') }}
    </p>

    <ErrorHelp v-if="error && !known" :error="error" />
    <p v-if="error" class="error" role="alert" data-testid="smart-error">{{ known }}</p>

    <!-- Answer -->
    <article v-if="answer" class="answer" aria-live="polite" data-testid="answer">
      <p class="text" dir="auto"><template v-for="(p, i) in answerParts(answer.text)" :key="i"><a v-if="p.n" :href="`#source-${p.n}`" class="cite">{{ p.text }}</a><template v-else>{{ p.text }}</template></template></p>
      <p class="muted small"><i class="pi pi-info-circle" aria-hidden="true" /> {{ t('smartSearch.check') }}</p>
      <h2>{{ t('smartSearch.sources') }}</h2>
      <ol class="sources">
        <li v-for="s in answer.sources" :id="`source-${s.number}`" :key="s.contentId">
          <RouterLink :to="pageLink(s)" dir="auto">{{ s.title }}</RouterLink>
          <span class="space"><code dir="ltr">{{ s.spaceKey }}</code></span>
          <p class="snippet" dir="auto">{{ s.snippet }}</p>
        </li>
      </ol>
    </article>

    <!-- Results -->
    <ul v-if="hits.length" class="hits" data-testid="hits">
      <li v-for="h in hits" :key="h.contentId" class="hit">
        <RouterLink :to="pageLink(h)" class="title" dir="auto" data-testid="hit">{{ h.title }}</RouterLink>
        <span class="space"><code dir="ltr">{{ h.spaceKey }}</code></span>
        <span v-if="h.semantic" class="sem" :title="t('smartSearch.semanticHelp')"><i class="pi pi-sparkles" aria-hidden="true" /></span>
        <p class="snippet" dir="auto"><template v-for="(p, i) in parts(h.snippet)" :key="i"><mark v-if="p.hit">{{ p.text }}</mark><template v-else>{{ p.text }}</template></template></p>
      </li>
    </ul>
    <p v-else-if="searched && !busy && !error && mode === 'search'" class="empty muted" data-testid="no-hits">{{ t('smartSearch.none') }}</p>
    <p v-else-if="!searched && !busy" class="tip muted">{{ t('smartSearch.tip') }}</p>
  </section>
</template>

<style scoped>
.smart { max-inline-size: 56rem; margin-inline: auto; display: grid; gap: var(--raavi-space-3); }
h1 { margin: 0; font-size: 1.4rem; }
h2 { margin: var(--raavi-space-3) 0 var(--raavi-space-1); font-size: 0.95rem; }
.head p { margin: 4px 0 0; }
.bar { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.seg { display: inline-flex; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); overflow: hidden; }
.seg button { font: inherit; border: 0; background: transparent; color: var(--raavi-text-muted); padding: 0.4rem 0.9rem; cursor: pointer; display: inline-flex; gap: 6px; align-items: center; }
.seg button.on { background: var(--p-primary-color); color: var(--p-primary-contrast-color, #fff); }
.seg button:disabled { opacity: 0.45; cursor: not-allowed; }
.q { flex: 1; min-inline-size: 14rem; font: inherit; padding: 0.5rem 0.75rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: var(--raavi-surface); color: inherit; }
.tags { min-inline-size: 12rem; font: inherit; padding: 0.5rem 0.75rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: var(--raavi-surface); color: inherit; }
.desc { display: inline-flex; align-items: center; gap: 0.35rem; font-size: 0.9rem; }
.bar select { font: inherit; padding: 0.45rem 0.5rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: var(--raavi-surface); color: inherit; max-inline-size: 12rem; }
.hits { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-3); }
.hit { padding: var(--raavi-space-3) var(--raavi-space-4); background: var(--raavi-surface); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-md); }
.title { font-weight: 600; font-size: 1.02rem; color: var(--p-primary-color); text-decoration: none; }
.title:hover { text-decoration: underline; }
.space { margin-inline-start: var(--raavi-space-2); }
.sem { margin-inline-start: var(--raavi-space-1); color: var(--p-primary-color); }
.snippet { margin: 4px 0 0; color: var(--raavi-text-muted); overflow-wrap: anywhere; }
mark { background: color-mix(in srgb, #f5c400 40%, transparent); color: inherit; border-radius: 2px; padding: 0 1px; }
.answer { padding: var(--raavi-space-4); background: var(--raavi-surface); border: 1px solid var(--p-primary-color); border-radius: var(--raavi-radius-md); }
.answer .text { margin: 0 0 var(--raavi-space-2); white-space: pre-wrap; line-height: 1.8; }
.cite { font-weight: 700; color: var(--p-primary-color); text-decoration: none; padding: 0 1px; }
.sources { margin: 0; padding-inline-start: 1.4rem; display: grid; gap: var(--raavi-space-2); }
.sources .snippet { margin: 2px 0 0; font-size: 0.88rem; }
.mode { display: flex; gap: 6px; align-items: center; margin: 0; }
.muted { color: var(--raavi-text-muted); }
.small { font-size: 0.82rem; }
.error { color: var(--raavi-danger); margin: 0; }
.empty, .tip { text-align: center; margin: var(--raavi-space-4) 0; }
</style>
