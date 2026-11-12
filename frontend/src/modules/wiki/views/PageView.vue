<script setup lang="ts">
import UserAvatar from '@/app/UserAvatar.vue'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import { useConfirm } from 'primevue/useconfirm'
import Button from 'primevue/button'
import Menu from 'primevue/menu'
import Tag from 'primevue/tag'
import Message from 'primevue/message'
import AutoComplete from 'primevue/autocomplete'
import Dialog from 'primevue/dialog'
import { ApiError } from '@/core/api/http'
import { wikiApi, type Page, type TreeNode } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import DocumentBody from '../components/DocumentBody.vue'
import MoveDialog from '../components/MoveDialog.vue'
import RestrictionsDialog from '../components/RestrictionsDialog.vue'
import { useSpaceContext } from '../spaceContext'
import { useCrumbsStore } from '@/stores/crumbs'
import { spaceTrail } from '@/core/navigation/trail'
import CommentsPanel from '../comments/CommentsPanel.vue'
import LinkedJiraPanel from '../components/LinkedJiraPanel.vue'
import GovernancePanel from '../components/GovernancePanel.vue'
import RelationshipsPanel from '../components/RelationshipsPanel.vue'
import PageApprovals from '@/modules/approvals/PageApprovals.vue'
import PageAssistant from '@/modules/ai/PageAssistant.vue'
import PagePromptsDialog from '@/modules/prompts/PagePromptsDialog.vue'
import { aiApi } from '@/core/api/ai'
import { textToDoc } from '@/core/api/prompts'
import { commentsApi } from '@/core/api/comments'
import type { AnchorHighlight, TextQuote } from '@/modules/editor/extensions/commentAnchors'

const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()
const ctx = useSpaceContext()
const crumbs = useCrumbsStore()

const page = ref<Page | null>(null)
const etag = ref<string | null>(null)
const children = ref<TreeNode[]>([])
const notFound = ref(false)
const moveOpen = ref(false)
const restrictOpen = ref(false)
const newLabel = ref('')
const labelSuggestions = ref<string[]>([])
const pageLabelSuggestions = ref<{ slug: string; displayName: string; source: string }[]>([])
const menu = ref<InstanceType<typeof Menu> | null>(null)

// ── Comments and watching (Phase 7a) ───────────────────────────────────────────────
const anchors = ref<AnchorHighlight[]>([])
const located = ref<string[]>([])
const activeThread = ref<string | null>(null)
const selection = ref<TextQuote | null>(null)
const pendingQuote = ref<TextQuote | null>(null)
const watching = ref(false)
const watchBusy = ref(false)
const discussable = computed(() => page.value?.contentType === 'page' && page.value.status === 'Current')

function onQuote(q: TextQuote | null) {
  selection.value = q
}
const promptsOpen = ref(false)
const jiraPanel = ref<InstanceType<typeof LinkedJiraPanel> | null>(null)

function jiraQuote() {
  return selection.value
    ? { exact: selection.value.exact, prefix: selection.value.prefix, suffix: selection.value.suffix }
    : pendingQuote.value
      ? { exact: pendingQuote.value.exact, prefix: pendingQuote.value.prefix, suffix: pendingQuote.value.suffix }
      : null
}

function createJiraFromSelection() {
  pendingQuote.value = selection.value
  jiraPanel.value?.openCreate()
}

function linkJiraFromSelection() {
  pendingQuote.value = selection.value
  jiraPanel.value?.openLink()
}

function locateQuote(exact: string, prefix: string | null, suffix: string | null) {
  toast.add({ severity: 'info', summary: t('jiraLinks.quoteHint'), detail: exact.slice(0, 160), life: 6000 })
  void prefix
  void suffix
}
/** Adds an AI answer to the page's DRAFT after an explicit confirmation; the published page and existing text are never overwritten. */
function insertIntoPage(text: string) {
  if (!page.value || !text.trim()) return
  const id = page.value.id
  confirm.require({
    header: t('prompts.page.insertTitle'),
    message: t('prompts.page.insertConfirm'),
    icon: 'pi pi-exclamation-triangle',
    acceptProps: { label: t('prompts.page.insertYes') },
    rejectProps: { severity: 'secondary', outlined: true, label: t('common.cancel') },
    accept: async () => {
      try {
        const draft = await wikiApi.draft(id)
        const current = draft.value
        const addition = textToDoc(text).doc.content ?? []
        const base = current.exists ? current.body : { schema: 'raavi.doc' as const, schemaVersion: 3, doc: { type: 'doc' as const, content: [] as never[] } }
        const body = { ...base, doc: { ...base.doc, content: [...(base.doc.content ?? []), ...addition] } }
        await wikiApi.saveDraft(id, draft.etag, { title: current.title || page.value!.title, body })
        toast.add({ severity: 'success', summary: t('prompts.page.inserted'), life: 5000 })
        promptsOpen.value = false
      } catch (e) {
        toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
      }
    },
  })
}

function commentOnSelection() {
  pendingQuote.value = selection.value
  selection.value = null
  window.getSelection()?.removeAllRanges()
}
async function toggleWatch() {
  if (!page.value) return
  watchBusy.value = true
  try {
    watching.value = (await commentsApi.watch(page.value.id, !watching.value)).watching
    toast.add({ severity: 'success', summary: t(watching.value ? 'comments.watch.on' : 'comments.watch.off'), life: 3000 })
  } catch (e) {
    fail(e)
  } finally {
    watchBusy.value = false
  }
}

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })

async function load() {
  try {
    const r = await wikiApi.content(String(route.params.id))
    page.value = r.value
    etag.value = r.etag
    notFound.value = false
    ctx.activePath.value = [...r.value.breadcrumbs.map((b) => b.id), r.value.id]
    crumbs.set(spaceTrail({ key: r.value.spaceKey, name: r.value.spaceName }, t, ...r.value.breadcrumbs.map((b) => ({ label: b.title, to: { name: 'page', params: { key: r.value.spaceKey, id: b.id } } })), { label: r.value.title }))
    children.value = r.value.status === 'Current' ? await wikiApi.tree(r.value.spaceKey, r.value.id).catch(() => []) : []
    // A page moved to another space is shown under its new space.
    if (r.value.spaceKey !== route.params.key) await router.replace({ name: 'page', params: { key: r.value.spaceKey, id: r.value.id, slug: r.value.slug || undefined } })
    await loadPageLabelSuggestions()
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) notFound.value = true
    else fail(e)
  }
}
onMounted(load)

const updated = computed(() => (page.value ? formatDate(page.value.updatedAt, locale.locale, { dateStyle: 'medium', timeStyle: 'short' }) : ''))

const actions = computed(() => {
  const p = page.value
  if (!p) return []
  const items: { label: string; icon: string; command: () => void }[] = []
  if (p.rights.canMove) items.push({ label: t('wiki.page.move'), icon: 'pi pi-arrows-alt', command: () => (moveOpen.value = true) })
  if (p.rights.canMove) {
    items.push({ label: t('wiki.page.moveUp'), icon: 'pi pi-arrow-up', command: () => reorder(-1) })
    items.push({ label: t('wiki.page.moveDown'), icon: 'pi pi-arrow-down', command: () => reorder(1) })
  }
  if (p.rights.canRestrict) items.push({ label: t('wiki.page.restrictions'), icon: 'pi pi-lock', command: () => (restrictOpen.value = true) })
  if (p.contentType === 'page' && p.version > 0) items.push({ label: t('wiki.page.history'), icon: 'pi pi-history', command: () => void router.push({ name: 'page-history', params: { key: p.spaceKey, id: p.id } }) })
  if (p.rights.canArchive) {
    items.push(
      p.status === 'Archived'
        ? { label: t('wiki.page.unarchive'), icon: 'pi pi-replay', command: () => archive(false) }
        : { label: t('wiki.page.archive'), icon: 'pi pi-inbox', command: () => archive(true) },
    )
  }
  if (p.rights.canDelete) items.push({ label: t('wiki.page.delete'), icon: 'pi pi-trash', command: remove })
  return items
})

async function reorder(delta: number) {
  const p = page.value
  if (!p) return
  try {
    const siblings = await wikiApi.tree(p.spaceKey, p.parentId)
    const index = siblings.findIndex((s) => s.id === p.id)
    if (index < 0) return
    const r = await wikiApi.move(p.id, etag.value, { spaceKey: null, parentId: p.parentId, position: Math.max(0, index + delta) })
    page.value = r.value
    etag.value = r.etag
    ctx.reloadTree()
  } catch (e) {
    fail(e)
  }
}

async function archive(value: boolean) {
  if (!page.value) return
  try {
    const r = await wikiApi.archive(page.value.id, etag.value, value)
    page.value = r.value
    etag.value = r.etag
    ctx.reloadTree()
  } catch (e) {
    fail(e)
  }
}

function remove() {
  const p = page.value
  if (!p) return
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('wiki.page.confirmDelete', { title: p.title }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await wikiApi.trash(p.id, etag.value)
        ctx.reloadTree()
        await router.push(p.parentId ? { name: 'page', params: { key: p.spaceKey, id: p.parentId } } : { name: 'space', params: { key: p.spaceKey } })
      } catch (e) {
        fail(e)
      }
    },
  })
}

async function suggest(e: { query: string }) {
  labelSuggestions.value = await wikiApi.suggestLabels(e.query.trim().toLowerCase()).catch(() => [])
}

async function loadPageLabelSuggestions() {
  if (!page.value?.rights.canEdit) {
    pageLabelSuggestions.value = []
    return
  }
  const id = page.value.id
  const assigned = new Set(page.value.labels.map((l) => l.toLowerCase()))
  const [heuristic, ai] = await Promise.all([
    wikiApi.labelSuggestionsForPage(id).catch(() => []),
    aiApi.labelSuggestions(id).catch(() => []),
  ])
  const bySlug = new Map<string, { slug: string; displayName: string; source: string }>()
  for (const r of heuristic) {
    if (!assigned.has(r.slug.toLowerCase())) {
      bySlug.set(r.slug.toLowerCase(), { slug: r.slug, displayName: r.displayName, source: r.source || 'heuristic' })
    }
  }
  for (const r of ai) {
    if (!assigned.has(r.slug.toLowerCase())) {
      bySlug.set(r.slug.toLowerCase(), { slug: r.slug, displayName: r.displayName, source: 'ai' })
    }
  }
  pageLabelSuggestions.value = [...bySlug.values()]
}

async function addLabel(nameOverride?: string) {
  const name = (nameOverride ?? newLabel.value).trim()
  if (!page.value || !name) return
  try {
    page.value = { ...page.value, labels: await wikiApi.addLabel(page.value.id, name) }
    newLabel.value = ''
    await loadPageLabelSuggestions()
  } catch (e) {
    fail(e)
  }
}

async function removeLabel(name: string) {
  if (!page.value) return
  try {
    page.value = { ...page.value, labels: await wikiApi.removeLabel(page.value.id, name) }
    await loadPageLabelSuggestions()
  } catch (e) {
    fail(e)
  }
}

function onMoved(p: Page, newEtag: string | null) {
  page.value = p
  etag.value = newEtag
  ctx.reloadTree()
  void load()
}
</script>

<template>
  <div v-if="notFound" class="missing">
    <h1>{{ t('wiki.page.notFound') }}</h1>
    <p class="muted">{{ t('wiki.page.notFoundHelp') }}</p>
  </div>
  <article v-else-if="page" class="page">
    <header class="head">
      <h1 dir="auto">
        <i v-if="page.contentType === 'folder'" class="pi pi-folder" aria-hidden="true" />
        {{ page.title }}
      </h1>
      <div class="tools">
        <Button
          v-if="page.rights.canEdit"
          as="router-link"
          :to="{ name: 'page-edit', params: { key: page.spaceKey, id: page.id } }"
          icon="pi pi-pencil"
          :label="t('wiki.page.edit')"
          size="small"
          data-testid="edit-page"
        />
        <Button
          v-if="discussable"
          :icon="watching ? 'pi pi-eye' : 'pi pi-eye-slash'"
          :label="watching ? t('comments.watch.watching') : t('comments.watch.watch')"
          :aria-pressed="watching"
          :loading="watchBusy"
          size="small"
          severity="secondary"
          outlined
          data-testid="watch"
          @click="toggleWatch"
        />
        <Button
          v-if="page.rights.canCreateChild"
          as="router-link"
          :to="{ name: 'page-create', params: { key: page.spaceKey }, query: { parentId: page.id } }"
          icon="pi pi-plus"
          :label="t('wiki.page.createChild')"
          size="small"
          severity="secondary"
        />
        <Button v-if="page.contentType === 'page'" icon="pi pi-bookmark" :label="t('prompts.page.button')" size="small" severity="secondary" outlined data-testid="page-prompts-button" @click="promptsOpen = true" />
        <template v-if="actions.length">
          <Button icon="pi pi-ellipsis-h" text rounded size="small" :aria-label="t('wiki.page.more')" aria-haspopup="true" aria-controls="page-actions" @click="(e) => menu?.toggle(e)" />
          <Menu id="page-actions" ref="menu" :model="actions" popup />
        </template>
      </div>
    </header>

    <p class="meta muted">
      <Tag v-if="page.isRestricted" severity="warn" icon="pi pi-lock" :value="t('wiki.page.restricted')" />
      <Tag v-if="page.status === 'Archived'" severity="secondary" :value="t('wiki.page.archived')" />
      <Tag v-if="page.status === 'Draft'" severity="info" :value="t('wiki.draft.unpublished')" />
      <UserAvatar :user-id="page.updatedBy" :name="page.updatedByName" :size="22" />
      <span>{{ t('wiki.page.updatedBy', { name: page.updatedByName ?? t('wiki.unknownUser'), date: updated }) }}</span>
      <span v-if="page.contentType === 'page' && page.version > 0">· <RouterLink :to="{ name: 'page-history', params: { key: page.spaceKey, id: page.id } }">{{ t('wiki.page.versionN', { n: page.version }) }}</RouterLink></span>
    </p>

    <Message v-if="page.status === 'Archived'" severity="info" :closable="false">{{ t('wiki.page.archivedNotice') }}</Message>
    <Message v-if="page.status === 'Draft'" severity="info" :closable="false">{{ t('wiki.draft.unpublishedPage') }}</Message>
    <Message v-else-if="page.hasDraft" severity="secondary" :closable="false" data-testid="has-draft">
      {{ t('wiki.draft.pendingChanges', { time: page.draftUpdatedAt ? formatDate(page.draftUpdatedAt, locale.locale, { dateStyle: 'medium', timeStyle: 'short' }) : '' }) }}
      <RouterLink :to="{ name: 'page-edit', params: { key: page.spaceKey, id: page.id } }">{{ t('wiki.draft.continue') }}</RouterLink>
    </Message>
    <Message v-if="page.scheduledPublishAt" severity="info" :closable="false">{{ t('wiki.publish.scheduledFor', { time: formatDate(page.scheduledPublishAt, locale.locale, { dateStyle: 'medium', timeStyle: 'short' }), name: '' }) }}</Message>
    <Message v-if="page.pendingApproval" severity="warn" :closable="false" data-testid="pending-approval">{{ t('wiki.publish.pendingShort') }}</Message>

    <PageAssistant v-if="page.contentType === 'page' && page.status === 'Current'" :page-id="page.id" />

    <DocumentBody
      v-if="page.contentType === 'page'"
      :doc="page.body"
      :anchors="discussable ? anchors : null"
      :active-anchor="activeThread"
      @quote="onQuote"
      @activate="(id) => (activeThread = id)"
      @located="(ids) => (located = ids)"
    />
    <PagePromptsDialog v-if="page.contentType === 'page'" v-model:visible="promptsOpen" :page-id="page.id" :space-key="page.spaceKey" :selected-text="selection?.exact ?? null" @insert="insertIntoPage" />
    <div v-if="selection && discussable && (page.rights.canComment || page.rights.canEdit)" class="selection-bar" role="region" :aria-label="t('comments.selection')">
      <Button v-if="page.rights.canComment" size="small" icon="pi pi-comment" :label="t('comments.commentOnSelection')" data-testid="comment-selection" @click="commentOnSelection" />
      <Button v-if="page.rights.canEdit" size="small" icon="pi pi-ticket" :label="t('jiraLinks.create')" data-testid="jira-selection-create" @click="createJiraFromSelection" />
      <Button v-if="page.rights.canEdit" size="small" text icon="pi pi-link" :label="t('jiraLinks.linkExisting')" data-testid="jira-selection-link" @click="linkJiraFromSelection" />
      <span class="muted" dir="auto">“{{ selection.exact.length > 80 ? selection.exact.slice(0, 80) + '…' : selection.exact }}”</span>
    </div>

    <section v-if="children.length" class="children">
      <h2>{{ t('wiki.page.children') }}</h2>
      <ul>
        <li v-for="c in children" :key="c.id">
          <i :class="c.contentType === 'folder' ? 'pi pi-folder' : 'pi pi-file'" aria-hidden="true" />
          <RouterLink :to="{ name: 'page', params: { key: page.spaceKey, id: c.id, slug: c.slug || undefined } }">{{ c.title }}</RouterLink>
        </li>
      </ul>
    </section>

    <section class="labels" :aria-label="t('wiki.labels.title')">
      <i class="pi pi-tags muted" aria-hidden="true" />
      <span v-if="page.labels.length === 0" class="muted">{{ t('wiki.labels.none') }}</span>
      <span v-for="l in page.labels" :key="l" class="label">
        <RouterLink :to="{ name: 'label', params: { name: l } }">{{ l }}</RouterLink>
        <Button v-if="page.rights.canEdit" text rounded size="small" icon="pi pi-times" :aria-label="t('wiki.labels.remove', { name: l })" @click="removeLabel(l)" />
      </span>
      <form v-if="page.rights.canEdit" class="add-label" @submit.prevent="() => addLabel()">
        <label for="new-label" class="sr-only">{{ t('wiki.labels.add') }}</label>
        <AutoComplete v-model="newLabel" input-id="new-label" :suggestions="labelSuggestions" :placeholder="t('wiki.labels.add')" size="small" @complete="suggest" />
        <Button type="submit" size="small" icon="pi pi-plus" :aria-label="t('wiki.labels.add')" />
      </form>
      <div v-if="page.rights.canEdit && pageLabelSuggestions.length" class="suggestions" data-testid="label-suggestions">
        <span class="muted">{{ t('wiki.labels.suggestions') }}:</span>
        <Button
          v-for="s in pageLabelSuggestions"
          :key="s.slug"
          size="small"
          text
          :aria-label="s.source === 'ai' ? t('wiki.labels.aiSuggestion', { name: s.displayName }) : t('wiki.labels.applySuggestion', { name: s.displayName })"
          @click="addLabel(s.slug)"
        >
          {{ s.displayName }}
          <Tag v-if="s.source === 'ai'" class="ai-badge" severity="info" :value="t('wiki.labels.aiBadge')" />
        </Button>
      </div>
    </section>

    <GovernancePanel v-if="page.governance" :page="page" :can-edit="page.rights.canEdit" @updated="(p) => (page = p)" />
    <RelationshipsPanel :content-id="page.id" :can-edit="page.rights.canEdit" />

    <PageApprovals v-if="discussable" :content-id="page.id" :can-request="page.rights.canEdit" />

    <LinkedJiraPanel
      v-if="discussable"
      ref="jiraPanel"
      :content-id="page.id"
      :can-edit="page.rights.canEdit"
      :pending-quote="jiraQuote()"
      @locate="locateQuote"
      @clear-quote="pendingQuote = null; selection = null"
    />

    <CommentsPanel
      v-if="discussable"
      v-model:pending-quote="pendingQuote"
      v-model:active="activeThread"
      :content-id="page.id"
      :located="located"
      @anchors="(a) => (anchors = a)"
      @watching="(w) => (watching = w)"
    />

    <MoveDialog v-if="page.rights.canMove" v-model:visible="moveOpen" :page="page" :etag="etag" @moved="onMoved" />
    <RestrictionsDialog v-if="page.rights.canRestrict" v-model:visible="restrictOpen" :content-id="page.id" @saved="load" />
  </article>
</template>

<style scoped>
.page { display: grid; gap: var(--raavi-space-3); max-inline-size: 60rem; }
.crumbs ol { list-style: none; display: flex; flex-wrap: wrap; gap: var(--raavi-space-1); margin: 0; padding: 0; font-size: 0.9rem; }
.crumbs li + li::before { content: '/'; margin-inline-end: var(--raavi-space-1); color: var(--raavi-text-muted); }
.head { display: flex; align-items: flex-start; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; }
h1 { margin: 0; font-size: 1.8rem; overflow-wrap: anywhere; }
.tools { display: flex; gap: var(--raavi-space-2); align-items: center; }
.meta { display: flex; flex-wrap: wrap; gap: var(--raavi-space-2); align-items: center; margin: 0; font-size: 0.9rem; }
.muted { color: var(--raavi-text-muted); }
.children h2 { font-size: 1rem; margin: 0 0 var(--raavi-space-2); }
.children ul { list-style: none; padding: 0; margin: 0; display: grid; gap: var(--raavi-space-1); }
.children li { display: flex; gap: var(--raavi-space-2); align-items: center; }
.labels { display: flex; flex-wrap: wrap; align-items: center; gap: var(--raavi-space-2); border-block-start: 1px solid var(--raavi-border); padding-block-start: var(--raavi-space-3); }
.label { display: inline-flex; align-items: center; padding-inline: var(--raavi-space-2); border: 1px solid var(--raavi-border); border-radius: 999px; }
.add-label { display: inline-flex; gap: var(--raavi-space-1); }
.suggestions { display: inline-flex; flex-wrap: wrap; align-items: center; gap: var(--raavi-space-1); }
.suggestions :deep(.ai-badge) { margin-inline-start: var(--raavi-space-1); font-size: 0.7rem; }
.history { padding-inline-start: var(--raavi-space-4); display: grid; gap: var(--raavi-space-2); }
.history li { display: grid; }
.selection-bar { position: sticky; inset-block-end: var(--raavi-space-3); z-index: 6; display: flex; gap: var(--raavi-space-2); align-items: center; padding: var(--raavi-space-2); background: var(--raavi-surface, #fff); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); box-shadow: 0 4px 16px rgb(0 0 0 / 0.12); overflow: hidden; }
.selection-bar .muted { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.missing { display: grid; gap: var(--raavi-space-2); }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
</style>
