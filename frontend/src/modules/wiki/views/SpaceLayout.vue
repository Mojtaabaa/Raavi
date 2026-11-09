<script setup lang="ts">
import { onBeforeUnmount, provide, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { ApiError } from '@/core/api/http'
import { wikiApi, type Space, type TreeNode } from '@/core/api/wiki'
import PageTree from '../components/PageTree.vue'
import { SPACE_CONTEXT } from '../spaceContext'
import { useCrumbsStore } from '@/stores/crumbs'
import { spaceTrail } from '@/core/navigation/trail'

/** Space chrome: sidebar with the page tree and space actions; child routes render the overview, pages and settings. */
const { t } = useI18n()
const route = useRoute()
const router = useRouter()

const space = ref<Space | null>(null)
const etag = ref<string | null>(null)
const treeVersion = ref(0)
const activePath = ref<string[]>([])
const notFound = ref(false)
const sidebarOpen = ref(true)

async function reloadSpace() {
  try {
    const r = await wikiApi.space(String(route.params.key))
    space.value = r.value
    etag.value = r.etag
    notFound.value = false
  } catch (e) {
    if (e instanceof ApiError && e.status === 404) {
      space.value = null
      notFound.value = true
    } else throw e
  }
}

provide(SPACE_CONTEXT, { space, etag, treeVersion, activePath, reloadSpace, reloadTree: () => treeVersion.value++ })

watch(() => route.params.key, reloadSpace, { immediate: true })

// Trail for the views of the space that do not set their own (overview and the section pages).
const crumbs = useCrumbsStore()
const SECTIONS: Record<string, string> = { 'space-workflows': 'workflows.nav', 'space-whiteboards': 'whiteboards.nav', 'space-slides': 'slides.nav', 'space-databases': 'databases.nav', 'space-calendar': 'calendar.nav', 'space-tasks': 'tasks.nav', 'space-settings': 'wiki.space.settings' }
watch(
  () => [space.value?.key, space.value?.name, route.name] as const,
  () => {
    const s = space.value
    const name = String(route.name ?? '')
    if (!s || !(name === 'space' || name in SECTIONS)) return
    crumbs.set(spaceTrail(s, t, ...(name in SECTIONS ? [{ label: t(SECTIONS[name]!) }] : [])))
  },
  { immediate: true },
)
onBeforeUnmount(() => crumbs.clear())

function open(node: TreeNode) {
  void router.push({ name: 'page', params: { key: String(route.params.key), id: node.id, slug: node.slug || undefined } })
}
</script>

<template>
  <div v-if="notFound" class="missing">
    <h1>{{ t('wiki.space.notFound') }}</h1>
    <RouterLink :to="{ name: 'spaces' }">{{ t('wiki.home.allSpaces') }}</RouterLink>
  </div>
  <div v-else-if="space" class="space-shell">
    <!-- Space menu: one horizontal bar above the tree and the page (scrolls sideways on narrow screens). -->
    <header class="space-bar">
      <div class="side-head">
        <RouterLink :to="{ name: 'space', params: { key: space.key } }" class="space-name">{{ space.name }}</RouterLink>
        <Tag v-if="space.status === 'Archived'" severity="secondary" :value="t('wiki.spaces.archived')" />
      </div>
      <nav class="space-menu" :aria-label="t('wiki.space.menu')">
        <RouterLink :to="{ name: 'space-whiteboards', params: { key: space.key } }" class="menu-link" active-class="active" data-testid="space-whiteboards-link"><i class="pi pi-palette" aria-hidden="true" /> {{ t('whiteboards.nav') }}</RouterLink>
        <RouterLink :to="{ name: 'space-slides', params: { key: space.key } }" class="menu-link" active-class="active" data-testid="space-slides-link"><i class="pi pi-desktop" aria-hidden="true" /> {{ t('slides.nav') }}</RouterLink>
        <RouterLink :to="{ name: 'space-databases', params: { key: space.key } }" class="menu-link" active-class="active" data-testid="space-databases-link"><i class="pi pi-table" aria-hidden="true" /> {{ t('databases.nav') }}</RouterLink>
        <RouterLink :to="{ name: 'space-calendar', params: { key: space.key } }" class="menu-link" active-class="active" data-testid="space-calendar-link"><i class="pi pi-calendar" aria-hidden="true" /> {{ t('calendar.nav') }}</RouterLink>
        <RouterLink :to="{ name: 'space-tasks', params: { key: space.key } }" class="menu-link" active-class="active" data-testid="space-tasks-link"><i class="pi pi-check-square" aria-hidden="true" /> {{ t('tasks.nav') }}</RouterLink>
        <RouterLink v-if="space.rights.canAdminister" :to="{ name: 'space-workflows', params: { key: space.key } }" class="menu-link" active-class="active" data-testid="space-workflows-link"><i class="pi pi-bolt" aria-hidden="true" /> {{ t('workflows.nav') }}</RouterLink>
        <RouterLink v-if="space.rights.canAdminister" :to="{ name: 'space-settings', params: { key: space.key } }" class="menu-link" active-class="active"><i class="pi pi-cog" aria-hidden="true" /> {{ t('wiki.space.settings') }}</RouterLink>
      </nav>
      <Button v-if="space.rights.canCreate" as="router-link" :to="{ name: 'page-create', params: { key: space.key } }" size="small" icon="pi pi-plus" :label="t('wiki.page.create')" class="create" data-testid="create-page" />
    </header>
    <div class="space-layout" :class="{ collapsed: !sidebarOpen }">
    <aside class="sidebar" :aria-label="t('wiki.space.sidebar')">
      <h2 class="side-title">{{ t('wiki.tree.title') }}</h2>
      <PageTree v-if="space.rights.canView" :space-key="space.key" :version="treeVersion" :expand-path="activePath" :selected-id="String(route.params.id ?? '')" @select="open" />
    </aside>
    <Button
      class="toggle"
      text
      rounded
      size="small"
      :icon="sidebarOpen ? 'pi pi-angle-double-left' : 'pi pi-angle-double-right'"
      :aria-label="sidebarOpen ? t('wiki.space.hideSidebar') : t('wiki.space.showSidebar')"
      :aria-expanded="sidebarOpen"
      @click="sidebarOpen = !sidebarOpen"
    />
    <div class="content">
      <RouterView :key="String(route.params.id ?? route.name)" />
    </div>
    </div>
  </div>
</template>

<style scoped>
.space-shell { display: grid; gap: var(--raavi-space-3); max-inline-size: 90rem; margin-inline: auto; }
.space-bar { display: flex; align-items: center; gap: var(--raavi-space-3); padding-block-end: var(--raavi-space-2); border-block-end: 1px solid var(--raavi-border); min-inline-size: 0; }
.space-menu { display: flex; gap: var(--raavi-space-1); flex: 1; min-inline-size: 0; overflow-x: auto; scrollbar-width: thin; }
.menu-link { display: inline-flex; align-items: center; gap: var(--raavi-space-1); white-space: nowrap; padding: var(--raavi-space-1) var(--raavi-space-3); border-radius: var(--raavi-radius-sm); color: var(--raavi-text-muted); text-decoration: none; font-size: 0.92rem; }
.menu-link:hover, .menu-link.active { background: var(--p-primary-50, rgba(0, 0, 0, 0.05)); color: var(--p-primary-color); }
.create { flex: none; }
.space-layout { display: grid; grid-template-columns: 18rem auto minmax(0, 1fr); gap: var(--raavi-space-3); align-items: start; }
.space-layout.collapsed { grid-template-columns: 0 auto minmax(0, 1fr); }
.space-layout.collapsed .sidebar { visibility: hidden; overflow: hidden; }
.sidebar { position: sticky; inset-block-start: calc(var(--raavi-header-offset) + var(--raavi-space-4)); max-block-size: calc(100vh - var(--raavi-header-offset) - 2 * var(--raavi-space-4)); overflow: auto; display: grid; gap: var(--raavi-space-3); padding-inline-end: var(--raavi-space-2); border-inline-end: 1px solid var(--raavi-border); }
.side-head { display: flex; align-items: center; gap: var(--raavi-space-2); }
.space-name { font-weight: 700; font-size: 1.1rem; text-decoration: none; color: inherit; overflow-wrap: anywhere; }
.side-actions { display: flex; flex-wrap: wrap; gap: var(--raavi-space-2); }
.side-title { margin: 0; font-size: 0.85rem; color: var(--raavi-text-muted); text-transform: uppercase; }
.toggle { position: sticky; inset-block-start: calc(var(--raavi-header-offset) + var(--raavi-space-4)); }
.content { min-inline-size: 0; }
.missing { text-align: center; display: grid; gap: var(--raavi-space-3); }
@media (max-width: 700px) {
  .space-bar { flex-wrap: wrap; }
  .space-menu { order: 3; flex-basis: 100%; }
}
@media (max-width: 900px) {
  .space-layout { grid-template-columns: minmax(0, 1fr); }
  .sidebar { position: static; max-block-size: none; border-inline-end: 0; border-block-end: 1px solid var(--raavi-border); padding-block-end: var(--raavi-space-3); }
  .toggle { display: none; }
  .space-layout.collapsed .sidebar { visibility: visible; }
}
</style>
