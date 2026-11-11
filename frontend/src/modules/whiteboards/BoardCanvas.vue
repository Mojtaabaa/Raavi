<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type Konva from 'konva'
import type { BoardElement } from '@/core/api/whiteboards'
import { whiteboardsApi } from '@/core/api/whiteboards'
import type { BoardCursor } from './liveBoard'
import {
  bounds, columnWidths, connectorPoints, createConnector, debounce, elementFromGesture, erasePen, GRID, hitsMarquee, isHighlighter, nearestPort, round, shapeAt, snapTo, type Tool,
} from './boardModel'

/**
 * The drawing surface (Konva). It only draws `elements` and reports gestures (select, create, change); the parent owns the model and the
 * history. Selected shapes get a transformer (resize/rotate); arrows and pen strokes can be moved but not reshaped.
 */
const props = defineProps<{
  elements: BoardElement[]
  tool: Tool
  selectedId: string | null
  selectedIds?: string[]
  readOnly: boolean
  stroke: string
  fill: string | null
  label: string
  snap?: boolean
  grid?: boolean
  gridStyle?: 'None' | 'Dot' | 'Line'
  background?: string
  boardId?: string
  peers?: { name: string; color: string; cursor: BoardCursor | null; selectedIds?: string[] }[]
}>()
const emit = defineEmits<{
  select: [id: string | null]
  toggle: [id: string]
  marquee: [ids: string[]]
  create: [element: BoardElement]
  change: [id: string, changes: Partial<BoardElement>]
  erase: [id: string, x: number, y: number]
  cursor: [point: BoardCursor | null]
}>()

const host = ref<HTMLDivElement | null>(null)
const ready = ref(false)
const cellEdit = ref<{ id: string; index: number; value: string; style: Record<string, string> } | null>(null)
const safeBackground = computed(() => (props.background && /^#[0-9a-fA-F]{6}$/.test(props.background) ? props.background : '#ffffff'))
const FONT = "Calibri, 'Segoe UI', Vazirmatn, Tahoma, sans-serif"
const CREATION: Tool[] = ['rect', 'ellipse', 'diamond', 'triangle', 'sticky', 'text', 'arrow', 'line', 'pen', 'roundRect', 'star', 'table', 'connector', 'highlighter']
const LINE_TOOLS: Tool[] = ['pen', 'highlighter', 'arrow', 'line', 'connector']

let K: typeof Konva
let stage: Konva.Stage
let content: Konva.Layer
let gridLayer: Konva.Layer
let overlay: Konva.Layer
let cursors: Konva.Layer
let transformer: Konva.Transformer
let observer: ResizeObserver | undefined
const nodes = new Map<string, Konva.Group>()
const imageCache = new Map<string, HTMLImageElement>()
let gesture: { x0: number; y0: number; pen: number[]; preview: Konva.Shape | null } | null = null
let marquee: { x0: number; y0: number; shift: boolean; rect: Konva.Rect } | null = null
let pointerDown = false
let lastCursorAt = 0
let suppressViewSave = false

const persistViewport = debounce(() => {
  if (suppressViewSave || !ready.value || !props.boardId) return
  try {
    localStorage.setItem(`raavi.board.viewport.${props.boardId}`, JSON.stringify({ x: round(stage.x()), y: round(stage.y()), scale: round(stage.scaleX()) }))
  } catch {
    /* private mode: the view is kept for this visit only */
  }
}, 200)

const editable = () => !props.readOnly && props.tool === 'select'
const gridKind = () => (props.gridStyle === 'None' || props.gridStyle === 'Line' || props.gridStyle === 'Dot' ? props.gridStyle : 'Dot')

function imageFor(attachmentId: string): HTMLImageElement {
  let img = imageCache.get(attachmentId)
  if (img) return img
  img = new Image()
  imageCache.set(attachmentId, img)
  img.onload = () => { if (ready.value) render() }
  img.src = whiteboardsApi.attachmentUrl(attachmentId)
  return img
}

function buildNode(e: BoardElement): Konva.Group {
  const g = new K.Group({ id: e.id, x: e.x, y: e.y, rotation: e.rotation, draggable: editable() && !e.locked && e.type !== 'Connector' })
  const common = { stroke: e.stroke, strokeWidth: e.strokeWidth }
  switch (e.type) {
    case 'Rect':
      g.add(new K.Rect({ width: e.w, height: e.h, fill: e.fill ?? 'rgba(0,0,0,0.004)', ...common }))
      break
    case 'RoundRect':
      g.add(new K.Rect({ width: e.w, height: e.h, cornerRadius: e.cornerRadius ?? Math.min(e.w, e.h) / 5, fill: e.fill ?? 'rgba(0,0,0,0.004)', ...common }))
      break
    case 'Ellipse':
      g.add(new K.Ellipse({ x: e.w / 2, y: e.h / 2, radiusX: e.w / 2, radiusY: e.h / 2, fill: e.fill ?? 'rgba(0,0,0,0.004)', ...common }))
      break
    case 'Diamond':
      g.add(new K.Line({ points: [e.w / 2, 0, e.w, e.h / 2, e.w / 2, e.h, 0, e.h / 2], closed: true, fill: e.fill ?? 'rgba(0,0,0,0.004)', lineJoin: 'round', ...common }))
      break
    case 'Triangle':
      g.add(new K.Line({ points: [e.w / 2, 0, e.w, e.h, 0, e.h], closed: true, fill: e.fill ?? 'rgba(0,0,0,0.004)', lineJoin: 'round', ...common }))
      break
    case 'Star': {
      const outer = Math.min(e.w, e.h) / 2
      g.add(new K.Star({ x: e.w / 2, y: e.h / 2, numPoints: 5, innerRadius: outer * 0.45, outerRadius: Math.max(outer, 1), fill: e.fill ?? 'rgba(0,0,0,0.004)', ...common }))
      break
    }
    case 'Line':
      g.add(new K.Line({ points: e.points, ...common, lineCap: 'round', hitStrokeWidth: 14 }))
      break
    case 'Sticky':
      g.add(new K.Rect({ width: e.w, height: e.h, fill: e.fill ?? '#fde68a', shadowColor: '#000', shadowOpacity: 0.18, shadowBlur: 6, shadowOffset: { x: 0, y: 2 } }))
      g.add(new K.Text({ x: 10, y: 10, width: Math.max(10, e.w - 20), height: Math.max(10, e.h - 20), text: e.text, fontSize: e.fontSize, fontFamily: FONT, fill: '#1f2937', direction: e.rtl ? 'rtl' : 'ltr', align: e.rtl ? 'right' : 'left', ellipsis: true, listening: false }))
      break
    case 'Text':
      g.add(new K.Text({ text: e.text || ' ', fontSize: e.fontSize, fontFamily: FONT, fill: e.stroke, ...(e.w > 0 ? { width: e.w } : {}), direction: e.rtl ? 'rtl' : 'ltr', align: e.rtl ? 'right' : 'left' }))
      break
    case 'Arrow': {
      const head = 8 + e.strokeWidth * 2
      g.add(new K.Arrow({ points: e.points, ...common, fill: e.stroke, pointerLength: head, pointerWidth: head, lineCap: 'round', hitStrokeWidth: 14 }))
      break
    }
    case 'Connector': {
      const head = 8 + e.strokeWidth * 2
      g.add(new K.Arrow({
        points: e.points.length >= 4 ? e.points : [0, 0, 1, 0],
        ...common,
        fill: e.stroke,
        pointerLength: head,
        pointerWidth: head,
        pointerAtBeginning: !!e.startArrow,
        pointerAtEnding: e.endArrow !== false,
        tension: e.route === 'Curve' ? 0.5 : 0,
        lineCap: 'round',
        lineJoin: 'round',
        hitStrokeWidth: 14,
      }))
      if (e.text) g.add(new K.Text({ x: 6, y: -18, text: e.text, fontSize: 14, fontFamily: FONT, fill: e.stroke, listening: false }))
      break
    }
    case 'Pen':
      g.add(new K.Line({ points: e.points, ...common, opacity: isHighlighter(e) ? 0.35 : 1, lineCap: 'round', lineJoin: 'round', tension: 0.3, hitStrokeWidth: 12 }))
      break
    case 'Image': {
      const img = e.attachmentId ? imageFor(e.attachmentId) : undefined
      if (img && img.complete && img.naturalWidth > 0) g.add(new K.Image({ image: img, width: e.w, height: e.h }))
      else g.add(new K.Rect({ width: e.w, height: e.h, fill: '#e5e7eb', stroke: e.stroke, strokeWidth: 1 }))
      break
    }
    case 'Group':
      g.add(new K.Rect({ width: Math.max(e.w, 8), height: Math.max(e.h, 8), stroke: '#3b82f6', strokeWidth: 1, dash: [6, 4], listening: true }))
      break
    case 'Table':
      addTable(g, e)
      break
  }

  g.on('click tap', (ev) => {
    if (props.tool !== 'select') return
    ev.cancelBubble = true
    const shift = 'shiftKey' in ev.evt && ev.evt.shiftKey
    if (shift) emit('toggle', e.id)
    else emit('select', e.id)
  })
  g.on('dblclick dbltap', (ev) => {
    if (props.readOnly || e.type !== 'Table') return
    ev.cancelBubble = true
    const p = boardPoint()
    openCell(e, cellIndex(e, p.x, p.y))
  })
  if (props.snap) g.dragBoundFunc((pos) => {
    // snap in board units: undo the stage transform, round to the grid, apply it again
    const s = stage.scaleX()
    const snap = (v: number, offset: number) => (snapTo((v - offset) / s, true) * s) + offset
    return { x: snap(pos.x, stage.x()), y: snap(pos.y, stage.y()) }
  })
  g.on('dragend', () => emit('change', e.id, { x: round(g.x()), y: round(g.y()) }))
  g.on('transformend', () => {
    const sx = g.scaleX()
    const sy = g.scaleY()
    const text = g.findOne<Konva.Text>('Text')
    const changes: Partial<BoardElement> = { x: round(g.x()), y: round(g.y()), rotation: round(g.rotation()) }
    if (e.type === 'Text') changes.w = round(Math.max(20, (e.w > 0 ? e.w : (text?.width() ?? 40)) * sx))
    else {
      changes.w = round(Math.max(10, e.w * sx))
      changes.h = round(Math.max(10, e.h * sy))
    }
    g.scale({ x: 1, y: 1 })
    emit('change', e.id, changes)
  })
  return g
}

function addTable(g: Konva.Group, e: BoardElement) {
  const rows = Math.max(1, e.rows || 2)
  const cols = Math.max(1, e.cols || 2)
  const widths = columnWidths(e)
  const rowH = e.h / rows
  g.add(new K.Rect({ width: e.w, height: e.h, fill: e.fill ?? '#ffffff', stroke: e.stroke, strokeWidth: e.strokeWidth }))
  let x = 0
  for (let c = 1; c < cols; c++) {
    x += widths[c - 1] ?? 0
    g.add(new K.Line({ points: [x, 0, x, e.h], stroke: e.stroke, strokeWidth: 1, listening: false }))
  }
  for (let r = 1; r < rows; r++) g.add(new K.Line({ points: [0, r * rowH, e.w, r * rowH], stroke: e.stroke, strokeWidth: 1, listening: false }))
  const cells = e.cells ?? []
  let cx = 0
  for (let c = 0; c < cols; c++) {
    for (let r = 0; r < rows; r++) {
      const text = cells[r * cols + c] ?? ''
      if (!text) continue
      g.add(new K.Text({
        x: cx + 4, y: r * rowH + 4, width: Math.max(8, (widths[c] ?? 0) - 8), height: Math.max(8, rowH - 8),
        text, fontSize: Math.min(e.fontSize, 16), fontFamily: FONT, fill: '#1f2937', ellipsis: true, listening: false,
      }))
    }
    cx += widths[c] ?? 0
  }
}

function cellIndex(e: BoardElement, x: number, y: number): number {
  const cols = Math.max(1, e.cols || 1)
  const rows = Math.max(1, e.rows || 1)
  const widths = columnWidths(e)
  let col = cols - 1
  let acc = 0
  const localX = x - e.x
  for (let i = 0; i < widths.length; i++) {
    acc += widths[i] ?? 0
    if (localX <= acc) { col = i; break }
  }
  const row = Math.min(rows - 1, Math.max(0, Math.floor((y - e.y) / (e.h / rows))))
  return row * cols + col
}

function openCell(e: BoardElement, index: number) {
  if (!ready.value) return
  const cols = Math.max(1, e.cols || 1)
  const rows = Math.max(1, e.rows || 1)
  const widths = columnWidths(e)
  const col = index % cols
  const row = Math.floor(index / cols)
  let x = e.x
  for (let i = 0; i < col; i++) x += widths[i] ?? 0
  const y = e.y + (e.h / rows) * row
  const s = stage.scaleX()
  cellEdit.value = {
    id: e.id,
    index,
    value: e.cells?.[index] ?? '',
    style: {
      insetInlineStart: `${stage.x() + x * s}px`,
      insetBlockStart: `${stage.y() + y * s}px`,
      inlineSize: `${Math.max(24, (widths[col] ?? 40) * s)}px`,
      blockSize: `${Math.max(20, (e.h / rows) * s)}px`,
    },
  }
}

function commitCell() {
  const edit = cellEdit.value
  if (!edit) return
  cellEdit.value = null
  const e = props.elements.find((x) => x.id === edit.id)
  if (!e || e.type !== 'Table') return
  const size = Math.max(1, e.rows || 1) * Math.max(1, e.cols || 1)
  const cells = [...(e.cells ?? [])]
  while (cells.length < size) cells.push('')
  if (cells[edit.index] === edit.value) return
  cells[edit.index] = edit.value
  emit('change', e.id, { cells })
}

function attachTransformer() {
  const selected = props.selectedId ? nodes.get(props.selectedId) : undefined
  const element = props.elements.find((x) => x.id === props.selectedId)
  const fixed = !!element && (element.type === 'Arrow' || element.type === 'Pen' || element.type === 'Line' || element.type === 'Connector' || element.type === 'Group' || element.locked)
  const reshapable = !!selected && !!element && !fixed && editable()
  transformer.nodes(reshapable ? [selected] : [])
  transformer.enabledAnchors(element?.type === 'Text' ? ['middle-left', 'middle-right'] : ['top-left', 'top-right', 'bottom-left', 'bottom-right', 'middle-left', 'middle-right', 'top-center', 'bottom-center'])
  content.find('.selection').forEach((n) => n.destroy())
  content.find('.multi').forEach((n) => n.destroy())
  if (selected && element && !reshapable) {
    const box = selected.getClientRect({ relativeTo: selected })
    selected.add(new K.Rect({ name: 'selection', x: box.x - 4, y: box.y - 4, width: box.width + 8, height: box.height + 8, stroke: '#3b82f6', dash: [4, 3], listening: false }))
  }
  const ids = props.selectedIds ?? []
  for (const id of ids) {
    if (id === props.selectedId) continue
    const node = nodes.get(id)
    if (!node) continue
    const box = node.getClientRect({ relativeTo: node })
    node.add(new K.Rect({ name: 'multi', x: box.x - 3, y: box.y - 3, width: box.width + 6, height: box.height + 6, stroke: '#3b82f6', dash: [4, 3], listening: false }))
  }
  overlay.batchDraw()
  content.batchDraw()
}

function inViewport(e: BoardElement, pad: number): boolean {
  const s = stage.scaleX() || 1
  const left = -stage.x() / s - pad
  const top = -stage.y() / s - pad
  const right = left + stage.width() / s + pad * 2
  const bottom = top + stage.height() / s + pad * 2
  const w = e.w || 1
  const h = e.h || 1
  if (e.type === 'Pen' || e.type === 'Arrow' || e.type === 'Line' || e.type === 'Connector') {
    const pts = e.points
    for (let i = 0; i + 1 < pts.length; i += 2) {
      const x = e.x + pts[i]!
      const y = e.y + pts[i + 1]!
      if (x >= left && x <= right && y >= top && y <= bottom) return true
    }
    return pts.length === 0
  }
  return e.x + w >= left && e.x <= right && e.y + h >= top && e.y <= bottom
}

function render() {
  if (!ready.value) return
  content.destroyChildren()
  nodes.clear()
  for (const e of props.elements) {
    if (e.hidden || !inViewport(e, 200)) continue
    const node = buildNode(e)
    nodes.set(e.id, node)
    content.add(node)
  }
  for (const e of props.elements) if (e.type === 'Group') nodes.get(e.id)?.moveToBottom()
  stage.draggable(props.tool === 'hand')
  stage.container().style.cursor = props.tool === 'hand' ? 'grab' : props.tool === 'eraser' ? 'cell' : CREATION.includes(props.tool) && !props.readOnly ? 'crosshair' : 'default'
  attachTransformer()
  drawCursors()
}

const boardPoint = () => content.getRelativePointerPosition() ?? { x: 0, y: 0 }
const pointer = () => {
  const p = boardPoint()
  const free = props.tool === 'pen' || props.tool === 'highlighter' || props.tool === 'eraser'
  return props.snap && !free ? { x: snapTo(p.x, true), y: snapTo(p.y, true) } : p
}

/** Dots or lines of the grid over the visible part of the board (drawn behind the content, never exported). */
function drawGrid(konvaContext: Konva.Context) {
  if (!props.grid || gridKind() === 'None') return
  const ctx = (konvaContext as unknown as { _context: CanvasRenderingContext2D })._context
  const s = stage.scaleX()
  const step = s < 0.5 ? GRID * 5 : GRID
  const left = -stage.x() / s
  const top = -stage.y() / s
  const right = left + stage.width() / s
  const bottom = top + stage.height() / s
  if (gridKind() === 'Line') {
    ctx.strokeStyle = '#d5d9e2'
    ctx.lineWidth = 1 / s
    ctx.beginPath()
    for (let x = Math.floor(left / step) * step; x <= right; x += step) { ctx.moveTo(x, top); ctx.lineTo(x, bottom) }
    for (let y = Math.floor(top / step) * step; y <= bottom; y += step) { ctx.moveTo(left, y); ctx.lineTo(right, y) }
    ctx.stroke()
    return
  }
  ctx.fillStyle = '#c4c9d4'
  const r = Math.max(0.8, 1.2 / s)
  for (let x = Math.floor(left / step) * step; x <= right; x += step) {
    for (let y = Math.floor(top / step) * step; y <= bottom; y += step) {
      ctx.beginPath()
      ctx.arc(x, y, r, 0, Math.PI * 2)
      ctx.fill()
    }
  }
}
const redrawGrid = () => gridLayer?.batchDraw()

function drawCursors() {
  if (!cursors) return
  cursors.destroyChildren()
  for (const peer of props.peers ?? []) {
    for (const id of peer.selectedIds ?? []) {
      const e = props.elements.find((x) => x.id === id)
      if (!e) continue
      const b = bounds([e])
      if (!b) continue
      cursors.add(new K.Rect({ x: b.x - 3, y: b.y - 3, width: b.w + 6, height: b.h + 6, stroke: peer.color, dash: [5, 4], listening: false }))
    }
    if (!peer.cursor) continue
    const g = new K.Group({ x: peer.cursor.x, y: peer.cursor.y, listening: false })
    g.add(new K.Line({ points: [0, 0, 0, 14, 4, 11, 7, 18, 9, 17, 6, 10, 12, 10], closed: true, fill: peer.color, stroke: '#ffffff', strokeWidth: 1 }))
    const label = new K.Label({ x: 14, y: 16 })
    label.add(new K.Tag({ fill: peer.color, cornerRadius: 3 }))
    label.add(new K.Text({ text: peer.name, fontSize: 12, fontFamily: FONT, fill: '#ffffff', padding: 3 }))
    g.add(label)
    cursors.add(g)
  }
  cursors.batchDraw()
}

function emitCursor() {
  const now = performance.now()
  if (now - lastCursorAt < 40) return
  lastCursorAt = now
  const p = boardPoint()
  emit('cursor', { x: round(p.x), y: round(p.y) })
}

function eraseHere() {
  const p = boardPoint()
  for (const e of props.elements) {
    if (e.type !== 'Pen' || e.hidden || e.locked) continue
    const parts = erasePen(e, p.x, p.y)
    if (parts.length === 1 && parts[0] === e) continue
    emit('erase', e.id, p.x, p.y)
  }
}

function start() {
  if (props.readOnly) return
  if (props.tool === 'select' || props.tool === 'eraser' || props.tool === 'image') return
  if (!CREATION.includes(props.tool)) return
  const { x, y } = pointer()
  gesture = { x0: x, y0: y, pen: [x, y], preview: null }
  if (props.tool === 'text') return
  const dash = { stroke: '#3b82f6', dash: [5, 4], listening: false }
  gesture.preview = LINE_TOOLS.includes(props.tool) ? new K.Line({ points: [x, y, x, y], ...dash }) : new K.Rect({ x, y, width: 0, height: 0, ...dash })
  overlay.add(gesture.preview)
}

function move() {
  emitCursor()
  if (marquee) {
    const { x, y } = pointer()
    marquee.rect.setAttrs({ x: Math.min(marquee.x0, x), y: Math.min(marquee.y0, y), width: Math.abs(x - marquee.x0), height: Math.abs(y - marquee.y0) })
    overlay.batchDraw()
    return
  }
  if (pointerDown && props.tool === 'eraser' && !props.readOnly) eraseHere()
  if (!gesture?.preview) return
  const { x, y } = pointer()
  if (props.tool === 'pen' || props.tool === 'highlighter') {
    gesture.pen.push(x, y)
    ;(gesture.preview as Konva.Line).points(gesture.pen)
  } else if (props.tool === 'arrow' || props.tool === 'line' || props.tool === 'connector') {
    ;(gesture.preview as Konva.Line).points([gesture.x0, gesture.y0, x, y])
  } else {
    gesture.preview.setAttrs({ x: Math.min(gesture.x0, x), y: Math.min(gesture.y0, y), width: Math.abs(x - gesture.x0), height: Math.abs(y - gesture.y0) })
  }
  overlay.batchDraw()
}

function finishConnector(x0: number, y0: number, x1: number, y1: number) {
  const from = shapeAt(props.elements, x0, y0)
  const to = shapeAt(props.elements, x1, y1)
  if (!from || !to || from.id === to.id) return
  const fromPort = nearestPort(from, x0, y0)
  const toPort = nearestPort(to, x1, y1)
  const abs = connectorPoints(from, to, fromPort, toPort, 'Orthogonal')
  emit('create', createConnector(from.id, to.id, abs, { stroke: props.stroke, fromPort, toPort, route: 'Orthogonal' }))
}

function finish() {
  if (marquee) {
    const box = { x: marquee.rect.x(), y: marquee.rect.y(), w: marquee.rect.width(), h: marquee.rect.height() }
    const shift = marquee.shift
    marquee.rect.destroy()
    marquee = null
    overlay.batchDraw()
    if (Math.hypot(box.w, box.h) < 4) emit('select', null)
    else {
      const ids = hitsMarquee(props.elements, box)
      emit('marquee', shift ? [...new Set([...(props.selectedIds ?? []), ...ids])] : ids)
    }
    return
  }
  if (!gesture) return
  const { x, y } = pointer()
  const g = gesture
  gesture = null
  g.preview?.destroy()
  overlay.batchDraw()
  if (props.tool === 'connector') {
    finishConnector(g.x0, g.y0, x, y)
    return
  }
  const element = elementFromGesture(props.tool, g.x0, g.y0, x, y, g.pen, { stroke: props.stroke, fill: props.fill })
  if (element) emit('create', element)
}

function onDown(ev: Konva.KonvaEventObject<MouseEvent | TouchEvent>) {
  pointerDown = true
  if (props.readOnly) return
  if (props.tool === 'eraser') {
    eraseHere()
    return
  }
  if (props.tool === 'select' && ev.target === stage) {
    const { x, y } = pointer()
    const shift = 'shiftKey' in ev.evt && ev.evt.shiftKey
    const rect = new K.Rect({ x, y, width: 0, height: 0, stroke: '#3b82f6', dash: [4, 3], fill: 'rgba(59,130,246,0.12)', listening: false })
    marquee = { x0: x, y0: y, shift, rect }
    overlay.add(rect)
    return
  }
  start()
}

function onWheel(ev: Konva.KonvaEventObject<WheelEvent>) {
  ev.evt.preventDefault()
  if (ev.evt.ctrlKey || ev.evt.metaKey) {
    const p = stage.getPointerPosition()
    if (p) zoomAt(ev.evt.deltaY < 0 ? 1.1 : 1 / 1.1, p.x, p.y)
  } else {
    stage.position({ x: stage.x() - ev.evt.deltaX, y: stage.y() - ev.evt.deltaY })
  }
  redrawGrid()
  persistViewport()
}

function zoomAt(factor: number, cx: number, cy: number) {
  const old = stage.scaleX()
  const next = Math.min(4, Math.max(0.2, old * factor))
  const origin = { x: (cx - stage.x()) / old, y: (cy - stage.y()) / old }
  stage.scale({ x: next, y: next })
  stage.position({ x: cx - origin.x * next, y: cy - origin.y * next })
  redrawGrid()
  persistViewport()
}

function applySavedViewport() {
  if (!props.boardId) return
  try {
    const raw = localStorage.getItem(`raavi.board.viewport.${props.boardId}`)
    if (!raw) return
    const v = JSON.parse(raw) as { x?: unknown; y?: unknown; scale?: unknown }
    if (typeof v.x !== 'number' || typeof v.y !== 'number' || typeof v.scale !== 'number') return
    if (!Number.isFinite(v.x) || !Number.isFinite(v.y) || !Number.isFinite(v.scale)) return
    const scale = Math.min(4, Math.max(0.2, v.scale))
    stage.scale({ x: scale, y: scale })
    stage.position({ x: v.x, y: v.y })
  } catch {
    /* ignore a broken saved view */
  }
}

onMounted(async () => {
  K = (await import('konva')).default
  const el = host.value
  if (!el) return
  stage = new K.Stage({ container: el, width: el.clientWidth || 800, height: el.clientHeight || 520 })
  gridLayer = new K.Layer({ listening: false })
  gridLayer.add(new K.Shape({ listening: false, sceneFunc: (ctx) => drawGrid(ctx) }))
  content = new K.Layer()
  overlay = new K.Layer()
  cursors = new K.Layer({ listening: false })
  transformer = new K.Transformer({ rotateEnabled: true, keepRatio: false, ignoreStroke: true, borderStroke: '#3b82f6', anchorStroke: '#3b82f6', boundBoxFunc: (_old, box) => (Math.abs(box.width) < 10 || Math.abs(box.height) < 10 ? _old : box) })
  overlay.add(transformer)
  stage.add(gridLayer, content, overlay, cursors)
  stage.on('dragmove', () => { redrawGrid(); persistViewport() })
  stage.on('dragend', () => { render(); persistViewport() })
  stage.on('mousedown touchstart', onDown)
  stage.on('mousemove touchmove', move)
  stage.on('mouseup touchend', () => { pointerDown = false; finish() })
  stage.on('wheel', (ev) => { onWheel(ev); render() })
  stage.on('mouseleave', () => {
    pointerDown = false
    if (gesture || marquee) finish()
    emit('cursor', null)
  })
  observer = new ResizeObserver(() => {
    stage.size({ width: el.clientWidth, height: el.clientHeight })
    redrawGrid()
  })
  observer.observe(el)
  ready.value = true
  applySavedViewport()
  render()
})

onBeforeUnmount(() => {
  persistViewport.cancel()
  observer?.disconnect()
  stage?.destroy()
})

watch(() => [props.elements, props.tool, props.readOnly, props.snap], render)
watch(() => [props.grid, props.gridStyle], redrawGrid)
watch(() => props.selectedId, () => { if (ready.value) attachTransformer() })
watch(() => props.selectedIds, () => { if (ready.value) attachTransformer() }, { deep: true })
watch(() => props.peers, () => { if (ready.value) drawCursors() }, { deep: true })
watch(() => props.tool, () => { cellEdit.value = null })

function zoomBy(factor: number) {
  if (!ready.value) return
  zoomAt(factor, stage.width() / 2, stage.height() / 2)
}

function place(b: { x: number; y: number; w: number; h: number }, pad: number, maxScale: number) {
  const scale = Math.min(maxScale, Math.max(0.2, Math.min((stage.width() - pad * 2) / Math.max(b.w, 1), (stage.height() - pad * 2) / Math.max(b.h, 1))))
  stage.scale({ x: scale, y: scale })
  stage.position({ x: (stage.width() - b.w * scale) / 2 - b.x * scale, y: (stage.height() - b.h * scale) / 2 - b.y * scale })
  redrawGrid()
  persistViewport()
}

function resetView() {
  if (!ready.value) return
  const b = bounds(props.elements)
  stage.scale({ x: 1, y: 1 })
  if (!b) {
    stage.position({ x: 0, y: 0 })
    persistViewport()
    return
  }
  place(b, 60, 1.5)
}

/** Fits the view to the current selection (or the primary element). */
function fitSelection() {
  if (!ready.value) return
  const ids = props.selectedIds?.length ? props.selectedIds : props.selectedId ? [props.selectedId] : []
  const b = bounds(props.elements.filter((e) => ids.includes(e.id)))
  if (!b) return
  place(b, 80, 2)
}

/** PNG of the whole board at 2× (independent of the current pan and zoom), or null for an empty board. */
function toPng(): string | null {
  const b = bounds(props.elements)
  if (!ready.value || !b) return null
  const pad = 40
  suppressViewSave = true
  const saved = { scale: stage.scale(), position: stage.position(), size: stage.size() }
  transformer.visible(false)
  gridLayer.visible(false)
  cursors.visible(false)
  content.find('.selection').forEach((n) => n.visible(false))
  content.find('.multi').forEach((n) => n.visible(false))
  const bg = new K.Rect({ x: b.x - pad, y: b.y - pad, width: b.w + pad * 2, height: b.h + pad * 2, fill: safeBackground.value, listening: false })
  content.add(bg)
  bg.moveToBottom()
  stage.scale({ x: 1, y: 1 })
  stage.position({ x: -(b.x - pad), y: -(b.y - pad) })
  stage.size({ width: Math.ceil(b.w + pad * 2), height: Math.ceil(b.h + pad * 2) })
  content.draw()
  const url = stage.toDataURL({ pixelRatio: 2, mimeType: 'image/png' })
  bg.destroy()
  transformer.visible(true)
  gridLayer.visible(true)
  cursors.visible(true)
  content.find('.selection').forEach((n) => n.visible(true))
  content.find('.multi').forEach((n) => n.visible(true))
  stage.size(saved.size)
  stage.scale(saved.scale)
  stage.position(saved.position)
  stage.batchDraw()
  suppressViewSave = false
  return url
}

/** Centre of what the person sees, in board coordinates (where inserted templates go). */
function viewCenter(): { x: number; y: number } {
  if (!ready.value) return { x: 0, y: 0 }
  const s = stage.scaleX()
  return { x: (stage.width() / 2 - stage.x()) / s, y: (stage.height() / 2 - stage.y()) / s }
}

/** Fits the view to the given element (zoom to selection). */
function focusOn(e: BoardElement) {
  if (!ready.value) return
  const b = bounds([e])
  if (!b) return
  place(b, 120, 2)
}

defineExpose({ zoomBy, resetView, fitSelection, toPng, viewCenter, focusOn })
</script>

<template>
  <div class="wrap" dir="ltr">
    <div ref="host" class="board" role="img" :aria-label="label" data-testid="board-canvas" :style="{ background: safeBackground }" />
    <textarea
      v-if="cellEdit"
      v-model="cellEdit.value"
      class="cell-edit"
      :style="cellEdit.style"
      dir="auto"
      maxlength="2000"
      data-testid="cell-editor"
      @blur="commitCell"
      @keydown.enter.prevent="commitCell"
      @keydown.escape.prevent="cellEdit = null"
    />
  </div>
</template>

<style scoped>
.wrap { position: relative; inline-size: 100%; block-size: 100%; min-block-size: 24rem; }
.board { inline-size: 100%; block-size: 100%; min-block-size: 24rem; touch-action: none; outline: none; }
.board :deep(canvas) { display: block; }
.cell-edit {
  position: absolute;
  z-index: 2;
  margin: 0;
  padding: 2px;
  box-sizing: border-box;
  font: inherit;
  resize: none;
  border: 2px solid var(--raavi-primary, #2563eb);
  background: var(--raavi-surface, #fff);
  color: inherit;
}
</style>
