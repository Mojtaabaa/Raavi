<script setup lang="ts">
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import type { PromptCategory } from '@/core/api/prompts'

/**
 * The prompt category tree: expand/collapse, search, counts, and (for people who may manage categories) add / rename / delete and drag-and-drop.
 * Dropping on the upper quarter of a row puts the category before it, on the lower quarter after it, in the middle makes it a child.
 * The server checks everything again (cycles, depth, rights); the tree only offers the gestures.
 */
const props = defineProps<{ categories: PromptCategory[]; selected: string | null; scope: 'mine' | 'public'; canManage: boolean }>()
const emit = defineEmits<{
  select: [id: string | null]
  add: [parentId: string | null]
  rename: [category: PromptCategory]
  remove: [category: PromptCategory]
  move: [id: string, parentId: string | null, index: number]
}>()
const { t } = useI18n()

const collapsed = ref(new Set<string>())
const filter = ref('')
const dragging = ref<string | null>(null)
const dropHint = ref<{ id: string; where: 'before' | 'inside' | 'after' } | null>(null)

const children = (parent: string | null) => props.categories.filter((c) => c.parentId === parent).sort((a, b) => a.sortOrder - b.sortOrder || a.name.localeCompare(b.name))
const count = (c: PromptCategory) => (props.scope === 'mine' ? c.minePrompts : c.publicPrompts)

/** With a filter: the matching categories and their ancestors stay visible. */
const visibleIds = computed(() => {
  const q = filter.value.trim().toLowerCase()
  if (!q) return null
  const keep = new Set<string>()
  for (const c of props.categories) {
    if (c.name.toLowerCase().includes(q)) {
      let cur: PromptCategory | undefined = c
      while (cur) {
        keep.add(cur.id)
        cur = props.categories.find((x) => x.id === cur!.parentId)
      }
    }
  }
  return keep
})

interface Row { cat: PromptCategory; depth: number; hasChildren: boolean }
const rows = computed<Row[]>(() => {
  const out: Row[] = []
  const walk = (parent: string | null, depth: number) => {
    for (const c of children(parent)) {
      if (visibleIds.value && !visibleIds.value.has(c.id)) continue
      const kids = children(c.id)
      out.push({ cat: c, depth, hasChildren: kids.length > 0 })
      if (!collapsed.value.has(c.id) || visibleIds.value) walk(c.id, depth + 1)
    }
  }
  walk(null, 0)
  return out
})

function toggle(id: string) {
  const next = new Set(collapsed.value)
  if (!next.delete(id)) next.add(id)
  collapsed.value = next
}

function isDescendant(id: string, maybeAncestor: string): boolean {
  let cur = props.categories.find((c) => c.id === id)
  while (cur) {
    if (cur.parentId === maybeAncestor) return true
    cur = props.categories.find((c) => c.id === cur!.parentId)
  }
  return false
}

function onDragOver(e: DragEvent, row: Row) {
  if (!props.canManage || !dragging.value || dragging.value === row.cat.id || isDescendant(row.cat.id, dragging.value)) return
  e.preventDefault()
  const el = e.currentTarget as HTMLElement
  const rect = el.getBoundingClientRect()
  const y = (e.clientY - rect.top) / Math.max(rect.height, 1)
  dropHint.value = { id: row.cat.id, where: y < 0.25 ? 'before' : y > 0.75 ? 'after' : 'inside' }
}

function onDrop(e: DragEvent, row: Row) {
  e.preventDefault()
  const id = dragging.value
  const hint = dropHint.value
  dragging.value = null
  dropHint.value = null
  if (!id || !hint || id === row.cat.id) return
  if (hint.where === 'inside') {
    emit('move', id, row.cat.id, children(row.cat.id).length)
    return
  }
  const siblings = children(row.cat.parentId).filter((c) => c.id !== id)
  const at = siblings.findIndex((c) => c.id === row.cat.id)
  emit('move', id, row.cat.parentId, hint.where === 'before' ? at : at + 1)
}

function onRootDrop(e: DragEvent) {
  e.preventDefault()
  const id = dragging.value
  dragging.value = null
  dropHint.value = null
  if (id) emit('move', id, null, children(null).filter((c) => c.id !== id).length)
}
</script>

<template>
  <nav class="tree" :aria-label="t('prompts.categories.title')" data-testid="category-tree">
    <div class="tools">
      <InputText v-model="filter" size="small" :placeholder="t('prompts.categories.search')" :aria-label="t('prompts.categories.search')" data-testid="category-search" />
      <Button v-if="canManage" text size="small" icon="pi pi-plus" :aria-label="t('prompts.categories.add')" :title="t('prompts.categories.add')" data-testid="category-add-root" @click="emit('add', null)" />
    </div>
    <ul role="tree">
      <li role="treeitem" :aria-selected="selected === null">
        <button type="button" :class="['row', { active: selected === null }]" data-testid="category-all" @click="emit('select', null)">
          <i class="pi pi-folder-open" aria-hidden="true" /> <span class="name">{{ t('prompts.categories.all') }}</span>
        </button>
      </li>
      <li
        v-for="row in rows"
        :key="row.cat.id"
        role="treeitem"
        :aria-selected="selected === row.cat.id"
        :aria-expanded="row.hasChildren ? !collapsed.has(row.cat.id) : undefined"
        :data-testid="`category-${row.cat.name}`"
        :draggable="canManage"
        @dragstart="dragging = row.cat.id"
        @dragend="dragging = null; dropHint = null"
        @dragover="onDragOver($event, row)"
        @drop="onDrop($event, row)"
      >
        <div :class="['row', { active: selected === row.cat.id, archived: row.cat.status === 'Archived', [`drop-${dropHint?.id === row.cat.id ? dropHint.where : 'none'}`]: true }]" :style="{ paddingInlineStart: `${row.depth * 1.1 + 0.25}rem` }">
          <button v-if="row.hasChildren" type="button" class="twisty" :aria-label="collapsed.has(row.cat.id) ? t('prompts.categories.expand') : t('prompts.categories.collapse')" @click="toggle(row.cat.id)">
            <i :class="collapsed.has(row.cat.id) ? 'pi pi-angle-right' : 'pi pi-angle-down'" aria-hidden="true" />
          </button>
          <span v-else class="twisty" />
          <button type="button" class="label" @click="emit('select', row.cat.id)">
            <i class="pi pi-folder" aria-hidden="true" /> <span class="name">{{ row.cat.name }}</span>
            <span class="count" :data-testid="`count-${row.cat.name}`">{{ count(row.cat) }}</span>
          </button>
          <span v-if="canManage" class="row-actions">
            <Button text size="small" icon="pi pi-plus" :aria-label="t('prompts.categories.addChild')" @click="emit('add', row.cat.id)" />
            <Button text size="small" icon="pi pi-pencil" :aria-label="t('prompts.categories.rename')" :data-testid="`category-rename-${row.cat.name}`" @click="emit('rename', row.cat)" />
            <Button text size="small" severity="danger" icon="pi pi-trash" :aria-label="t('prompts.categories.delete')" :data-testid="`category-delete-${row.cat.name}`" @click="emit('remove', row.cat)" />
          </span>
        </div>
      </li>
    </ul>
    <div v-if="canManage" class="root-drop" data-testid="category-root-drop" @dragover.prevent @drop="onRootDrop">{{ t('prompts.categories.dropToRoot') }}</div>
    <p v-if="!categories.length" class="muted">{{ t('prompts.categories.empty') }}</p>
  </nav>
</template>

<style scoped>
.tree { display: grid; gap: var(--raavi-space-2); }
.tools { display: flex; gap: var(--raavi-space-1); align-items: center; }
.tools :first-child { flex: 1; }
ul { list-style: none; margin: 0; padding: 0; display: grid; gap: 1px; }
.row { display: flex; align-items: center; gap: 0.15rem; border-radius: var(--raavi-radius-sm); border: 1px solid transparent; padding-block: 0.1rem; }
.row.active { background: color-mix(in srgb, var(--p-primary-color) 14%, transparent); }
.row.archived { opacity: 0.6; }
.row.drop-inside { border-color: var(--p-primary-color); background: color-mix(in srgb, var(--p-primary-color) 10%, transparent); }
.row.drop-before { border-block-start-color: var(--p-primary-color); }
.row.drop-after { border-block-end-color: var(--p-primary-color); }
button { font: inherit; color: inherit; background: transparent; border: 0; cursor: pointer; }
.twisty { inline-size: 1.4rem; text-align: center; flex: none; }
.label, button.row { flex: 1; display: flex; align-items: center; gap: 0.4rem; text-align: start; padding: 0.2rem 0.3rem; min-inline-size: 0; }
button.row { inline-size: 100%; padding-inline-start: 1.65rem; }
.name { flex: 1; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.count { font-size: 0.75rem; color: var(--raavi-text-muted); }
.row-actions { display: none; flex: none; }
.row:hover .row-actions, .row:focus-within .row-actions { display: inline-flex; }
.root-drop { border: 1px dashed var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: 0.3rem; font-size: 0.8rem; text-align: center; color: var(--raavi-text-muted); }
.muted { color: var(--raavi-text-muted); margin: 0; font-size: 0.85rem; }
</style>
