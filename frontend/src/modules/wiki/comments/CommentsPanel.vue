<script setup lang="ts">
import { computed, nextTick, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import Checkbox from 'primevue/checkbox'
import { commentsApi, type Thread } from '@/core/api/comments'
import type { RaaviDoc } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import type { AnchorHighlight, TextQuote } from '@/modules/editor/extensions/commentAnchors'
import CommentComposer from './CommentComposer.vue'
import CommentThread from './CommentThread.vue'

/**
 * Discussion of a page (ADR-024): page comments and inline threads. The parent passes the quote selected in the page
 * body (`pendingQuote`) and the ids of anchors found in the current text (`located`); this panel reports the anchors
 * to highlight and the focused thread.
 */
const props = defineProps<{ contentId: string; pendingQuote: TextQuote | null; located: string[]; active: string | null }>()
const emit = defineEmits<{
  'update:pendingQuote': [q: TextQuote | null]
  'update:active': [id: string | null]
  anchors: [anchors: AnchorHighlight[]]
  watching: [value: boolean]
}>()
const { t, te } = useI18n()
const toast = useToast()
const route = useRoute()

const threads = ref<Thread[]>([])
const canComment = ref(false)
const reactions = ref<string[]>([])
const includeResolved = ref(false)
const loaded = ref(false)
const busy = ref(false)
const pageComposer = ref<InstanceType<typeof CommentComposer> | null>(null)
const inlineComposer = ref<InstanceType<typeof CommentComposer> | null>(null)

/** Thread ids are GUIDs; anything else is never used in a selector. */
const SAFE_ID = /^[0-9a-zA-Z-]+$/

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })

const openCount = computed(() => threads.value.filter((th) => th.status === 'Open').length)

function publishAnchors() {
  // Include every inline thread currently in the panel (Open always; Resolved when "show resolved" is on)
  // so quote clicks can scroll to the passage even for resolved comments.
  emit('anchors', threads.value
    .filter((th) => th.kind === 'Inline' && th.anchor)
    .map((th) => ({ id: th.id, ...th.anchor! })))
}

async function load() {
  try {
    const r = await commentsApi.list(props.contentId, includeResolved.value)
    threads.value = r.threads
    canComment.value = r.canComment
    reactions.value = r.reactions
    emit('watching', r.watching)
    loaded.value = true
    publishAnchors()
    await focusFromHash()
  } catch (e) {
    fail(e)
  }
}
onMounted(load)
watch(() => props.contentId, load)
watch(includeResolved, load)
// A notification for the page already open only changes the hash: reload so the new comment is shown and focused.
watch(() => route.hash, (hash) => {
  if (hash.startsWith('#comment-')) void load()
})

/** Deep links from notifications and e-mail: #comment-{id}. */
async function focusFromHash() {
  const id = route.hash.startsWith('#comment-') ? route.hash.slice('#comment-'.length) : null
  if (!id) return
  const thread = threads.value.find((th) => th.comments.some((c) => c.id === id))
  if (!thread) return
  emit('update:active', thread.id)
  await nextTick()
  document.getElementById(`comment-${id}`)?.scrollIntoView?.({ block: 'center' })
}

function changed(thread: Thread) {
  threads.value = threads.value.map((th) => (th.id === thread.id ? thread : th))
  if (thread.status === 'Resolved' && !includeResolved.value) threads.value = threads.value.filter((th) => th.id !== thread.id)
  publishAnchors()
}

async function create(doc: RaaviDoc, anchor: TextQuote | null) {
  busy.value = true
  try {
    const thread = await commentsApi.create(props.contentId, doc, anchor)
    threads.value = [...threads.value, thread]
    if (anchor) {
      emit('update:pendingQuote', null)
      emit('update:active', thread.id)
      inlineComposer.value?.clear()
    } else {
      pageComposer.value?.clear()
    }
    emit('watching', true)
    publishAnchors()
  } catch (e) {
    fail(e)
  } finally {
    busy.value = false
  }
}

function focusAnchor(id: string) {
  emit('update:active', id)
  if (SAFE_ID.test(id)) document.querySelector(`[data-thread-id="${id}"]`)?.scrollIntoView?.({ block: 'start', behavior: 'smooth' })
}

// A highlighted passage was clicked in the page: show its thread.
watch(() => props.active, async (id) => {
  if (!id) return
  await nextTick()
  if (SAFE_ID.test(id)) document.querySelector(`[data-thread="${id}"]`)?.scrollIntoView?.({ block: 'nearest' })
})

// Start an inline comment from the current selection.
watch(() => props.pendingQuote, async (q) => {
  if (!q) return
  await nextTick()
  document.getElementById('inline-composer')?.scrollIntoView?.({ block: 'nearest' })
})
</script>

<template>
  <section class="comments-panel" :aria-labelledby="'comments-heading'" data-testid="comments">
    <header class="head">
      <h2 id="comments-heading">{{ t('comments.title', { n: openCount }) }}</h2>
      <label class="toggle">
        <Checkbox v-model="includeResolved" binary input-id="show-resolved" />
        <span>{{ t('comments.showResolved') }}</span>
      </label>
    </header>

    <div v-if="pendingQuote && canComment" id="inline-composer" class="inline-new" data-testid="inline-composer">
      <blockquote dir="auto">{{ pendingQuote.exact.length > 220 ? pendingQuote.exact.slice(0, 220).trimEnd() + '…' : pendingQuote.exact }}</blockquote>
      <CommentComposer
        ref="inlineComposer"
        :busy="busy"
        cancellable
        :label="t('comments.inlineLabel')"
        @submit="(d) => create(d, pendingQuote)"
        @cancel="emit('update:pendingQuote', null)"
      />
    </div>

    <p v-if="loaded && threads.length === 0" class="muted">{{ t('comments.none') }}</p>
    <CommentThread
      v-for="th in threads"
      :key="th.id"
      :thread="th"
      :reactions="reactions"
      :active="active === th.id"
      :orphaned="th.kind === 'Inline' && !located.includes(th.id)"
      @changed="changed"
      @focus-anchor="focusAnchor"
      @error="fail"
    />

    <div v-if="canComment" class="new">
      <h3 class="sr-only">{{ t('comments.add') }}</h3>
      <CommentComposer ref="pageComposer" :busy="busy" :label="t('comments.composer.label')" @submit="(d) => create(d, null)" />
    </div>
    <p v-else-if="loaded" class="muted">{{ t('comments.readOnly') }}</p>
  </section>
</template>

<style scoped>
.comments-panel { display: grid; gap: var(--raavi-space-3); border-block-start: 1px solid var(--raavi-border); padding-block-start: var(--raavi-space-3); }
.head { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-2); flex-wrap: wrap; }
h2 { font-size: 1.1rem; margin: 0; }
.toggle { display: inline-flex; gap: var(--raavi-space-2); align-items: center; font-size: 0.9rem; }
.inline-new { display: grid; gap: var(--raavi-space-2); padding: var(--raavi-space-3); border: 1px solid #d9a800; border-radius: var(--raavi-radius-sm); }
.inline-new blockquote { margin: 0; border-inline-start: 3px solid #d9a800; padding-inline-start: var(--raavi-space-2); color: var(--raavi-text-muted); font-style: italic; overflow-wrap: anywhere; }
.muted { color: var(--raavi-text-muted); }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
</style>
