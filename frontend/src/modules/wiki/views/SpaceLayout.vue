<script setup lang="ts">
import { provide, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import { ApiError } from '@/core/api/http'
import { wikiApi, type Space, type TreeNode } from '@/core/api/wiki'
import PageTree from '../components/PageTree.vue'
import { SPACE_CONTEXT } from '../spaceContext'

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

function open(node: TreeNode) {
  void router.push({ name: 'page', params: { key: String(route.params.key), id: node.id, slug: node.slug || undefined } })
}
</script>

<template>
  <div v-if="notFound" class="missing">
    <h1>{{ t('wiki.space.notFound') }}</h1>
    <RouterLink :to="{ name: 'spaces' }">{{ t('wiki.home.allSpaces') }}</RouterLink>
  </div>
  <div v-else-if="space" class="space-layout" :class="{ collapsed: !sidebarOpen }">
    <aside class="sidebar" :aria-label="t('wiki.space.sidebar')">
      <div class="side-head">
        <RouterLink :to="{ name: 'space', params: { key: space.key } }" class="space-name">{{ space.name }}</RouterLink>
        <Tag v-if="space.status === 'Archived'" severity="secondary" :value="t('wiki.spaces.archived')" />
      </div>
      <nav class="side-actions">
        <Button v-if="space.rights.canCreate" as="router-link" :to="{ name: 'page-create', params: { key: space.key } }" size="small" icon="pi pi-plus" :label="t('wiki.page.create')" data-testid="create-page" />
        <Button as="router-link" :to="{ name: 'space-calendar', params: { key: space.key } }" size="small" text icon="pi pi-calendar" :label="t('calendar.nav')" data-testid="space-calendar-link" />
        <Button as="router-link" :to="{ name: 'space-tasks', params: { key: space.key } }" size="small" text icon="pi pi-check-square" :label="t('tasks.nav')" data-testid="space-tasks-link" />
        <Button v-if="space.rights.canAdminister" as="router-link" :to="{ name: 'space-settings', params: { key: space.key } }" size="small" text icon="pi pi-cog" :label="t('wiki.space.settings')" />
      </nav>
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
</template>

<style scoped>
.space-layout { display: grid; grid-template-columns: 18rem auto minmax(0, 1fr); gap: var(--raavi-space-3); align-items: start; max-inline-size: 90rem; margin-inline: auto; }
.space-layout.collapsed { grid-template-columns: 0 auto minmax(0, 1fr); }
.space-layout.collapsed .sidebar { visibility: hidden; overflow: hidden; }
.sidebar { position: sticky; inset-block-start: calc(var(--raavi-header-height) + var(--raavi-space-4)); max-block-size: calc(100vh - var(--raavi-header-height) - 2 * var(--raavi-space-4)); overflow: auto; display: grid; gap: var(--raavi-space-3); padding-inline-end: var(--raavi-space-2); border-inline-end: 1px solid var(--raavi-border); }
.side-head { display: flex; align-items: center; gap: var(--raavi-space-2); }
.space-name { font-weight: 700; font-size: 1.1rem; text-decoration: none; color: inherit; overflow-wrap: anywhere; }
.side-actions { display: flex; flex-wrap: wrap; gap: var(--raavi-space-2); }
.side-title { margin: 0; font-size: 0.85rem; color: var(--raavi-text-muted); text-transform: uppercase; }
.toggle { position: sticky; inset-block-start: calc(var(--raavi-header-height) + var(--raavi-space-4)); }
.content { min-inline-size: 0; }
.missing { text-align: center; display: grid; gap: var(--raavi-space-3); }
@media (max-width: 900px) {
  .space-layout { grid-template-columns: minmax(0, 1fr); }
  .sidebar { position: static; max-block-size: none; border-inline-end: 0; border-block-end: 1px solid var(--raavi-border); padding-block-end: var(--raavi-space-3); }
  .toggle { display: none; }
  .space-layout.collapsed .sidebar { visibility: visible; }
}
</style>
