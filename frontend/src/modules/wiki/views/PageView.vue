<script setup lang="ts">
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
import CommentsPanel from '../comments/CommentsPanel.vue'
import PageApprovals from '@/modules/approvals/PageApprovals.vue'
import PageAssistant from '@/modules/ai/PageAssistant.vue'
import { commentsApi } from '@/core/api/comments'
import type { AnchorHighlight, TextQuote } from '@/modules/editor/extensions/commentAnchors'

const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()
const ctx = useSpaceContext()

const page = ref<Page | null>(null)
const etag = ref<string | null>(null)
const children = ref<TreeNode[]>([])
const notFound = ref(false)
const moveOpen = ref(false)
const restrictOpen = ref(false)
const newLabel = ref('')
const labelSuggestions = ref<string[]>([])
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
    children.value = r.value.status === 'Current' ? await wikiApi.tree(r.value.spaceKey, r.value.id).catch(() => []) : []
    // A page moved to another space is shown under its new space.
    if (r.value.spaceKey !== route.params.key) await router.replace({ name: 'page', params: { key: r.value.spaceKey, id: r.value.id, slug: r.value.slug || undefined } })
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

async function addLabel() {
  const name = newLabel.value.trim()
  if (!page.value || !name) return
  try {
    page.value = { ...page.value, labels: await wikiApi.addLabel(page.value.id, name) }
    newLabel.value = ''
  } catch (e) {
    fail(e)
  }
}

async function removeLabel(name: string) {
  if (!page.value) return
  try {
    page.value = { ...page.value, labels: await wikiApi.removeLabel(page.value.id, name) }
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
    <nav class="crumbs" :aria-label="t('wiki.page.breadcrumbs')">
      <ol>
        <li><RouterLink :to="{ name: 'space', params: { key: page.spaceKey } }">{{ page.spaceName }}</RouterLink></li>
        <li v-for="c in page.breadcrumbs" :key="c.id"><RouterLink :to="{ name: 'page', params: { key: page.spaceKey, id: c.id } }">{{ c.title }}</RouterLink></li>
      </ol>
    </nav>

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
    <div v-if="selection && page.rights.canComment && discussable" class="selection-bar" role="region" :aria-label="t('comments.selection')">
      <Button size="small" icon="pi pi-comment" :label="t('comments.commentOnSelection')" data-testid="comment-selection" @click="commentOnSelection" />
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
      <form v-if="page.rights.canEdit" class="add-label" @submit.prevent="addLabel">
        <label for="new-label" class="sr-only">{{ t('wiki.labels.add') }}</label>
        <AutoComplete v-model="newLabel" input-id="new-label" :suggestions="labelSuggestions" :placeholder="t('wiki.labels.add')" size="small" @complete="suggest" />
        <Button type="submit" size="small" icon="pi pi-plus" :aria-label="t('wiki.labels.add')" />
      </form>
    </section>

    <PageApprovals v-if="discussable" :content-id="page.id" :can-request="page.rights.canEdit" />

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
.history { padding-inline-start: var(--raavi-space-4); display: grid; gap: var(--raavi-space-2); }
.history li { display: grid; }
.selection-bar { position: sticky; inset-block-end: var(--raavi-space-3); z-index: 6; display: flex; gap: var(--raavi-space-2); align-items: center; padding: var(--raavi-space-2); background: var(--raavi-surface, #fff); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); box-shadow: 0 4px 16px rgb(0 0 0 / 0.12); overflow: hidden; }
.selection-bar .muted { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.missing { display: grid; gap: var(--raavi-space-2); }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
</style>
