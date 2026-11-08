<script setup lang="ts">
import { computed, defineAsyncComponent, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import { whiteboardsApi, type BoardElement, type Whiteboard } from '@/core/api/whiteboards'
import { errorMessage } from '@/core/api/errors'
import { duplicate, FILLS, History, labelOf, MAX_ELEMENTS, PALETTE, patch, remove, reorder, STROKE_WIDTHS, type Tool } from './boardModel'

/**
 * One whiteboard (Phase 9e). The model (elements, selection, undo/redo) lives here; the Konva canvas draws it and reports gestures. The whole
 * board is saved as one document with one ETag. Everything that can be done on the canvas can also be done from the element list and the
 * inspector, which are plain HTML controls (keyboard and screen-reader friendly).
 */
const BoardCanvas = defineAsyncComponent(() => import('./BoardCanvas.vue').then((m) => m.default))

const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const toast = useToast()
const confirm = useConfirm()

const tools: { id: Tool; icon: string }[] = [
  { id: 'select', icon: 'pi pi-arrow-up-left' },
  { id: 'hand', icon: 'pi pi-arrows-alt' },
  { id: 'rect', icon: 'pi pi-stop' },
  { id: 'ellipse', icon: 'pi pi-circle' },
  { id: 'sticky', icon: 'pi pi-bookmark' },
  { id: 'text', icon: 'pi pi-align-left' },
  { id: 'arrow', icon: 'pi pi-arrow-right' },
  { id: 'pen', icon: 'pi pi-pencil' },
]

const id = computed(() => String(route.params.id))
const board = ref<Whiteboard | null>(null)
const title = ref('')
const elements = ref<BoardElement[]>([])
const selectedId = ref<string | null>(null)
const tool = ref<Tool>('select')
const stroke = ref<string>(PALETTE[0])
const fill = ref<string | null>(null)
const saved = ref('')
const saving = ref(false)
const history = new History()
const historyTick = ref(0) // re-evaluates canUndo/canRedo
const canvas = ref<{ zoomBy(f: number): void; resetView(): void; toPng(): string | null } | null>(null)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const canEdit = computed(() => !!board.value?.canEdit)
const selected = computed(() => elements.value.find((e) => e.id === selectedId.value) ?? null)
const hasText = computed(() => selected.value?.type === 'Text' || selected.value?.type === 'Sticky')
const snapshot = () => JSON.stringify({ title: title.value, elements: elements.value })
const dirty = computed(() => canEdit.value && snapshot() !== saved.value)
const canUndo = computed(() => (historyTick.value, history.canUndo))
const canRedo = computed(() => (historyTick.value, history.canRedo))

function apply(b: Whiteboard) {
  board.value = b
  title.value = b.title
  elements.value = b.elements
  if (!b.elements.some((e) => e.id === selectedId.value)) selectedId.value = null
  history.clear()
  historyTick.value++
  saved.value = snapshot()
}

async function load() {
  try {
    apply(await whiteboardsApi.get(id.value))
  } catch (e) {
    fail(e)
  }
}
onMounted(load)
watch(id, load)

/** Every model change goes through here so that it can be undone. */
function commit(next: BoardElement[]) {
  if (next === elements.value) return
  history.record(elements.value)
  elements.value = next
  historyTick.value++
}

function onCreate(el: BoardElement) {
  if (!canEdit.value || elements.value.length >= MAX_ELEMENTS) return
  commit([...elements.value, el])
  selectedId.value = el.id
  tool.value = 'select'
}

function onChange(elId: string, changes: Partial<BoardElement>) {
  if (canEdit.value) commit(patch(elements.value, elId, changes))
}

function setSelected(elId: string | null) {
  selectedId.value = elId
}

/** Colour choices change the selected element, or the default used for the next one. */
function pickStroke(color: string) {
  stroke.value = color
  if (selected.value && canEdit.value) onChange(selected.value.id, { stroke: color })
}

function pickFill(color: string | null) {
  fill.value = color
  if (selected.value && canEdit.value && selected.value.type !== 'Text' && selected.value.type !== 'Pen' && selected.value.type !== 'Arrow') onChange(selected.value.id, { fill: color })
}

function removeSelected() {
  if (!selected.value || !canEdit.value) return
  const doomed = selected.value.id
  commit(remove(elements.value, doomed))
  selectedId.value = null
}

function duplicateSelected() {
  if (!selected.value || !canEdit.value) return
  const result = duplicate(elements.value, selected.value.id)
  if (result.id) {
    commit(result.elements)
    selectedId.value = result.id
  }
}

function order(to: 'front' | 'back') {
  if (selected.value && canEdit.value) commit(reorder(elements.value, selected.value.id, to))
}

function undo() {
  const prev = history.undo(elements.value)
  if (prev) {
    elements.value = prev
    historyTick.value++
  }
}

function redo() {
  const next = history.redo(elements.value)
  if (next) {
    elements.value = next
    historyTick.value++
  }
}

async function save() {
  if (!board.value || !dirty.value) return
  saving.value = true
  try {
    apply(await whiteboardsApi.update(board.value, { title: title.value.trim(), elements: elements.value }))
    toast.add({ severity: 'success', summary: t('common.saved'), life: 2000 })
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}

function exportPng() {
  const url = canvas.value?.toPng()
  if (!url) {
    toast.add({ severity: 'info', summary: t('whiteboards.emptyExport'), life: 3000 })
    return
  }

  const a = document.createElement('a')
  a.href = url
  a.download = `${(title.value || 'whiteboard').replace(/[^\p{L}\p{N}_-]+/gu, '-')}.png`
  a.click()
}

function removeBoard() {
  const b = board.value
  if (!b) return
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('whiteboards.confirmDelete', { name: b.title }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await whiteboardsApi.remove(b.id)
        saved.value = snapshot()
        await router.push({ name: 'space-whiteboards', params: { key: b.spaceKey } })
      } catch (e) {
        fail(e)
      }
    },
  })
}

function onKey(event: KeyboardEvent) {
  const target = event.target as HTMLElement | null
  if (target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return
  const mod = event.ctrlKey || event.metaKey
  if (mod && event.key.toLowerCase() === 'z') (event.shiftKey ? redo() : undo())
  else if (mod && event.key.toLowerCase() === 'y') redo()
  else if (mod && event.key.toLowerCase() === 'd') duplicateSelected()
  else if (event.key === 'Delete' || event.key === 'Backspace') removeSelected()
  else if (event.key === 'Escape') {
    tool.value = 'select'
    selectedId.value = null
  } else return
  event.preventDefault()
}

onBeforeRouteLeave(() => !dirty.value || window.confirm(t('whiteboards.unsaved')))
</script>

<template>
  <section v-if="board" class="board-page" data-testid="whiteboard" @keydown="onKey">
    <header class="head">
      <div class="titles">
        <RouterLink :to="{ name: 'space-whiteboards', params: { key: board.spaceKey } }" class="muted">{{ board.spaceName }}</RouterLink>
        <InputText v-if="canEdit" v-model="title" maxlength="200" class="board-title" dir="auto" :aria-label="t('whiteboards.boardTitle')" data-testid="board-title-input" />
        <h1 v-else dir="auto">{{ board.title }}</h1>
      </div>
      <div class="tools">
        <Button v-if="canEdit" icon="pi pi-save" :label="t('common.save')" :loading="saving" :disabled="!dirty || !title.trim()" data-testid="save-board" @click="save" />
        <Button icon="pi pi-image" text :label="t('whiteboards.exportPng')" data-testid="export-png" @click="exportPng" />
        <Button as="a" :href="whiteboardsApi.exportUrl(board.id)" icon="pi pi-download" text :label="t('whiteboards.exportSvg')" :disabled="dirty" data-testid="export-svg" />
      </div>
    </header>
    <p v-if="dirty" class="muted" role="status" data-testid="unsaved">{{ t('whiteboards.exportAfterSave') }}</p>

    <div v-if="canEdit" class="toolbar" role="toolbar" :aria-label="t('whiteboards.tools')">
      <Button v-for="tl in tools" :key="tl.id" :icon="tl.icon" :severity="tool === tl.id ? 'primary' : 'secondary'" :outlined="tool !== tl.id" size="small" :aria-label="t(`whiteboards.toolNames.${tl.id}`)" :aria-pressed="tool === tl.id" :title="t(`whiteboards.toolNames.${tl.id}`)" :data-testid="`tool-${tl.id}`" @click="tool = tl.id" />
      <span class="sep" />
      <Button icon="pi pi-undo" text rounded size="small" :disabled="!canUndo" :aria-label="t('whiteboards.undo')" data-testid="undo" @click="undo" />
      <Button icon="pi pi-refresh" text rounded size="small" :disabled="!canRedo" :aria-label="t('whiteboards.redo')" class="redo" data-testid="redo" @click="redo" />
      <span class="sep" />
      <span class="swatches" role="group" :aria-label="t('whiteboards.stroke')">
        <button v-for="c in PALETTE" :key="c" type="button" class="swatch" :class="{ on: (selected?.stroke ?? stroke) === c }" :style="{ background: c }" :aria-label="`${t('whiteboards.stroke')} ${c}`" :aria-pressed="(selected?.stroke ?? stroke) === c" @click="pickStroke(c)" />
      </span>
      <span class="swatches" role="group" :aria-label="t('whiteboards.fill')">
        <button type="button" class="swatch none" :class="{ on: (selected ? selected.fill : fill) === null }" :aria-label="t('whiteboards.noFill')" @click="pickFill(null)">∅</button>
        <button v-for="c in FILLS" :key="c" type="button" class="swatch" :class="{ on: (selected ? selected.fill : fill) === c }" :style="{ background: c }" :aria-label="`${t('whiteboards.fill')} ${c}`" @click="pickFill(c)" />
      </span>
    </div>

    <div class="workspace">
      <div class="surface" tabindex="0" :aria-label="t('whiteboards.canvasHint')">
        <BoardCanvas ref="canvas" :elements="elements" :tool="tool" :selected-id="selectedId" :read-only="!canEdit" :stroke="stroke" :fill="fill" :label="t('whiteboards.canvasLabel', { title: board.title, count: elements.length })" @select="setSelected" @create="onCreate" @change="onChange" />
        <div class="zoom">
          <Button icon="pi pi-search-minus" text rounded size="small" :aria-label="t('whiteboards.zoomOut')" @click="canvas?.zoomBy(1 / 1.2)" />
          <Button icon="pi pi-expand" text rounded size="small" :aria-label="t('whiteboards.fit')" data-testid="fit" @click="canvas?.resetView()" />
          <Button icon="pi pi-search-plus" text rounded size="small" :aria-label="t('whiteboards.zoomIn')" @click="canvas?.zoomBy(1.2)" />
        </div>
      </div>

      <aside class="side">
        <section v-if="selected && canEdit" class="inspector" :aria-label="t('whiteboards.inspector')" data-testid="inspector">
          <h2>{{ t(`whiteboards.types.${selected.type}`) }}</h2>
          <template v-if="hasText">
            <label for="el-text">{{ t('whiteboards.text') }}</label>
            <textarea id="el-text" :value="selected.text" rows="4" maxlength="2000" dir="auto" data-testid="element-text" @input="onChange(selected.id, { text: ($event.target as HTMLTextAreaElement).value })" />
            <label for="el-font">{{ t('whiteboards.fontSize') }}</label>
            <input id="el-font" type="number" min="8" max="200" :value="selected.fontSize" data-testid="element-font" @change="onChange(selected.id, { fontSize: Math.min(200, Math.max(8, Number(($event.target as HTMLInputElement).value) || 20)) })" />
          </template>
          <template v-if="selected.type !== 'Text' && selected.type !== 'Sticky'">
            <label for="el-width">{{ t('whiteboards.lineWidth') }}</label>
            <select id="el-width" :value="selected.strokeWidth" data-testid="element-stroke-width" @change="onChange(selected.id, { strokeWidth: Number(($event.target as HTMLSelectElement).value) })">
              <option v-for="w in STROKE_WIDTHS" :key="w" :value="w">{{ w }}px</option>
            </select>
          </template>
          <div class="actions">
            <Button size="small" severity="secondary" outlined icon="pi pi-clone" :label="t('whiteboards.duplicate')" data-testid="duplicate" @click="duplicateSelected" />
            <Button size="small" severity="secondary" outlined icon="pi pi-angle-double-up" :label="t('whiteboards.toFront')" data-testid="to-front" @click="order('front')" />
            <Button size="small" severity="secondary" outlined icon="pi pi-angle-double-down" :label="t('whiteboards.toBack')" data-testid="to-back" @click="order('back')" />
            <Button size="small" severity="danger" outlined icon="pi pi-trash" :label="t('common.delete')" data-testid="delete-element" @click="removeSelected" />
          </div>
        </section>

        <section class="outline" :aria-label="t('whiteboards.elements')">
          <h2>{{ t('whiteboards.elements') }} ({{ elements.length }})</h2>
          <p v-if="elements.length === 0" class="muted" data-testid="empty-board">{{ t('whiteboards.emptyBoard') }}</p>
          <ol>
            <li v-for="(e, i) in elements" :key="e.id">
              <button type="button" class="row" :class="{ on: e.id === selectedId }" :aria-current="e.id === selectedId" data-testid="outline-item" @click="setSelected(e.id)">
                <span class="n">{{ i + 1 }}</span>
                <span>{{ t(`whiteboards.types.${e.type}`) }}</span>
                <span class="excerpt" dir="auto">{{ labelOf(e) }}</span>
              </button>
            </li>
          </ol>
        </section>
        <Button v-if="canEdit" type="button" severity="danger" text size="small" :label="t('whiteboards.deleteBoard')" data-testid="delete-board" @click="removeBoard" />
      </aside>
    </div>
  </section>
</template>

<style scoped>
.board-page { display: grid; gap: var(--raavi-space-3); }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: end; }
.titles { display: grid; gap: 0.25rem; min-inline-size: 0; flex: 1 1 18rem; }
h1 { margin: 0; font-size: 1.5rem; }
h2 { margin: 0; font-size: 1rem; }
.board-title { font-size: 1.25rem; font-weight: 600; }
.tools, .toolbar { display: flex; gap: var(--raavi-space-1, 0.25rem); flex-wrap: wrap; align-items: center; }
.toolbar { padding: var(--raavi-space-1, 0.25rem); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.sep { inline-size: 1px; block-size: 1.5rem; background: var(--raavi-border); margin-inline: 0.25rem; }
.swatches { display: inline-flex; gap: 0.2rem; align-items: center; }
.swatch { inline-size: 1.4rem; block-size: 1.4rem; border-radius: 50%; border: 2px solid var(--raavi-border); cursor: pointer; padding: 0; font-size: 0.8rem; line-height: 1; background: transparent; color: inherit; }
.swatch.on { outline: 2px solid var(--raavi-primary, #2563eb); outline-offset: 1px; }
.swatch:focus-visible { outline: 2px solid var(--raavi-primary, #2563eb); outline-offset: 2px; }
.workspace { display: grid; grid-template-columns: 1fr minmax(13rem, 17rem); gap: var(--raavi-space-3); align-items: start; }
@media (max-width: 52rem) { .workspace { grid-template-columns: 1fr; } }
.surface { position: relative; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); block-size: min(70vh, 40rem); min-block-size: 24rem; overflow: hidden; }
.surface:focus-visible { outline: 2px solid var(--raavi-primary, #2563eb); }
.zoom { position: absolute; inset-block-end: 0.5rem; inset-inline-end: 0.5rem; display: flex; background: var(--raavi-surface, #fff); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.side { display: grid; gap: var(--raavi-space-3); }
.inspector, .outline { display: grid; gap: var(--raavi-space-2); padding: var(--raavi-space-2); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.inspector textarea, .inspector input, .inspector select { font: inherit; padding: 0.35rem; }
.actions { display: flex; flex-wrap: wrap; gap: 0.35rem; }
.outline ol { list-style: none; margin: 0; padding: 0; display: grid; gap: 0.2rem; max-block-size: 18rem; overflow: auto; }
.row { all: unset; box-sizing: border-box; inline-size: 100%; display: flex; gap: 0.5rem; align-items: baseline; padding: 0.25rem 0.4rem; border-radius: 4px; cursor: pointer; }
.row:focus-visible { outline: 2px solid var(--raavi-primary, #2563eb); }
.row.on { background: var(--raavi-surface-muted, rgba(59, 130, 246, 0.12)); }
.n { min-inline-size: 1.5rem; font-weight: 700; text-align: center; }
.excerpt { color: var(--raavi-text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.muted { color: var(--raavi-text-muted); margin: 0; }
:deep(.redo .pi-refresh) { transform: scaleX(-1); }
</style>
