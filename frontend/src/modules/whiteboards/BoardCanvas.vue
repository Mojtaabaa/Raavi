<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import type Konva from 'konva'
import type { BoardElement } from '@/core/api/whiteboards'
import { bounds, elementFromGesture, GRID, round, snapTo, type Tool } from './boardModel'

/**
 * The drawing surface (Konva). It only draws `elements` and reports gestures (select, create, change); the parent owns the model and the
 * history. Selected shapes get a transformer (resize/rotate); arrows and pen strokes can be moved but not reshaped.
 */
const props = defineProps<{ elements: BoardElement[]; tool: Tool; selectedId: string | null; readOnly: boolean; stroke: string; fill: string | null; label: string; snap?: boolean; grid?: boolean }>()
const emit = defineEmits<{ select: [id: string | null]; create: [element: BoardElement]; change: [id: string, changes: Partial<BoardElement>] }>()

const host = ref<HTMLDivElement | null>(null)
const ready = ref(false)
const FONT = "Calibri, 'Segoe UI', Vazirmatn, Tahoma, sans-serif"
const CREATION: Tool[] = ['rect', 'ellipse', 'diamond', 'triangle', 'sticky', 'text', 'arrow', 'line', 'pen']

let K: typeof Konva
let stage: Konva.Stage
let content: Konva.Layer
let gridLayer: Konva.Layer
let overlay: Konva.Layer
let transformer: Konva.Transformer
let observer: ResizeObserver | undefined
const nodes = new Map<string, Konva.Group>()
let gesture: { x0: number; y0: number; pen: number[]; preview: Konva.Shape | null } | null = null

const editable = () => !props.readOnly && props.tool === 'select'

function buildNode(e: BoardElement): Konva.Group {
  const g = new K.Group({ id: e.id, x: e.x, y: e.y, rotation: e.rotation, draggable: editable() })
  const common = { stroke: e.stroke, strokeWidth: e.strokeWidth }
  switch (e.type) {
    case 'Rect':
      g.add(new K.Rect({ width: e.w, height: e.h, fill: e.fill ?? 'rgba(0,0,0,0.004)', ...common }))
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
    case 'Line':
      g.add(new K.Line({ points: e.points, ...common, lineCap: 'round', hitStrokeWidth: 14 }))
      break
    case 'Sticky':
      g.add(new K.Rect({ width: e.w, height: e.h, fill: e.fill ?? '#fde68a', shadowColor: '#000', shadowOpacity: 0.18, shadowBlur: 6, shadowOffset: { x: 0, y: 2 } }))
      g.add(new K.Text({ x: 10, y: 10, width: Math.max(10, e.w - 20), height: Math.max(10, e.h - 20), text: e.text, fontSize: e.fontSize, fontFamily: FONT, fill: '#1f2937', direction: e.rtl ? 'rtl' : 'ltr', align: e.rtl ? 'right' : 'left', ellipsis: true }))
      break
    case 'Text':
      g.add(new K.Text({ text: e.text || ' ', fontSize: e.fontSize, fontFamily: FONT, fill: e.stroke, ...(e.w > 0 ? { width: e.w } : {}), direction: e.rtl ? 'rtl' : 'ltr', align: e.rtl ? 'right' : 'left' }))
      break
    case 'Arrow': {
      const head = 8 + e.strokeWidth * 2
      g.add(new K.Arrow({ points: e.points, ...common, fill: e.stroke, pointerLength: head, pointerWidth: head, lineCap: 'round', hitStrokeWidth: 14 }))
      break
    }
    case 'Pen':
      g.add(new K.Line({ points: e.points, ...common, lineCap: 'round', lineJoin: 'round', tension: 0.3, hitStrokeWidth: 12 }))
      break
  }

  g.on('click tap', (ev) => {
    if (props.tool === 'select') {
      ev.cancelBubble = true
      emit('select', e.id)
    }
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

function attachTransformer() {
  const selected = props.selectedId ? nodes.get(props.selectedId) : undefined
  const element = props.elements.find((x) => x.id === props.selectedId)
  const reshapable = !!selected && !!element && element.type !== 'Arrow' && element.type !== 'Pen' && element.type !== 'Line' && editable()
  transformer.nodes(reshapable ? [selected] : [])
  transformer.enabledAnchors(element?.type === 'Text' ? ['middle-left', 'middle-right'] : ['top-left', 'top-right', 'bottom-left', 'bottom-right', 'middle-left', 'middle-right', 'top-center', 'bottom-center'])
  // Arrows and pen strokes get an outline instead, so the selection is visible.
  content.find('.selection').forEach((n) => n.destroy())
  if (selected && element && !reshapable) {
    const box = selected.getClientRect({ relativeTo: selected })
    selected.add(new K.Rect({ name: 'selection', x: box.x - 4, y: box.y - 4, width: box.width + 8, height: box.height + 8, stroke: '#3b82f6', dash: [4, 3], listening: false }))
  }

  overlay.batchDraw()
  content.batchDraw()
}

function render() {
  if (!ready.value) return
  content.destroyChildren()
  nodes.clear()
  for (const e of props.elements) {
    const node = buildNode(e)
    nodes.set(e.id, node)
    content.add(node)
  }

  stage.draggable(props.tool === 'hand')
  stage.container().style.cursor = props.tool === 'hand' ? 'grab' : CREATION.includes(props.tool) && !props.readOnly ? 'crosshair' : 'default'
  attachTransformer()
}

const pointer = () => {
  const p = content.getRelativePointerPosition() ?? { x: 0, y: 0 }
  return props.snap && props.tool !== 'pen' ? { x: snapTo(p.x, true), y: snapTo(p.y, true) } : p
}

/** Dots of the grid over the visible part of the board (drawn behind the content, never exported). */
function drawGrid(konvaContext: Konva.Context) {
  if (!props.grid) return
  const ctx = (konvaContext as unknown as { _context: CanvasRenderingContext2D })._context
  const s = stage.scaleX()
  const step = s < 0.5 ? GRID * 5 : GRID
  const left = -stage.x() / s
  const top = -stage.y() / s
  const right = left + stage.width() / s
  const bottom = top + stage.height() / s
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

function start() {
  if (props.readOnly) return
  if (props.tool === 'select') return
  if (!CREATION.includes(props.tool)) return
  const { x, y } = pointer()
  gesture = { x0: x, y0: y, pen: [x, y], preview: null }
  if (props.tool === 'text') return
  const dash = { stroke: '#3b82f6', dash: [5, 4], listening: false }
  gesture.preview = props.tool === 'pen' || props.tool === 'arrow' || props.tool === 'line' ? new K.Line({ points: [x, y, x, y], ...dash }) : new K.Rect({ x, y, width: 0, height: 0, ...dash })
  overlay.add(gesture.preview)
}

function move() {
  if (!gesture?.preview) return
  const { x, y } = pointer()
  if (props.tool === 'pen') {
    gesture.pen.push(x, y)
    ;(gesture.preview as Konva.Line).points(gesture.pen)
  } else if (props.tool === 'arrow' || props.tool === 'line') {
    ;(gesture.preview as Konva.Line).points([gesture.x0, gesture.y0, x, y])
  } else {
    gesture.preview.setAttrs({ x: Math.min(gesture.x0, x), y: Math.min(gesture.y0, y), width: Math.abs(x - gesture.x0), height: Math.abs(y - gesture.y0) })
  }

  overlay.batchDraw()
}

function finish() {
  if (!gesture) return
  const { x, y } = pointer()
  const g = gesture
  gesture = null
  g.preview?.destroy()
  overlay.batchDraw()
  const element = elementFromGesture(props.tool, g.x0, g.y0, x, y, g.pen, { stroke: props.stroke, fill: props.fill })
  if (element) emit('create', element)
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
}

function zoomAt(factor: number, cx: number, cy: number) {
  const old = stage.scaleX()
  const next = Math.min(4, Math.max(0.2, old * factor))
  const origin = { x: (cx - stage.x()) / old, y: (cy - stage.y()) / old }
  stage.scale({ x: next, y: next })
  stage.position({ x: cx - origin.x * next, y: cy - origin.y * next })
  redrawGrid()
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
  transformer = new K.Transformer({ rotateEnabled: true, keepRatio: false, ignoreStroke: true, borderStroke: '#3b82f6', anchorStroke: '#3b82f6', boundBoxFunc: (_old, box) => (Math.abs(box.width) < 10 || Math.abs(box.height) < 10 ? _old : box) })
  overlay.add(transformer)
  stage.add(gridLayer, content, overlay)
  stage.on('dragmove', redrawGrid)
  stage.on('mousedown touchstart', (ev) => {
    if (ev.target === stage && props.tool === 'select') emit('select', null)
    start()
  })
  stage.on('mousemove touchmove', move)
  stage.on('mouseup touchend', finish)
  stage.on('wheel', onWheel)
  stage.on('mouseleave', () => {
    if (gesture) finish()
  })
  observer = new ResizeObserver(() => {
    stage.size({ width: el.clientWidth, height: el.clientHeight })
    redrawGrid()
  })
  observer.observe(el)
  ready.value = true
  render()
})

onBeforeUnmount(() => {
  observer?.disconnect()
  stage?.destroy()
})

watch(() => [props.elements, props.tool, props.readOnly, props.snap], render)
watch(() => props.grid, redrawGrid)
watch(() => props.selectedId, attachTransformer)

function zoomBy(factor: number) {
  if (!ready.value) return
  zoomAt(factor, stage.width() / 2, stage.height() / 2)
}

function resetView() {
  if (!ready.value) return
  const b = bounds(props.elements)
  stage.scale({ x: 1, y: 1 })
  if (!b) {
    stage.position({ x: 0, y: 0 })
    return
  }

  const pad = 60
  const scale = Math.min(1.5, Math.max(0.2, Math.min((stage.width() - pad * 2) / Math.max(b.w, 1), (stage.height() - pad * 2) / Math.max(b.h, 1))))
  stage.scale({ x: scale, y: scale })
  stage.position({ x: (stage.width() - b.w * scale) / 2 - b.x * scale, y: (stage.height() - b.h * scale) / 2 - b.y * scale })
  redrawGrid()
}

/** PNG of the whole board at 2× (independent of the current pan and zoom), or null for an empty board. */
function toPng(): string | null {
  const b = bounds(props.elements)
  if (!ready.value || !b) return null
  const pad = 40
  const saved = { scale: stage.scale(), position: stage.position(), size: stage.size() }
  transformer.visible(false)
  gridLayer.visible(false)
  content.find('.selection').forEach((n) => n.visible(false))
  const bg = new K.Rect({ x: b.x - pad, y: b.y - pad, width: b.w + pad * 2, height: b.h + pad * 2, fill: '#ffffff', listening: false })
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
  content.find('.selection').forEach((n) => n.visible(true))
  stage.size(saved.size)
  stage.scale(saved.scale)
  stage.position(saved.position)
  stage.batchDraw()
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
  const pad = 120
  const scale = Math.min(2, Math.max(0.2, Math.min((stage.width() - pad * 2) / Math.max(b.w, 1), (stage.height() - pad * 2) / Math.max(b.h, 1))))
  stage.scale({ x: scale, y: scale })
  stage.position({ x: (stage.width() - b.w * scale) / 2 - b.x * scale, y: (stage.height() - b.h * scale) / 2 - b.y * scale })
  redrawGrid()
}

defineExpose({ zoomBy, resetView, toPng, viewCenter, focusOn })
</script>

<template>
  <div ref="host" class="board" role="img" :aria-label="label" data-testid="board-canvas" />
</template>

<style scoped>
.board { inline-size: 100%; block-size: 100%; min-block-size: 24rem; background: #fff; touch-action: none; outline: none; }
.board :deep(canvas) { display: block; }
</style>
