<script setup lang="ts">
import { computed, defineAsyncComponent, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useEntityCrumbs } from '@/core/navigation/useEntityCrumbs'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import { whiteboardsApi, type BoardElement, type BoardGridStyle, type Whiteboard } from '@/core/api/whiteboards'
import { errorMessage } from '@/core/api/errors'
import { useAuthStore } from '@/stores/auth'
import { liveBoardsAvailable, startBoardSession, type BoardPeer, type BoardSession } from './liveSession'
import type { LiveStatus } from '@/modules/editor/collaboration'
import { buildTemplate, TEMPLATE_IDS, templateLabels, type TemplateId } from './boardTemplates'
import { syncState } from './liveBoard'
import {
  alignBottom, alignCenterX, alignCenterY, alignLeft, alignRight, alignTop, copyElements, createImageElement, debounce, distributeH, distributeV,
  duplicate, erasePen, FILLS, groupElements, History, labelOf, MAX_ELEMENTS, PALETTE, pasteElements, patch, recomputeConnectors, remove, reorder,
  round, snapTo, stepOrder, STROKE_WIDTHS, ungroupElements, type Tool,
} from './boardModel'

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
const auth = useAuthStore()

const tools: { id: Tool; icon: string }[] = [
  { id: 'select', icon: 'pi pi-arrow-up-left' },
  { id: 'hand', icon: 'pi pi-arrows-alt' },
  { id: 'rect', icon: 'pi pi-stop' },
  { id: 'roundRect', icon: 'pi pi-stop' },
  { id: 'ellipse', icon: 'pi pi-circle' },
  { id: 'diamond', icon: 'pi pi-stop diamond' },
  { id: 'triangle', icon: 'pi pi-caret-up' },
  { id: 'star', icon: 'pi pi-star' },
  { id: 'sticky', icon: 'pi pi-bookmark' },
  { id: 'text', icon: 'pi pi-align-left' },
  { id: 'arrow', icon: 'pi pi-arrow-right' },
  { id: 'line', icon: 'pi pi-minus' },
  { id: 'pen', icon: 'pi pi-pencil' },
  { id: 'highlighter', icon: 'pi pi-palette' },
  { id: 'eraser', icon: 'pi pi-eraser' },
  { id: 'connector', icon: 'pi pi-link' },
  { id: 'table', icon: 'pi pi-table' },
  { id: 'image', icon: 'pi pi-image' },
]

const id = computed(() => String(route.params.id))
const board = ref<Whiteboard | null>(null)
useEntityCrumbs(() => board.value, { labelKey: 'whiteboards.nav', routeName: 'space-whiteboards' })
const title = ref('')
const description = ref('')
const background = ref('#ffffff')
const gridStyle = ref<BoardGridStyle>('Dot')
const elements = ref<BoardElement[]>([])
const selectedId = ref<string | null>(null)
const selectedIds = ref<string[]>([])
const tool = ref<Tool>('select')
const stroke = ref<string>(PALETTE[0])
const fill = ref<string | null>(null)
const gridOn = ref(localGet('raavi.board.grid') === '1')
const snapOn = ref(localGet('raavi.board.snap') === '1')
function localGet(key: string): string | null {
  try {
    return localStorage.getItem(key)
  } catch {
    return null
  }
}
function toggle(which: 'grid' | 'snap') {
  const flag = which === 'grid' ? gridOn : snapOn
  flag.value = !flag.value
  try {
    localStorage.setItem(`raavi.board.${which}`, flag.value ? '1' : '0')
  } catch {
    /* not saved: only for this page */
  }
}
const saved = ref('')
const saving = ref(false)
const saveError = ref(false)
const history = new History()
const historyTick = ref(0)
const canvas = ref<{ zoomBy(f: number): void; resetView(): void; fitSelection(): void; toPng(): string | null; viewCenter(): { x: number; y: number }; focusOn(e: BoardElement): void } | null>(null)
const clipboard = ref<BoardElement[]>([])
const templateChoice = ref<TemplateId | ''>('')
const imageInput = ref<HTMLInputElement | null>(null)
const lastCursor = ref<{ x: number; y: number } | null>(null)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const canEdit = computed(() => !!board.value?.canEdit)
const selected = computed(() => elements.value.find((e) => e.id === selectedId.value) ?? null)
const hasText = computed(() => selected.value?.type === 'Text' || selected.value?.type === 'Sticky')
const fillable = (type: string) => !['Text', 'Pen', 'Arrow', 'Line', 'Connector', 'Image', 'Group'].includes(type)
const snapshot = () => JSON.stringify({ title: title.value, description: description.value, background: background.value, gridStyle: gridStyle.value, elements: elements.value })
const dirty = computed(() => canEdit.value && !isLive.value && liveState.value !== 'joining' && snapshot() !== saved.value)
const liveState = ref<'off' | 'joining' | 'live'>('off')
const liveStatus = ref<LiveStatus>('connecting')
const peers = ref<BoardPeer[]>([])
let session: BoardSession | null = null
let stops: (() => void)[] = []
let joinTimer: ReturnType<typeof setTimeout> | undefined
const isLive = computed(() => liveState.value === 'live')
const editable = computed(() => canEdit.value && liveState.value !== 'joining')
const canUndo = computed(() => (historyTick.value, session && isLive.value ? session.board.canUndo : history.canUndo))
const canRedo = computed(() => (historyTick.value, session && isLive.value ? session.board.canRedo : history.canRedo))
const sync = computed(() => syncState({
  live: isLive.value || liveState.value === 'joining',
  status: liveState.value === 'joining' ? 'connecting' : liveStatus.value,
  saving: saving.value,
  dirty: dirty.value,
  failed: saveError.value,
}))

function gridOf(value: string | undefined): BoardGridStyle {
  return value === 'None' || value === 'Line' || value === 'Dot' ? value : 'Dot'
}

function apply(b: Whiteboard) {
  board.value = b
  title.value = b.title
  description.value = b.description ?? ''
  background.value = b.background || '#ffffff'
  gridStyle.value = gridOf(b.gridStyle)
  elements.value = b.elements
  keepSelection(b.elements)
  history.clear()
  historyTick.value++
  saved.value = snapshot()
  saveError.value = false
}

function keepSelection(els: BoardElement[]) {
  selectedIds.value = selectedIds.value.filter((sid) => els.some((e) => e.id === sid))
  if (!selectedIds.value.includes(selectedId.value ?? '')) selectedId.value = selectedIds.value[selectedIds.value.length - 1] ?? null
}

async function load() {
  leaveLive()
  try {
    apply(await whiteboardsApi.get(id.value))
    if (canEdit.value) void joinLive()
  } catch (e) {
    fail(e)
  }
}

/** Readers may opt into a read-only live session to follow editors (Phase 3). */
async function followLive() {
  if (canEdit.value || isLive.value || liveState.value === 'joining') return
  await joinLive()
}

function followPeer(peer: BoardPeer) {
  if (!peer.cursor || !canvas.value) return
  canvas.value.focusOn({ id: '_', type: 'Rect', x: peer.cursor.x - 40, y: peer.cursor.y - 40, w: 80, h: 80, rotation: 0, stroke: '#000', fill: null, strokeWidth: 1, text: '', fontSize: 12, rtl: false, points: [] })
}
onMounted(load)
watch(id, load)

function leaveLive() {
  clearTimeout(joinTimer)
  stops.forEach((s) => s())
  stops = []
  session?.destroy()
  session = null
  liveState.value = 'off'
  peers.value = []
}

/** Tries to join the live session; any problem falls back to the ordinary whole-board editing (nothing is lost, nothing is blocked for long). */
async function joinLive() {
  const target = board.value
  const me = auth.me
  if (!target || !me || !(await liveBoardsAvailable())) return
  liveState.value = 'joining'
  try {
    const s = await startBoardSession(target.id, { id: me.id, name: me.displayName })
    session = s
    const stopPeers = watch(s.peers, (p) => (peers.value = [...p]), { immediate: true, deep: true })
    const stopStatus = watch(
      s.status,
      (status) => {
        liveStatus.value = status
        if (status === 'live' && liveState.value === 'joining') {
          clearTimeout(joinTimer)
          const synced = s.board.toElements()
          elements.value = synced
          saved.value = snapshot()
          keepSelection(synced)
          history.clear()
          liveState.value = 'live'
        } else if (status === 'reset') {
          void load()
        } else if (status === 'revoked') {
          toast.add({ severity: 'warn', summary: t('whiteboards.liveRevoked'), life: 6000 })
          void load()
        }
      },
      { immediate: true },
    )
    stops.push(
      stopPeers,
      stopStatus,
      s.board.onRemote((els) => {
        elements.value = els
        keepSelection(els)
        historyTick.value++
      }),
      s.board.onHistory(() => historyTick.value++),
    )
    joinTimer = setTimeout(() => {
      if (liveState.value === 'joining') {
        leaveLive()
        toast.add({ severity: 'info', summary: t('whiteboards.liveFallback'), life: 5000 })
      }
    }, 10_000)
  } catch {
    leaveLive()
  }
}

/** Live mode saves the title on its own (the whole-board save would reset everybody's session). */
async function renameLive() {
  const b = board.value
  const name = title.value.trim()
  if (!isLive.value || !b || !name || name === b.title) return
  try {
    const updated = await whiteboardsApi.rename(b.id, name)
    board.value = { ...b, title: updated.title, revision: updated.revision }
    saved.value = snapshot()
  } catch (e) {
    fail(e)
  }
}

/** Background, grid and description while a live session is open (elements stay in Yjs). */
async function saveAppearance() {
  const b = board.value
  if (!isLive.value || !b) return
  try {
    const updated = await whiteboardsApi.updateMeta(b.id, { description: description.value, background: background.value, gridStyle: gridStyle.value })
    board.value = { ...b, description: updated.description, background: updated.background, gridStyle: updated.gridStyle, revision: updated.revision, updatedAt: updated.updatedAt }
    saved.value = snapshot()
  } catch (e) {
    fail(e)
  }
}

function appearanceChanged() {
  if (isLive.value) void saveAppearance()
}

/** Every model change goes through here so that it can be undone. */
function commit(next: BoardElement[]) {
  if (next === elements.value) return
  if (session && isLive.value) {
    session.board.apply(next)
    elements.value = next
    historyTick.value++
    return
  }
  history.record(elements.value)
  elements.value = next
  historyTick.value++
}

function onCreate(el: BoardElement) {
  if (!canEdit.value || elements.value.length >= MAX_ELEMENTS) return
  const moved = new Set([el.id, el.fromId, el.toId].filter((x): x is string => !!x))
  commit(recomputeConnectors([...elements.value, el], moved))
  setSelected(el.id)
  tool.value = 'select'
}

function onChange(elId: string, changes: Partial<BoardElement>) {
  if (!canEdit.value) return
  const before = elements.value
  const current = before.find((e) => e.id === elId)
  if (!current || current.locked) return
  const isMove = changes.x !== undefined && changes.y !== undefined && changes.w === undefined && changes.h === undefined && changes.rotation === undefined && changes.text === undefined && changes.cells === undefined
  const moved = new Set<string>([elId])
  let next: BoardElement[]
  if (isMove && current.parentId) {
    const dx = changes.x! - current.x
    const dy = changes.y! - current.y
    const group = before.find((e) => e.id === current.parentId && e.type === 'Group')
    const ids = new Set<string>(group?.children ?? [elId])
    if (group) ids.add(group.id)
    ids.forEach((sid) => moved.add(sid))
    next = before.map((e) => (ids.has(e.id) && !e.locked ? { ...e, x: round(e.x + dx), y: round(e.y + dy) } : e))
  } else if (isMove && current.type === 'Group') {
    const dx = changes.x! - current.x
    const dy = changes.y! - current.y
    const ids = new Set<string>([elId, ...(current.children ?? [])])
    ids.forEach((sid) => moved.add(sid))
    next = before.map((e) => (ids.has(e.id) && !e.locked ? { ...e, x: round(e.x + dx), y: round(e.y + dy) } : e))
  } else if (isMove && selectedIds.value.length > 1 && selectedIds.value.includes(elId)) {
    const dx = changes.x! - current.x
    const dy = changes.y! - current.y
    selectedIds.value.forEach((sid) => moved.add(sid))
    next = before.map((e) => (selectedIds.value.includes(e.id) && !e.locked ? { ...e, x: round(e.x + dx), y: round(e.y + dy) } : e))
  } else {
    next = patch(before, elId, changes)
  }
  commit(recomputeConnectors(next, moved))
}

function onErase(elId: string, x: number, y: number) {
  if (!canEdit.value) return
  const current = elements.value.find((e) => e.id === elId)
  if (!current) return
  const parts = erasePen(current, x, y)
  if (parts.length === 1 && parts[0] === current) return
  if (elements.value.length - 1 + parts.length > MAX_ELEMENTS) return
  const next = elements.value.slice()
  next.splice(elements.value.findIndex((e) => e.id === elId), 1, ...parts)
  commit(next)
  if (!next.some((e) => e.id === selectedId.value)) setSelected(parts[0]?.id ?? null)
}

function setSelected(elId: string | null) {
  selectedId.value = elId
  selectedIds.value = elId ? [elId] : []
}

function toggleSelected(elId: string) {
  const has = selectedIds.value.includes(elId)
  const next = has ? selectedIds.value.filter((sid) => sid !== elId) : [...selectedIds.value, elId]
  selectedIds.value = next
  selectedId.value = has ? (next[next.length - 1] ?? null) : elId
}

function onMarquee(ids: string[]) {
  selectedIds.value = ids
  selectedId.value = ids[ids.length - 1] ?? null
}

function onCursor(point: { x: number; y: number } | null) {
  lastCursor.value = point
  if (isLive.value) session?.setPresence?.(point, selectedIds.value)
}

watch(selectedIds, (ids) => {
  if (isLive.value) session?.setPresence?.(lastCursor.value, ids)
})

function pickStroke(color: string) {
  stroke.value = color
  if (selected.value && canEdit.value) onChange(selected.value.id, { stroke: color })
}

function pickFill(color: string | null) {
  fill.value = color
  if (selected.value && canEdit.value && fillable(selected.value.type)) onChange(selected.value.id, { fill: color })
}

function removeSelected() {
  if (!canEdit.value || selectedIds.value.length === 0) return
  const drop = new Set(selectedIds.value)
  const next = elements.value.filter((e) => !drop.has(e.id)).map((e) => (e.parentId && drop.has(e.parentId) ? { ...e, parentId: null } : e))
  commit(next)
  setSelected(null)
}

function duplicateSelected() {
  if (!selected.value || !canEdit.value) return
  const result = duplicate(elements.value, selected.value.id)
  if (result.id) {
    commit(result.elements)
    setSelected(result.id)
  }
}

function order(to: 'front' | 'back') {
  if (selected.value && canEdit.value) commit(reorder(elements.value, selected.value.id, to))
}

function insertTemplate(templateId: TemplateId) {
  if (!canEdit.value) return
  const c = canvas.value?.viewCenter() ?? { x: 400, y: 300 }
  const made = buildTemplate(templateId, Math.round(c.x), Math.round(c.y), templateLabels((k) => t(k))).map((e) => ({ ...e, rtl: /[\u0590-\u08FF]/.test(e.text) }))
  if (elements.value.length + made.length > MAX_ELEMENTS) {
    toast.add({ severity: 'warn', summary: t('whiteboards.tooMany'), life: 5000 })
    return
  }
  commit([...elements.value, ...made])
  setSelected(null)
  templateChoice.value = ''
}

function copySelected() {
  const ids = selectedIds.value.length ? selectedIds.value : selectedId.value ? [selectedId.value] : []
  const picked = elements.value.filter((e) => ids.includes(e.id))
  if (picked.length) clipboard.value = copyElements(picked)
}

function pasteClipboard() {
  if (!canEdit.value || clipboard.value.length === 0) return
  const copies = pasteElements(clipboard.value.slice(0, Math.max(0, MAX_ELEMENTS - elements.value.length)), { x: 30, y: 30 })
  if (copies.length === 0) return
  commit([...elements.value, ...copies])
  selectedIds.value = copies.map((e) => e.id)
  selectedId.value = copies[0]?.id ?? null
  clipboard.value = clipboard.value.map((e) => ({ ...e, x: e.x + 30, y: e.y + 30 }))
}

function stepLayer(dir: 'forward' | 'backward') {
  if (selected.value && canEdit.value) commit(stepOrder(elements.value, selected.value.id, dir))
}

function alignToGrid() {
  if (!selected.value || !canEdit.value) return
  onChange(selected.value.id, { x: snapTo(selected.value.x, true), y: snapTo(selected.value.y, true) })
}

function alignSelection(which: 'left' | 'right' | 'top' | 'bottom' | 'centerX' | 'centerY' | 'h' | 'v') {
  if (!canEdit.value) return
  const ids = selectedIds.value
  const fns = { left: alignLeft, right: alignRight, top: alignTop, bottom: alignBottom, centerX: alignCenterX, centerY: alignCenterY, h: distributeH, v: distributeV }
  const next = recomputeConnectors(fns[which](elements.value, ids), new Set(ids))
  if (next !== elements.value) commit(next)
}

function groupSelected() {
  if (!canEdit.value) return
  const result = groupElements(elements.value, selectedIds.value)
  if (!result.id) return
  commit(result.elements)
  setSelected(result.id)
}

function ungroupSelected() {
  if (!canEdit.value || selected.value?.type !== 'Group') return
  commit(ungroupElements(elements.value, selected.value.id))
  setSelected(null)
}

function cycleGrid() {
  const order: BoardGridStyle[] = ['Dot', 'Line', 'None']
  gridStyle.value = order[(order.indexOf(gridStyle.value) + 1) % order.length]!
  if (gridStyle.value !== 'None') gridOn.value = true
  appearanceChanged()
}

function pickTool(next: Tool) {
  if (next === 'image') {
    imageInput.value?.click()
    return
  }
  tool.value = next
}

async function uploadImage(ev: Event) {
  const input = ev.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  const b = board.value
  if (!file || !b || !canEdit.value) return
  const form = new FormData()
  form.append('file', file, file.name)
  try {
    const att = await whiteboardsApi.uploadAttachment(b.id, form)
    const c = canvas.value?.viewCenter() ?? { x: 200, y: 200 }
    onCreate(createImageElement(att.id, Math.round(c.x - 80), Math.round(c.y - 60), 160, 120, file.name.replace(/\.[^.]+$/, '')))
  } catch (e) {
    fail(e)
  }
}

function cycleSelection(back: boolean) {
  const list = elements.value
  if (list.length === 0) return
  const i = list.findIndex((e) => e.id === selectedId.value)
  const next = list[(i + (back ? -1 : 1) + list.length) % list.length]!
  setSelected(next.id)
  canvas.value?.focusOn(next)
}

function undo() {
  if (session && isLive.value) {
    session.board.undo()
    return
  }
  const prev = history.undo(elements.value)
  if (prev) {
    elements.value = prev
    historyTick.value++
  }
}

function redo() {
  if (session && isLive.value) {
    session.board.redo()
    return
  }
  const next = history.redo(elements.value)
  if (next) {
    elements.value = next
    historyTick.value++
  }
}

async function save(manual = false) {
  if (!board.value || !dirty.value || saving.value || !title.value.trim()) return
  autosave.cancel()
  saving.value = true
  saveError.value = false
  try {
    apply(await whiteboardsApi.update(board.value, {
      title: title.value.trim(),
      description: description.value,
      background: background.value,
      gridStyle: gridStyle.value,
      elements: elements.value,
    }))
    if (manual) toast.add({ severity: 'success', summary: t('common.saved'), life: 2000 })
  } catch (e) {
    saveError.value = true
    fail(e)
  } finally {
    saving.value = false
  }
}

const autosave = debounce(() => { void save(false) }, 800)

watch([elements, title, description, background, gridStyle], () => {
  if (!canEdit.value || isLive.value || liveState.value === 'joining') {
    autosave.cancel()
    return
  }
  if (snapshot() !== saved.value) autosave()
  else autosave.cancel()
})

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

const TOOL_KEYS: Record<string, Tool> = {
  v: 'select', h: 'hand', r: 'rect', u: 'roundRect', e: 'ellipse', d: 'diamond', g: 'triangle', f: 'star', s: 'sticky', t: 'text', a: 'arrow', l: 'line', p: 'pen', k: 'connector', m: 'highlighter', x: 'eraser', b: 'table',
}

function onKey(event: KeyboardEvent) {
  const target = event.target as HTMLElement | null
  if (target && ['INPUT', 'TEXTAREA', 'SELECT'].includes(target.tagName)) return
  const mod = event.ctrlKey || event.metaKey
  if (mod && event.key.toLowerCase() === 'z') (event.shiftKey ? redo() : undo())
  else if (mod && event.key.toLowerCase() === 'y') redo()
  else if (mod && event.key.toLowerCase() === 'd') duplicateSelected()
  else if (mod && event.key.toLowerCase() === 'c') copySelected()
  else if (mod && event.key.toLowerCase() === 'v') pasteClipboard()
  else if (event.key === ']' && !mod) stepLayer('forward')
  else if (event.key === '[' && !mod) stepLayer('backward')
  else if ((event.key === '+' || event.key === '=') && !mod) canvas.value?.zoomBy(1.2)
  else if (event.key === '-' && !mod) canvas.value?.zoomBy(1 / 1.2)
  else if (event.key === '0' && !mod) canvas.value?.resetView()
  else if (event.key === 'Tab' && !mod && elements.value.length > 0) cycleSelection(event.shiftKey)
  else if (event.key === 'Delete' || event.key === 'Backspace') removeSelected()
  else if (event.key === 'Escape') {
    tool.value = 'select'
    setSelected(null)
  } else if (event.key.startsWith('Arrow') && selectedIds.value.length > 0 && editable.value) {
    const step = event.shiftKey ? 20 : 1
    const dx = event.key === 'ArrowLeft' ? -step : event.key === 'ArrowRight' ? step : 0
    const dy = event.key === 'ArrowUp' ? -step : event.key === 'ArrowDown' ? step : 0
    const ids = new Set(selectedIds.value)
    const next = elements.value.map((e) => (ids.has(e.id) && !e.locked ? { ...e, x: e.x + dx, y: e.y + dy } : e))
    commit(recomputeConnectors(next, ids))
  } else if (!mod && !event.altKey && editable.value && TOOL_KEYS[event.key.toLowerCase()]) tool.value = TOOL_KEYS[event.key.toLowerCase()]!
  else return
  event.preventDefault()
}

onBeforeUnmount(() => {
  autosave.cancel()
  leaveLive()
})
onBeforeRouteLeave(() => !dirty.value || window.confirm(t('whiteboards.unsaved')))
</script>

<template>
  <section v-if="board" class="board-page" data-testid="whiteboard" @keydown="onKey">
    <header class="head">
      <div class="titles">
        <RouterLink :to="{ name: 'space-whiteboards', params: { key: board.spaceKey } }" class="muted">{{ board.spaceName }}</RouterLink>
        <InputText v-if="canEdit" v-model="title" maxlength="200" class="board-title" dir="auto" :aria-label="t('whiteboards.boardTitle')" data-testid="board-title-input" @change="renameLive" />
        <h1 v-else dir="auto">{{ board.title }}</h1>
        <textarea v-if="canEdit" v-model="description" maxlength="2000" rows="2" class="board-note" dir="auto" :aria-label="t('whiteboards.description')" :placeholder="t('whiteboards.description')" data-testid="board-description-input" @change="appearanceChanged" />
        <p v-else-if="board.description" class="muted" dir="auto">{{ board.description }}</p>
      </div>
      <div class="tools">
        <span v-if="canEdit" class="live" :class="sync" role="status" data-testid="sync-status">{{ t(`whiteboards.sync.${sync}`) }}</span>
        <Button v-if="!canEdit && liveState === 'off'" icon="pi pi-eye" text size="small" :label="t('whiteboards.followLive')" data-testid="follow-live" @click="followLive" />
        <span v-if="liveState !== 'off'" class="live" :class="liveState === 'live' && liveStatus === 'offline' ? 'off' : liveState" role="status" data-testid="live-status">
          <i class="pi pi-circle-fill" aria-hidden="true" />
          {{ liveState === 'joining' ? t('whiteboards.liveJoining') : liveStatus === 'offline' ? t('whiteboards.liveOffline') : t('whiteboards.liveOn') }}
          <template v-if="peers.length"> · <span data-testid="peers">{{ peers.map((p) => p.name).join(', ') }}</span>
            <button v-for="p in peers" :key="p.clientId" type="button" class="follow-peer" :title="t('whiteboards.followPeer', { name: p.name })" data-testid="follow-peer" @click="followPeer(p)">{{ p.name }}</button>
          </template>
        </span>
        <Button v-if="canEdit && !isLive" icon="pi pi-save" :label="t('common.save')" :loading="saving" :disabled="!dirty || !title.trim()" data-testid="save-board" @click="save(true)" />
        <Button icon="pi pi-image" text :label="t('whiteboards.exportPng')" data-testid="export-png" @click="exportPng" />
        <Button as="a" :href="whiteboardsApi.exportUrl(board.id)" icon="pi pi-download" text :label="t('whiteboards.exportSvg')" :disabled="dirty" data-testid="export-svg" />
        <Button as="a" :href="whiteboardsApi.exportJsonUrl(board.id)" icon="pi pi-file" text :label="t('whiteboards.exportJson')" :disabled="dirty" data-testid="export-json" />
      </div>
    </header>
    <p v-if="dirty" class="muted" role="status" data-testid="unsaved">{{ t('whiteboards.exportAfterSave') }}</p>

    <div v-if="editable" class="toolbar" role="toolbar" :aria-label="t('whiteboards.tools')">
      <Button v-for="tl in tools" :key="tl.id" :icon="tl.icon" :severity="tool === tl.id ? 'primary' : 'secondary'" :outlined="tool !== tl.id" size="small" :aria-label="tl.id === 'image' ? t('whiteboards.uploadImage') : t(`whiteboards.toolNames.${tl.id}`)" :aria-pressed="tool === tl.id" :title="tl.id === 'image' ? t('whiteboards.uploadImage') : t(`whiteboards.toolNames.${tl.id}`)" :data-testid="`tool-${tl.id}`" @click="pickTool(tl.id)" />
      <input ref="imageInput" type="file" accept="image/png,image/jpeg,image/gif,image/webp" class="sr" data-testid="image-file" @change="uploadImage" />
      <span class="sep" />
      <Button icon="pi pi-undo" text rounded size="small" :disabled="!canUndo" :aria-label="t('whiteboards.undo')" data-testid="undo" @click="undo" />
      <Button icon="pi pi-refresh" text rounded size="small" :disabled="!canRedo" :aria-label="t('whiteboards.redo')" class="redo" data-testid="redo" @click="redo" />
      <span class="sep" />
      <select v-model="templateChoice" class="tpl" :aria-label="t('whiteboards.templates')" data-testid="template-select" @change="templateChoice && insertTemplate(templateChoice as TemplateId)">
        <option value="">{{ t('whiteboards.templates') }}…</option>
        <option v-for="tid in TEMPLATE_IDS" :key="tid" :value="tid">{{ t(`whiteboards.templateNames.${tid}`) }}</option>
      </select>
      <span class="sep" />
      <Button icon="pi pi-table" size="small" :severity="gridOn ? 'primary' : 'secondary'" :outlined="!gridOn" :aria-pressed="gridOn" :aria-label="t('whiteboards.grid')" :title="t('whiteboards.grid')" data-testid="toggle-grid" @click="toggle('grid')" />
      <Button icon="pi pi-bars" size="small" :aria-label="t(`whiteboards.gridStyles.${gridStyle}`)" :title="t(`whiteboards.gridStyles.${gridStyle}`)" data-testid="grid-style" @click="cycleGrid" />
      <Button icon="pi pi-th-large" size="small" :severity="snapOn ? 'primary' : 'secondary'" :outlined="!snapOn" :aria-pressed="snapOn" :aria-label="t('whiteboards.snap')" :title="t('whiteboards.snap')" data-testid="toggle-snap" @click="toggle('snap')" />
      <label class="bg" :title="t('whiteboards.background')">
        <span class="sr">{{ t('whiteboards.background') }}</span>
        <input type="color" :value="background" data-testid="board-background" :aria-label="t('whiteboards.background')" @change="background = ($event.target as HTMLInputElement).value; appearanceChanged()" />
      </label>
      <span class="sep" />
      <span class="swatches" role="group" :aria-label="t('whiteboards.stroke')">
        <button v-for="c in PALETTE" :key="c" type="button" class="swatch" :class="{ on: (selected?.stroke ?? stroke) === c }" :style="{ background: c }" :aria-label="`${t('whiteboards.stroke')} ${c}`" :aria-pressed="(selected?.stroke ?? stroke) === c" @click="pickStroke(c)" />
      </span>
      <span class="swatches" role="group" :aria-label="t('whiteboards.fill')">
        <button type="button" class="swatch none" :class="{ on: (selected ? selected.fill : fill) === null }" :aria-label="t('whiteboards.noFill')" @click="pickFill(null)">∅</button>
        <button v-for="c in FILLS" :key="c" type="button" class="swatch" :class="{ on: (selected ? selected.fill : fill) === c }" :style="{ background: c }" :aria-label="`${t('whiteboards.fill')} ${c}`" @click="pickFill(c)" />
      </span>
    </div>

    <p v-if="editable" class="muted shortcuts" data-testid="board-shortcuts">{{ t('whiteboards.shortcuts') }}</p>
    <div class="workspace">
      <div class="surface" tabindex="0" :aria-label="t('whiteboards.canvasHint')">
        <BoardCanvas :key="board.id" ref="canvas" :elements="elements" :tool="tool" :selected-id="selectedId" :selected-ids="selectedIds" :read-only="!editable" :stroke="stroke" :fill="fill" :grid="gridOn && gridStyle !== 'None'" :snap="snapOn" :grid-style="gridStyle" :background="background" :board-id="board.id" :peers="peers" :label="t('whiteboards.canvasLabel', { title: board.title, count: elements.length })" @select="setSelected" @toggle="toggleSelected" @marquee="onMarquee" @create="onCreate" @change="onChange" @erase="onErase" @cursor="onCursor" />
        <div class="zoom">
          <Button icon="pi pi-search-minus" text rounded size="small" :aria-label="t('whiteboards.zoomOut')" @click="canvas?.zoomBy(1 / 1.2)" />
          <Button icon="pi pi-expand" text rounded size="small" :aria-label="t('whiteboards.fit')" data-testid="fit" @click="canvas?.resetView()" />
          <Button icon="pi pi-objects-column" text rounded size="small" :aria-label="t('whiteboards.fitSelection')" data-testid="fit-selection" @click="canvas?.fitSelection()" />
          <Button icon="pi pi-search-plus" text rounded size="small" :aria-label="t('whiteboards.zoomIn')" @click="canvas?.zoomBy(1.2)" />
        </div>
      </div>

      <aside class="side">
        <section v-if="canEdit && selectedIds.length >= 2" class="inspector" :aria-label="t('whiteboards.align')" data-testid="align-tools">
          <h2>{{ t('whiteboards.align') }}</h2>
          <div class="actions">
            <Button size="small" severity="secondary" outlined :label="t('whiteboards.alignLeft')" data-testid="align-left" @click="alignSelection('left')" />
            <Button size="small" severity="secondary" outlined :label="t('whiteboards.alignRight')" data-testid="align-right" @click="alignSelection('right')" />
            <Button size="small" severity="secondary" outlined :label="t('whiteboards.alignTop')" data-testid="align-top" @click="alignSelection('top')" />
            <Button size="small" severity="secondary" outlined :label="t('whiteboards.alignBottom')" data-testid="align-bottom" @click="alignSelection('bottom')" />
            <Button size="small" severity="secondary" outlined :label="t('whiteboards.alignCenterX')" data-testid="align-center-x" @click="alignSelection('centerX')" />
            <Button size="small" severity="secondary" outlined :label="t('whiteboards.alignCenterY')" data-testid="align-center-y" @click="alignSelection('centerY')" />
            <Button size="small" severity="secondary" outlined :label="t('whiteboards.distributeH')" data-testid="distribute-h" @click="alignSelection('h')" />
            <Button size="small" severity="secondary" outlined :label="t('whiteboards.distributeV')" data-testid="distribute-v" @click="alignSelection('v')" />
            <Button size="small" severity="secondary" outlined icon="pi pi-objects-column" :label="t('whiteboards.group')" data-testid="group-selection" @click="groupSelected" />
          </div>
        </section>

        <section v-if="selected && canEdit" class="inspector" :aria-label="t('whiteboards.inspector')" data-testid="inspector">
          <h2>{{ t(`whiteboards.types.${selected.type}`) }}</h2>
          <template v-if="hasText">
            <label for="el-text">{{ t('whiteboards.text') }}</label>
            <textarea id="el-text" :value="selected.text" rows="4" maxlength="2000" dir="auto" data-testid="element-text" @input="onChange(selected.id, { text: ($event.target as HTMLTextAreaElement).value })" />
            <label for="el-font">{{ t('whiteboards.fontSize') }}</label>
            <input id="el-font" type="number" min="8" max="200" :value="selected.fontSize" data-testid="element-font" @change="onChange(selected.id, { fontSize: Math.min(200, Math.max(8, Number(($event.target as HTMLInputElement).value) || 20)) })" />
          </template>
          <template v-if="selected.type === 'Image'">
            <label for="el-alt">{{ t('whiteboards.imageAlt') }}</label>
            <input id="el-alt" type="text" maxlength="200" :value="selected.alt ?? ''" data-testid="image-alt" @change="onChange(selected.id, { alt: ($event.target as HTMLInputElement).value })" />
          </template>
          <p v-if="selected.type === 'Table'" class="muted">{{ t('whiteboards.editCells') }}</p>
          <template v-if="selected.type === 'RoundRect'">
            <label for="el-radius">{{ t('whiteboards.cornerRadius') }}</label>
            <input id="el-radius" type="number" min="0" max="200" :value="selected.cornerRadius ?? 0" data-testid="corner-radius" @change="onChange(selected.id, { cornerRadius: Math.max(0, Number(($event.target as HTMLInputElement).value) || 0) })" />
          </template>
          <template v-if="selected.type !== 'Text' && selected.type !== 'Sticky'">
            <label for="el-width">{{ t('whiteboards.lineWidth') }}</label>
            <select id="el-width" :value="selected.strokeWidth" data-testid="element-stroke-width" @change="onChange(selected.id, { strokeWidth: Number(($event.target as HTMLSelectElement).value) })">
              <option v-for="w in STROKE_WIDTHS" :key="w" :value="w">{{ w }}px</option>
            </select>
          </template>
          <div v-if="selected.type !== 'Arrow' && selected.type !== 'Pen' && selected.type !== 'Line' && selected.type !== 'Connector'" class="geometry" role="group" :aria-label="t('whiteboards.geometry')">
            <label>X <input type="number" :value="Math.round(selected.x)" data-testid="geo-x" @change="onChange(selected.id, { x: Number(($event.target as HTMLInputElement).value) || 0 })" /></label>
            <label>Y <input type="number" :value="Math.round(selected.y)" data-testid="geo-y" @change="onChange(selected.id, { y: Number(($event.target as HTMLInputElement).value) || 0 })" /></label>
            <label>{{ t('whiteboards.width') }} <input type="number" min="10" :value="Math.round(selected.w)" data-testid="geo-w" :disabled="selected.type === 'Text'" @change="onChange(selected.id, { w: Math.max(10, Number(($event.target as HTMLInputElement).value) || 10) })" /></label>
            <label>{{ t('whiteboards.height') }} <input type="number" min="10" :value="Math.round(selected.h)" data-testid="geo-h" :disabled="selected.type === 'Text'" @change="onChange(selected.id, { h: Math.max(10, Number(($event.target as HTMLInputElement).value) || 10) })" /></label>
            <label>{{ t('whiteboards.rotation') }} <input type="number" min="-360" max="360" :value="Math.round(selected.rotation)" data-testid="geo-rotation" @change="onChange(selected.id, { rotation: Number(($event.target as HTMLInputElement).value) || 0 })" /></label>
          </div>
          <div class="actions">
            <Button size="small" severity="secondary" outlined icon="pi pi-clone" :label="t('whiteboards.duplicate')" data-testid="duplicate" @click="duplicateSelected" />
            <Button size="small" severity="secondary" outlined icon="pi pi-copy" :label="t('whiteboards.copy')" data-testid="copy-element" @click="copySelected" />
            <Button size="small" severity="secondary" outlined icon="pi pi-clipboard" :label="t('whiteboards.paste')" :disabled="clipboard.length === 0" data-testid="paste-element" @click="pasteClipboard" />
            <Button v-if="selected.type === 'Group'" size="small" severity="secondary" outlined icon="pi pi-objects-column" :label="t('whiteboards.ungroup')" data-testid="ungroup" @click="ungroupSelected" />
            <Button size="small" severity="secondary" outlined icon="pi pi-angle-up" :label="t('whiteboards.forward')" data-testid="step-forward" @click="stepLayer('forward')" />
            <Button size="small" severity="secondary" outlined icon="pi pi-angle-down" :label="t('whiteboards.backward')" data-testid="step-backward" @click="stepLayer('backward')" />
            <Button size="small" severity="secondary" outlined icon="pi pi-th-large" :label="t('whiteboards.alignGrid')" data-testid="align-grid" @click="alignToGrid" />
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
              <button type="button" class="row" :class="{ on: selectedIds.includes(e.id) }" :aria-current="e.id === selectedId" data-testid="outline-item" @click="setSelected(e.id)">
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
.board-note { font: inherit; padding: 0.35rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: var(--raavi-surface); color: inherit; resize: vertical; }
.tpl { font: inherit; padding: 0.25rem 0.4rem; border: 1px solid var(--raavi-border); border-radius: 4px; background: var(--raavi-surface); color: inherit; max-inline-size: 11rem; }
.geometry { display: grid; grid-template-columns: repeat(2, minmax(0, 1fr)); gap: var(--raavi-space-1) var(--raavi-space-2); }
.geometry label { display: grid; gap: 2px; font-size: 0.8rem; color: var(--raavi-text-muted); }
.geometry input { inline-size: 100%; }
.tools, .toolbar { display: flex; gap: var(--raavi-space-1, 0.25rem); flex-wrap: wrap; align-items: center; }
.toolbar { padding: var(--raavi-space-1, 0.25rem); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.sep { inline-size: 1px; block-size: 1.5rem; background: var(--raavi-border); margin-inline: 0.25rem; }
.swatches { display: inline-flex; gap: 0.2rem; align-items: center; }
.swatch { inline-size: 1.4rem; block-size: 1.4rem; border-radius: 50%; border: 2px solid var(--raavi-border); cursor: pointer; padding: 0; font-size: 0.8rem; line-height: 1; background: transparent; color: inherit; }
.swatch.on { outline: 2px solid var(--raavi-primary, #2563eb); outline-offset: 1px; }
.swatch:focus-visible { outline: 2px solid var(--raavi-primary, #2563eb); outline-offset: 2px; }
.bg input { inline-size: 1.8rem; block-size: 1.8rem; padding: 0; border: 1px solid var(--raavi-border); background: transparent; }
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
.live { display: inline-flex; gap: 0.35rem; align-items: center; font-size: 0.85rem; padding: 0.15rem 0.6rem; border-radius: 999px; background: var(--raavi-surface-muted, #eee); }
.live .pi { font-size: 0.6rem; }
.live.live .pi { color: var(--raavi-success, #16a34a); }
.live.joining .pi, .live.off .pi, .live.syncing .pi, .live.saving .pi { color: var(--raavi-warning, #d97706); }
.live.error .pi, .live.offline { color: var(--raavi-danger, #dc2626); }
.follow-peer { all: unset; cursor: pointer; text-decoration: underline; margin-inline-start: 0.35rem; color: inherit; }
.follow-peer:focus-visible { outline: 2px solid var(--raavi-primary, #2563eb); }
.muted { color: var(--raavi-text-muted); margin: 0; }
.sr { position: absolute; inline-size: 1px; block-size: 1px; padding: 0; margin: -1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; border: 0; }
:deep(.redo .pi-refresh) { transform: scaleX(-1); }
</style>
