<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import { useConfirm } from 'primevue/useconfirm'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import Paginator from 'primevue/paginator'
import Dialog from 'primevue/dialog'
import RadioButton from 'primevue/radiobutton'
import { PROMPT_CONTEXTS, promptsApi, type PromptCategory, type PromptSummary } from '@/core/api/prompts'
import { errorMessage } from '@/core/api/errors'
import { useAuthStore } from '@/stores/auth'
import CategoryTree from './CategoryTree.vue'
import PromptRunDialog from './PromptRunDialog.vue'
import PromptAnalytics from './PromptAnalytics.vue'
import { promptRunApi } from '@/core/api/prompts'

/** Prompt Hub: my prompts and the workspace's public prompts, with the category tree beside them (ADR-052). */
const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const toast = useToast()
const confirm = useConfirm()
const auth = useAuthStore()

const scope = ref<'mine' | 'public'>(route.query.scope === 'mine' ? 'mine' : 'public')
const items = ref<PromptSummary[]>([])
const total = ref(0)
const loading = ref(true)
const categories = ref<PromptCategory[]>([])
const category = ref<string | null>(null)
const q = ref('')
const context = ref<string | null>(null)
const sort = ref('newest')
const showArchived = ref(false)
const first = ref(0)
const pageSize = 20
const analyticsOpen = ref(false)
const canManage = computed(() => !!auth.me?.isTenantAdministrator)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
const scopeOptions = computed(() => [
  { value: 'mine', label: t('prompts.mine') },
  { value: 'public', label: t('prompts.public') },
])
const contextOptions = computed(() => [{ value: null, label: t('prompts.anyContext') }, ...PROMPT_CONTEXTS.map((c) => ({ value: c, label: t(`prompts.contexts.${c}`) }))])
const sortOptions = computed(() => (scope.value === 'public' ? ['newest', 'popular', 'rated', 'used', 'updated', 'title'] : ['newest', 'updated', 'title']).map((s) => ({ value: s, label: t(`prompts.sort.${s}`) })))

async function load() {
  loading.value = true
  try {
    const page = await promptsApi.list({
      scope: scope.value,
      q: q.value.trim() || undefined,
      categoryId: category.value,
      context: context.value ?? undefined,
      sort: sort.value,
      status: scope.value === 'mine' && showArchived.value ? 'Archived' : undefined,
      skip: first.value,
      take: pageSize,
    })
    items.value = page.items
    total.value = page.total
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}

async function loadCategories() {
  try {
    categories.value = await promptsApi.categories(true)
  } catch (e) {
    fail(e)
  }
}

let timer: number | undefined
watch(q, () => {
  window.clearTimeout(timer)
  timer = window.setTimeout(() => { first.value = 0; load() }, 300)
})
watch([scope, category, context, sort, showArchived], () => {
  first.value = 0
  load()
  router.replace({ query: { ...route.query, scope: scope.value } })
})
onMounted(() => Promise.all([load(), loadCategories()]))

// ── category management ──
const catDialog = ref<{ mode: 'add' | 'rename'; parentId: string | null; category?: PromptCategory } | null>(null)
const catName = ref('')
function openCat(mode: 'add' | 'rename', parentId: string | null, c?: PromptCategory) {
  catDialog.value = { mode, parentId, category: c }
  catName.value = c?.name ?? ''
}
async function saveCat() {
  const d = catDialog.value
  if (!d || !catName.value.trim()) return
  try {
    if (d.mode === 'add') await promptsApi.createCategory(catName.value.trim(), d.parentId)
    else await promptsApi.editCategory(d.category!.id, catName.value.trim(), d.category!.description)
    catDialog.value = null
    await loadCategories()
  } catch (e) {
    fail(e)
  }
}
async function moveCat(id: string, parentId: string | null, index: number) {
  try {
    categories.value = await promptsApi.moveCategory(id, parentId, index)
  } catch (e) {
    fail(e)
    await loadCategories()
  }
}

const deleting = ref<PromptCategory | null>(null)
const strategy = ref<'parent' | 'category' | 'uncategorized'>('parent')
const target = ref<string | null>(null)
const targets = computed(() => categories.value.filter((c) => c.id !== deleting.value?.id && c.status === 'Active').map((c) => ({ value: c.id, label: c.name })))
async function removeCat() {
  const c = deleting.value
  if (!c) return
  try {
    categories.value = await promptsApi.deleteCategory(c.id, strategy.value, strategy.value === 'category' ? target.value : null)
    deleting.value = null
    if (category.value === c.id) category.value = null
    await load()
  } catch (e) {
    fail(e)
  }
}

// ── prompt actions ──
const runTarget = ref<PromptSummary | null>(null)
const runOpen = ref(false)
function run(p: PromptSummary) {
  runTarget.value = p
  runOpen.value = true
}
async function fork(p: PromptSummary) {
  try {
    const copy = await promptRunApi.fork(p.id)
    toast.add({ severity: 'success', summary: t('prompts.forked'), life: 3000 })
    router.push({ name: 'prompt', params: { id: copy.prompt.id } })
  } catch (e) {
    fail(e)
  }
}
async function duplicate(p: PromptSummary) {
  try {
    const copy = await promptsApi.duplicate(p.id)
    router.push({ name: 'prompt', params: { id: copy.prompt.id } })
  } catch (e) {
    fail(e)
  }
}
async function setStatus(p: PromptSummary, status: 'Archived' | 'Published') {
  try {
    await promptsApi.setStatus(p.id, status)
    await Promise.all([load(), loadCategories()])
  } catch (e) {
    fail(e)
  }
}
function removeForever(p: PromptSummary) {
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('prompts.confirmDelete', { title: p.title }),
    icon: 'pi pi-exclamation-triangle',
    acceptProps: { severity: 'danger', label: t('common.delete') },
    rejectProps: { severity: 'secondary', outlined: true, label: t('common.cancel') },
    accept: async () => {
      try {
        await promptsApi.remove(p.id)
        await Promise.all([load(), loadCategories()])
      } catch (e) {
        fail(e)
      }
    },
  })
}
const statusSeverity = (s: string) => (s === 'Published' ? 'success' : s === 'Draft' ? 'secondary' : s === 'Deprecated' ? 'warn' : 'contrast')
</script>

<template>
  <section class="hub" data-testid="prompt-hub">
    <header class="head">
      <h1>{{ t('prompts.title') }}</h1>
      <div class="actions">
        <SelectButton v-model="scope" :options="scopeOptions" option-label="label" option-value="value" :allow-empty="false" :aria-label="t('prompts.title')" data-testid="prompt-scope" />
        <Button v-if="canManage" outlined icon="pi pi-chart-bar" :label="t('prompts.analytics.title')" :aria-pressed="analyticsOpen" data-testid="prompt-analytics-toggle" @click="analyticsOpen = !analyticsOpen" />
        <Button icon="pi pi-plus" :label="t('prompts.new')" data-testid="new-prompt" @click="router.push({ name: 'prompt-new' })" />
      </div>
    </header>

    <PromptAnalytics v-if="analyticsOpen && canManage" />
    <div class="layout">
      <aside>
        <CategoryTree
          :categories="categories.filter((c) => c.status === 'Active' || canManage)"
          :selected="category"
          :scope="scope"
          :can-manage="canManage"
          @select="category = $event"
          @add="openCat('add', $event)"
          @rename="openCat('rename', $event.parentId, $event)"
          @remove="deleting = $event; strategy = 'parent'; target = null"
          @move="moveCat"
        />
      </aside>

      <div class="main">
        <div class="filters">
          <InputText v-model="q" :placeholder="t('prompts.search')" :aria-label="t('prompts.search')" data-testid="prompt-search" />
          <Select v-model="context" :options="contextOptions" option-label="label" option-value="value" :aria-label="t('prompts.context')" />
          <Select v-model="sort" :options="sortOptions" option-label="label" option-value="value" :aria-label="t('prompts.sortBy')" />
          <label v-if="scope === 'mine'" class="check"><input v-model="showArchived" type="checkbox" data-testid="show-archived" /> {{ t('prompts.showArchived') }}</label>
        </div>

        <p v-if="loading" class="muted" role="status">{{ t('common.loading') }}</p>
        <p v-else-if="!items.length" class="empty" data-testid="prompt-empty">{{ scope === 'mine' ? t('prompts.emptyMine') : t('prompts.emptyPublic') }}</p>
        <ul v-else class="cards">
          <li v-for="p in items" :key="p.id" class="card" :data-testid="`prompt-${p.title}`">
            <div class="top">
              <RouterLink :to="{ name: 'prompt', params: { id: p.id } }" class="title">{{ p.title }}</RouterLink>
              <span class="badges">
                <Tag :severity="p.visibility === 'Tenant' ? 'info' : 'secondary'" :value="p.visibility === 'Tenant' ? t('prompts.visibility.Tenant') : t('prompts.visibility.Personal')" />
                <Tag :severity="statusSeverity(p.status)" :value="t(`prompts.status.${p.status}`)" />
              </span>
            </div>
            <p v-if="p.description" class="desc">{{ p.description }}</p>
            <p class="meta">
              <span v-if="p.ownerName">{{ p.ownerName }}</span>
              <span v-if="(p.ratingCount ?? 0) > 0" class="rating" :data-testid="`rating-${p.title}`">★ {{ p.ratingAverage }} ({{ p.ratingCount }})</span>
              <span v-if="p.uses" class="muted">{{ t('prompts.uses', { n: p.uses }) }}</span>
              <span v-for="c in p.contexts" :key="c" class="chip">{{ t(`prompts.contexts.${c}`) }}</span>
              <span v-for="tag in p.tags" :key="tag" class="chip tag">#{{ tag }}</span>
              <span v-if="p.forkedFromTitle" class="muted">{{ t('prompts.forkedFrom', { title: p.forkedFromTitle }) }}</span>
            </p>
            <div class="row-actions">
              <Button v-if="p.status !== 'Archived'" text size="small" icon="pi pi-play" :label="t('prompts.run.open')" :data-testid="`run-${p.title}`" @click="run(p)" />
              <Button v-if="!p.isOwner && p.visibility === 'Tenant'" text size="small" icon="pi pi-clone" :label="t('prompts.fork')" :data-testid="`fork-${p.title}`" @click="fork(p)" />
            </div>
            <div v-if="p.isOwner" class="row-actions">
              <Button text size="small" icon="pi pi-copy" :label="t('prompts.duplicate')" @click="duplicate(p)" />
              <Button v-if="p.status !== 'Archived'" text size="small" icon="pi pi-inbox" :label="t('prompts.archive')" :data-testid="`archive-${p.title}`" @click="setStatus(p, 'Archived')" />
              <Button v-else text size="small" icon="pi pi-replay" :label="t('prompts.restore')" @click="setStatus(p, 'Published')" />
              <Button v-if="p.status === 'Archived'" text size="small" severity="danger" icon="pi pi-trash" :label="t('common.delete')" @click="removeForever(p)" />
            </div>
          </li>
        </ul>
        <Paginator v-if="total > pageSize" v-model:first="first" :rows="pageSize" :total-records="total" @page="load" />
      </div>
    </div>

    <PromptRunDialog v-model:visible="runOpen" :prompt="runTarget" />

    <Dialog :visible="!!catDialog" modal :header="catDialog?.mode === 'add' ? t('prompts.categories.add') : t('prompts.categories.rename')" :style="{ inlineSize: 'min(26rem, 95vw)' }" @update:visible="(v) => !v && (catDialog = null)">
      <form class="form" @submit.prevent="saveCat">
        <label for="cat-name">{{ t('prompts.categories.name') }}</label>
        <InputText id="cat-name" v-model="catName" maxlength="120" autofocus data-testid="category-name" />
        <Button type="submit" :label="t('common.save')" :disabled="!catName.trim()" data-testid="category-save" />
      </form>
    </Dialog>

    <Dialog :visible="!!deleting" modal :header="t('prompts.categories.deleteTitle', { name: deleting?.name ?? '' })" :style="{ inlineSize: 'min(30rem, 95vw)' }" @update:visible="(v) => !v && (deleting = null)">
      <div v-if="deleting" class="form" data-testid="category-delete-dialog">
        <p>{{ t('prompts.categories.deleteIntro') }}</p>
        <label class="radio"><RadioButton v-model="strategy" value="parent" input-id="st-parent" /> {{ t('prompts.categories.toParent') }}</label>
        <label class="radio"><RadioButton v-model="strategy" value="category" input-id="st-cat" /> {{ t('prompts.categories.toCategory') }}</label>
        <Select v-if="strategy === 'category'" v-model="target" :options="targets" option-label="label" option-value="value" :placeholder="t('prompts.categories.pick')" />
        <label class="radio"><RadioButton v-model="strategy" value="uncategorized" input-id="st-none" /> {{ t('prompts.categories.toNone') }}</label>
        <div class="row">
          <Button severity="secondary" outlined :label="t('common.cancel')" @click="deleting = null" />
          <Button severity="danger" icon="pi pi-trash" :label="t('common.delete')" :disabled="strategy === 'category' && !target" data-testid="category-delete-confirm" @click="removeCat" />
        </div>
      </div>
    </Dialog>
  </section>
</template>

<style scoped>
.hub { display: grid; gap: var(--raavi-space-3); max-inline-size: 80rem; margin-inline: auto; }
.head { display: flex; justify-content: space-between; align-items: center; gap: var(--raavi-space-3); flex-wrap: wrap; }
h1 { margin: 0; font-size: 1.4rem; }
.actions { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.layout { display: grid; grid-template-columns: minmax(14rem, 18rem) 1fr; gap: var(--raavi-space-4); align-items: start; }
@media (max-width: 800px) { .layout { grid-template-columns: 1fr; } }
.filters { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; margin-block-end: var(--raavi-space-3); }
.filters > :first-child { flex: 1; min-inline-size: 12rem; }
.check { display: inline-flex; gap: 0.3rem; align-items: center; }
.cards { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.card { border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-md, 8px); padding: var(--raavi-space-3); background: var(--raavi-surface); display: grid; gap: var(--raavi-space-1); }
.top { display: flex; justify-content: space-between; gap: var(--raavi-space-2); align-items: start; flex-wrap: wrap; }
.title { font-weight: 600; font-size: 1.05rem; text-decoration: none; color: var(--p-primary-color); }
.badges { display: inline-flex; gap: 0.3rem; }
.desc { margin: 0; color: var(--raavi-text-muted); }
.meta { margin: 0; display: flex; gap: 0.4rem; flex-wrap: wrap; align-items: center; font-size: 0.85rem; }
.chip { border: 1px solid var(--raavi-border); border-radius: 999px; padding: 0 0.5rem; }
.chip.tag { color: var(--raavi-text-muted); }
.rating { color: var(--p-yellow-600, #ca8a04); font-weight: 600; }
.row-actions { display: flex; gap: 0.2rem; flex-wrap: wrap; }
.muted { color: var(--raavi-text-muted); }
.empty { padding: var(--raavi-space-6, 2rem); text-align: center; color: var(--raavi-text-muted); border: 1px dashed var(--raavi-border); border-radius: var(--raavi-radius-md, 8px); }
.form { display: grid; gap: var(--raavi-space-2); }
.radio { display: flex; gap: 0.5rem; align-items: center; }
.row { display: flex; gap: var(--raavi-space-2); justify-content: flex-end; }
</style>
