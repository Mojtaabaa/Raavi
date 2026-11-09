<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import { useConfirm } from 'primevue/useconfirm'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Message from 'primevue/message'
import Dialog from 'primevue/dialog'
import Checkbox from 'primevue/checkbox'
import { ApiError } from '@/core/api/http'
import { wikiApi, type Draft, type Page, type RaaviDoc, type Template } from '@/core/api/wiki'
import { errorMessage, errorSteps } from '@/core/api/errors'
import ErrorHelp from '@/app/ErrorHelp.vue'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import RaaviEditor from '@/modules/editor/components/RaaviEditor.vue'
import type { CollaborationTarget } from '@/modules/editor/collaboration'
import { useAuthStore } from '@/stores/auth'
import { useSpaceContext } from '../spaceContext'

/**
 * Create (title, type, template → private draft page) or edit the shared draft of a page (ADR-019). Every change is
 * autosaved to the draft (If-Match on the draft revision); readers keep seeing the published version until "Publish".
 * Folders have no draft: their title is saved directly.
 */
const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()
const ctx = useSpaceContext()
const auth = useAuthStore()

/** Real-time mode (Phase 6): the collaboration server stores the body; this view saves the title and publishes. */
const live = ref<CollaborationTarget | null>(null)
const liveRevoked = ref(false)
const editorKey = ref(0)

async function joinLive(): Promise<void> {
  const me = auth.me
  if (!me) return
  try {
    if (!(await wikiApi.collaborationConfig()).enabled) return
    const issued = await wikiApi.collaborationTicket(id.value)
    let first: string | null = issued.ticket
    live.value = {
      documentName: issued.documentName,
      url: issued.url,
      // The first ticket is used for the first connection; every reconnect fetches a fresh one (re-checked by the API).
      ticket: async () => {
        if (first) {
          const t = first
          first = null
          return t
        }
        return (await wikiApi.collaborationTicket(id.value)).ticket
      },
      user: { id: me.id, name: me.displayName },
    }
  } catch {
    live.value = null // no live editing: the REST draft autosave is used
  }
}

function onLiveReset() {
  // The draft was replaced (discarded/restored elsewhere): rejoin with fresh local state.
  void reloadDraft()
  editorKey.value++
}

const isCreate = computed(() => route.name === 'page-create')
const key = computed(() => String(route.params.key))
const id = computed(() => String(route.params.id ?? ''))

const loaded = ref(false)
const page = ref<Page | null>(null)
const pageEtag = ref<string | null>(null)
const draft = ref<Draft | null>(null)
const draftEtag = ref<string | null>(null)
const title = ref('')
const body = ref<RaaviDoc | null>(null)
const contentType = ref<'page' | 'folder'>('page')
const templates = ref<Template[]>([])
const templateId = ref<string | null>(null)

type SaveState = 'idle' | 'pending' | 'saving' | 'saved' | 'conflict' | 'error'
const saveState = ref<SaveState>('idle')
const busy = ref(false)
let timer: ReturnType<typeof setTimeout> | undefined
let suppress = true // ignore the editor's initial normalisation
let leaving = false

const publishOpen = ref(false)
const changeSummary = ref('')
const schedule = ref(false)
const publishAt = ref('')
const previewOpen = ref(false)

const typeOptions = computed(() => [
  { value: 'page', label: t('wiki.page.typePage') },
  { value: 'folder', label: t('wiki.page.typeFolder') },
])
const templateOptions = computed(() => [{ id: null, name: t('wiki.templates.blank') }, ...templates.value])
const fmt = (iso: string | null | undefined) => (iso ? formatDate(iso, locale.locale, { dateStyle: 'medium', timeStyle: 'short' }) : '')
/** The last failure that has guidance (steps): shown inline under the title bar, so the person can read what to do, instead of only a toast that disappears. */
// The title bar sticks below the app header; the editor toolbar sticks below the bar (otherwise the bar covers it while scrolling a long page).
const barEl = ref<HTMLElement | null>(null)
const barHeight = ref(0)
let barObserver: ResizeObserver | null = null
watch(barEl, (el) => {
  barObserver?.disconnect()
  barObserver = null
  if (!el) return
  barHeight.value = Math.round(el.getBoundingClientRect().height)
  if (typeof ResizeObserver !== 'undefined') {
    barObserver = new ResizeObserver(() => (barHeight.value = Math.round(el.getBoundingClientRect().height)))
    barObserver.observe(el)
  }
})
onBeforeUnmount(() => barObserver?.disconnect())
const guidedError = ref<unknown>(null)
const fail = (e: unknown) => {
  guidedError.value = errorSteps(e, t, te).length ? e : null
  toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
}

onMounted(async () => {
  try {
    if (isCreate.value) {
      templates.value = await wikiApi.spaceTemplates(key.value).catch(() => [])
    } else {
      const p = await wikiApi.content(id.value)
      page.value = p.value
      pageEtag.value = p.etag
      contentType.value = p.value.contentType
      if (!p.value.rights.canEdit) {
        await router.replace({ name: 'page', params: { key: key.value, id: id.value } })
        return
      }
      if (p.value.contentType === 'page') {
        await joinLive()
        const d = await wikiApi.draft(id.value)
        draft.value = d.value
        draftEtag.value = d.etag
        title.value = d.value.title
        body.value = d.value.body
      } else {
        title.value = p.value.title
      }
    }
  } catch (e) {
    fail(e)
  } finally {
    loaded.value = true
    setTimeout(() => (suppress = false), 300)
  }
})

// ── Autosave ────────────────────────────────────────────────────────────────────────────────
watch([title, body], ([newTitle], [oldTitle]) => {
  if (isCreate.value || contentType.value !== 'page' || suppress || saveState.value === 'conflict') return
  if (live.value && newTitle === oldTitle) return // live mode: the body is persisted by the collaboration server
  saveState.value = 'pending'
  clearTimeout(timer)
  timer = setTimeout(() => void saveDraft(), 1200)
})

async function saveDraft(): Promise<boolean> {
  clearTimeout(timer)
  if (saveState.value === 'conflict') return false
  if (!title.value.trim()) return false
  saveState.value = 'saving'
  try {
    if (live.value) {
      draft.value = await wikiApi.saveDraftTitle(id.value, title.value)
      saveState.value = 'saved'
      guidedError.value = null
      return true
    }
    const r = await wikiApi.saveDraft(id.value, draftEtag.value, { title: title.value, body: body.value })
    draft.value = r.value
    draftEtag.value = r.etag
    saveState.value = 'saved'
    guidedError.value = null
    return true
  } catch (e) {
    if (e instanceof ApiError && e.status === 412) saveState.value = 'conflict'
    else {
      saveState.value = 'error'
      fail(e)
    }
    return false
  }
}

onBeforeUnmount(() => clearTimeout(timer))
onBeforeRouteLeave(async () => {
  if (leaving || isCreate.value || contentType.value !== 'page') return true
  if (saveState.value === 'pending') await saveDraft()
  return saveState.value !== 'error' || window.confirm(t('wiki.edit.discard'))
})

const saveLabel = computed(() => {
  switch (saveState.value) {
    case 'pending':
    case 'saving':
      return t('wiki.draft.saving')
    case 'saved':
      return t('wiki.draft.savedAt', { time: fmt(draft.value?.updatedAt) })
    case 'conflict':
      return t('wiki.draft.conflict')
    case 'error':
      return t('wiki.draft.error')
    default:
      return draft.value?.exists ? t('wiki.draft.savedAt', { time: fmt(draft.value.updatedAt) }) : t('wiki.draft.noChanges')
  }
})

async function reloadDraft() {
  const d = await wikiApi.draft(id.value)
  suppress = true
  draft.value = d.value
  draftEtag.value = d.etag
  title.value = d.value.title
  body.value = d.value.body
  saveState.value = 'idle'
  setTimeout(() => (suppress = false), 300)
}

// ── Create ──────────────────────────────────────────────────────────────────────────────────
async function create() {
  busy.value = true
  try {
    const parentId = typeof route.query.parentId === 'string' ? route.query.parentId : null
    const tpl = templates.value.find((x) => x.id === templateId.value)
    const created = await wikiApi.createContent(key.value, {
      title: title.value,
      parentId,
      contentType: contentType.value,
      ...(contentType.value === 'page' ? { publish: false, ...(tpl ? { body: tpl.body } : {}) } : {}),
    })
    leaving = true
    ctx.reloadTree()
    await router.replace(created.contentType === 'page' ? { name: 'page-edit', params: { key: created.spaceKey, id: created.id } } : { name: 'page', params: { key: created.spaceKey, id: created.id } })
  } catch (e) {
    fail(e)
  } finally {
    busy.value = false
  }
}

// ── Folder title ────────────────────────────────────────────────────────────────────────────
async function saveFolder() {
  busy.value = true
  try {
    const r = await wikiApi.updateContent(id.value, pageEtag.value, { title: title.value, body: null, changeSummary: null })
    leaving = true
    ctx.reloadTree()
    await router.push({ name: 'page', params: { key: r.value.spaceKey, id: r.value.id } })
  } catch (e) {
    fail(e)
  } finally {
    busy.value = false
  }
}

// ── Publish ─────────────────────────────────────────────────────────────────────────────────
function openPublish() {
  changeSummary.value = ''
  schedule.value = false
  const inOneHour = new Date(Date.now() + 3600_000)
  inOneHour.setSeconds(0, 0)
  publishAt.value = new Date(inOneHour.getTime() - inOneHour.getTimezoneOffset() * 60000).toISOString().slice(0, 16)
  publishOpen.value = true
}

async function publish() {
  busy.value = true
  try {
    if (saveState.value === 'pending' || (!live.value && !draft.value?.exists)) {
      if (!(await saveDraft())) return
    }
    // Live mode: the API flushes the collaboration server and publishes the latest shared draft (If-Match: *).
    const r = await wikiApi.publish(id.value, live.value ? '*' : draftEtag.value, {
      changeSummary: changeSummary.value || null,
      publishAt: schedule.value && publishAt.value ? new Date(publishAt.value).toISOString() : null,
    })
    publishOpen.value = false
    const outcome = r.value.outcome
    toast.add({ severity: outcome === 'Published' ? 'success' : 'info', summary: t(`wiki.publish.outcome.${outcome}`), life: 4000 })
    leaving = true
    ctx.reloadTree()
    await router.push({ name: 'page', params: { key: r.value.page.spaceKey, id: r.value.page.id, slug: r.value.page.slug || undefined } })
  } catch (e) {
    if (e instanceof ApiError && e.status === 412) saveState.value = 'conflict'
    fail(e)
  } finally {
    busy.value = false
  }
}

async function cancelSchedule() {
  try {
    draft.value = await wikiApi.cancelSchedule(id.value)
    const d = await wikiApi.draft(id.value)
    draftEtag.value = d.etag
  } catch (e) {
    fail(e)
  }
}

function discard() {
  confirm.require({
    header: t('common.confirmTitle'),
    message: page.value?.status === 'Draft' ? t('wiki.draft.confirmDiscardNew') : t('wiki.draft.confirmDiscard'),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        clearTimeout(timer)
        await wikiApi.discardDraft(id.value)
        leaving = true
        ctx.reloadTree()
        await router.push(page.value?.status === 'Draft' ? { name: 'space', params: { key: key.value } } : { name: 'page', params: { key: key.value, id: id.value } })
      } catch (e) {
        fail(e)
      }
    },
  })
}

function close() {
  void router.push(isCreate.value || !page.value || page.value.status === 'Draft'
    ? { name: 'space', params: { key: key.value } }
    : { name: 'page', params: { key: key.value, id: id.value } })
}
</script>

<template>
  <!-- ── Create ── -->
  <form v-if="loaded && isCreate" class="editor-page narrow" @submit.prevent="create">
    <h1>{{ t('wiki.edit.createTitle') }}</h1>
    <div class="row">
      <span id="ct-label">{{ t('wiki.page.type') }}</span>
      <SelectButton v-model="contentType" :options="typeOptions" option-label="label" option-value="value" :allow-empty="false" aria-labelledby="ct-label" />
    </div>
    <template v-if="contentType === 'page' && templates.length">
      <label for="tpl">{{ t('wiki.templates.use') }}</label>
      <Select v-model="templateId" input-id="tpl" :options="templateOptions" option-label="name" option-value="id" />
    </template>
    <label for="page-title">{{ t('wiki.page.titleLabel') }}</label>
    <InputText id="page-title" v-model="title" required maxlength="255" dir="auto" class="title" autofocus />
    <small v-if="contentType === 'page'" class="muted">{{ t('wiki.draft.createHelp') }}</small>
    <div class="actions">
      <Button type="submit" :label="contentType === 'page' ? t('wiki.draft.create') : t('common.create')" :loading="busy" data-testid="create-content" />
      <Button type="button" text :label="t('common.cancel')" @click="close" />
    </div>
  </form>

  <!-- ── Folder ── -->
  <form v-else-if="loaded && contentType === 'folder'" class="editor-page narrow" @submit.prevent="saveFolder">
    <h1>{{ t('wiki.edit.editTitle') }}</h1>
    <label for="page-title">{{ t('wiki.page.titleLabel') }}</label>
    <InputText id="page-title" v-model="title" required maxlength="255" dir="auto" class="title" />
    <div class="actions">
      <Button type="submit" :label="t('common.save')" :loading="busy" />
      <Button type="button" text :label="t('common.cancel')" @click="close" />
    </div>
  </form>

  <!-- ── Draft editor ── -->
  <div v-else-if="loaded && draft" class="editor-page" :style="{ '--raavi-editor-top': `calc(var(--raavi-header-offset) + ${barHeight}px)` }">
    <header ref="barEl" class="bar">
      <InputText id="page-title" v-model="title" required maxlength="255" dir="auto" class="title grow" :aria-label="t('wiki.page.titleLabel')" />
      <span class="save-state" :class="saveState" role="status" data-testid="save-state">{{ saveLabel }}</span>
      <Button text :label="t('wiki.draft.preview')" icon="pi pi-eye" @click="previewOpen = true" />
      <Button text severity="danger" :label="t('wiki.draft.discard')" icon="pi pi-trash" :disabled="!draft.exists && page?.status !== 'Draft'" @click="discard" />
      <Button text :label="t('wiki.draft.close')" @click="close" />
      <Button
        :label="page?.requirePublishApproval && !page?.rights.canApprove ? t('wiki.publish.request') : t('wiki.publish.button')"
        icon="pi pi-send"
        :disabled="!page?.rights.canPublish || saveState === 'conflict' || liveRevoked"
        data-testid="publish"
        @click="openPublish"
      />
    </header>

    <ErrorHelp v-if="guidedError" :error="guidedError" data-testid="edit-error-help" />
    <Message v-if="saveState === 'conflict'" severity="warn" :closable="false" data-testid="edit-conflict">
      {{ t('wiki.edit.conflict') }}
      <Button text size="small" :label="t('wiki.draft.reload')" @click="reloadDraft" />
    </Message>
    <Message v-if="draft.isStale" severity="warn" :closable="false">{{ t('wiki.draft.stale', { version: draft.publishedVersion }) }}</Message>
    <Message v-if="page?.status === 'Draft'" severity="info" :closable="false">{{ t('wiki.draft.unpublishedPage') }}</Message>
    <Message v-if="draft.scheduledPublishAt" severity="info" :closable="false">
      {{ t('wiki.publish.scheduledFor', { time: fmt(draft.scheduledPublishAt), name: draft.scheduledByName ?? '' }) }}
      <Button text size="small" :label="t('wiki.publish.cancelSchedule')" @click="cancelSchedule" />
    </Message>
    <Message v-if="draft.pendingRequest" severity="info" :closable="false">
      {{ t('wiki.publish.pending', { name: draft.pendingRequest.requestedByName ?? '', time: fmt(draft.pendingRequest.requestedAt) }) }}
    </Message>
    <p v-if="draft.contributors.length" class="muted small">{{ t('wiki.draft.contributors', { names: draft.contributors.join('، ') }) }}</p>

    <RaaviEditor
      :key="editorKey"
      v-model="body"
      editable
      :content-id="id"
      :collaboration="live"
      :placeholder="t('editor.placeholder')"
      @reset="onLiveReset"
      @revoked="liveRevoked = true"
    />

    <Dialog v-model:visible="publishOpen" modal :header="t('wiki.publish.title')" :style="{ inlineSize: 'min(30rem, 95vw)' }">
      <form class="form" @submit.prevent="publish">
        <Message v-if="page?.requirePublishApproval && !page?.rights.canApprove" severity="info" :closable="false">{{ t('wiki.publish.approvalNote') }}</Message>
        <label for="change-summary">{{ t('wiki.edit.changeSummary') }}</label>
        <InputText id="change-summary" v-model="changeSummary" maxlength="500" />
        <div class="row">
          <Checkbox v-model="schedule" input-id="schedule" binary />
          <label for="schedule">{{ t('wiki.publish.schedule') }}</label>
        </div>
        <template v-if="schedule">
          <label for="publish-at">{{ t('wiki.publish.at') }}</label>
          <input id="publish-at" v-model="publishAt" type="datetime-local" class="p-inputtext" required />
        </template>
        <Button type="submit" :label="schedule ? t('wiki.publish.scheduleButton') : t('wiki.publish.button')" :loading="busy" data-testid="confirm-publish" />
      </form>
    </Dialog>

    <Dialog v-model:visible="previewOpen" modal :header="t('wiki.draft.previewTitle', { title })" :style="{ inlineSize: 'min(60rem, 95vw)' }">
      <h1 class="preview-title" dir="auto">{{ title }}</h1>
      <RaaviEditor v-if="previewOpen" :model-value="body" />
    </Dialog>
  </div>
</template>

<style scoped>
.editor-page { display: grid; gap: var(--raavi-space-2); max-inline-size: 64rem; }
.editor-page.narrow { max-inline-size: 40rem; }
h1 { margin: 0 0 var(--raavi-space-2); font-size: 1.4rem; }
.title { font-size: 1.3rem; }
.bar { display: flex; flex-wrap: wrap; align-items: center; gap: var(--raavi-space-2); position: sticky; inset-block-start: var(--raavi-header-offset, var(--raavi-header-height)); z-index: 6; background: var(--raavi-bg, #f8fafc); padding-block: var(--raavi-space-2); }
.grow { flex: 1 1 16rem; }
.save-state { font-size: 0.85rem; color: var(--raavi-text-muted); white-space: nowrap; }
.save-state.conflict, .save-state.error { color: #b91c1c; }
.row { display: flex; align-items: center; gap: var(--raavi-space-2); }
.actions { display: flex; gap: var(--raavi-space-2); margin-block-start: var(--raavi-space-2); }
.form { display: grid; gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); }
.small { font-size: 0.85rem; margin: 0; }
.preview-title { font-size: 1.8rem; }
</style>
