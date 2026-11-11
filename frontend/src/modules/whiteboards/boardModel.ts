import type { BoardElement, BoardPort, BoardRoute, BoardShape } from '@/core/api/whiteboards'

/** Pure whiteboard logic (no canvas): element factories, edits, history and geometry. The canvas component only draws and reports gestures. */

export type Tool = 'select' | 'hand' | 'rect' | 'ellipse' | 'diamond' | 'triangle' | 'sticky' | 'text' | 'arrow' | 'line' | 'pen' | 'roundRect' | 'star' | 'image' | 'connector' | 'highlighter' | 'eraser' | 'table'

/** Grid step in board units: the size of the grid dots, and the step positions snap to when "snap to grid" is on. */
export const GRID = 20

/** Pen strokes at least this wide are highlighters and are drawn at 35% opacity. Inspector widths stop at 8. */
export const HIGHLIGHTER_WIDTH = 12

/** Position snapped to the grid (identity when snapping is off). */
export const snapTo = (value: number, on: boolean): number => (on ? Math.round(value / GRID) * GRID : value)

export const PALETTE = ['#1f2937', '#ef4444', '#f59e0b', '#10b981', '#3b82f6', '#8b5cf6', '#ec4899', '#ffffff'] as const
export const FILLS = ['#fde68a', '#fecaca', '#bbf7d0', '#bfdbfe', '#ddd6fe', '#fbcfe8', '#e5e7eb'] as const
export const STROKE_WIDTHS = [1, 2, 4, 8] as const
export const MAX_ELEMENTS = 2000

const RTL_CHAR = /[֐-ࣿיִ-﷿ﹰ-﻿]/
const LTR_CHAR = /[A-Za-zÀ-ɏ]/

/** Text direction from the first strong character, like `dir="auto"`. */
export function isRtlText(text: string): boolean {
  for (const ch of text) {
    if (RTL_CHAR.test(ch)) return true
    if (LTR_CHAR.test(ch)) return false
  }
  return false
}

export function newId(): string {
  const c = globalThis.crypto
  if (c && typeof c.randomUUID === 'function') return c.randomUUID().replace(/-/g, '').slice(0, 16)
  return Math.random().toString(36).slice(2, 10) + Date.now().toString(36)
}

const base = (type: BoardShape, x: number, y: number): BoardElement => ({
  id: newId(), type, x, y, w: 0, h: 0, rotation: 0, stroke: PALETTE[0], fill: null, strokeWidth: 2, text: '', fontSize: 20, rtl: false, points: [],
})

/** Default size of a shape created by a plain click instead of a drag. */
const CLICK_SIZE: Partial<Record<BoardShape, [number, number]>> = {
  Rect: [140, 90], Ellipse: [140, 90], Diamond: [140, 110], Triangle: [130, 110], Sticky: [170, 130], RoundRect: [140, 90], Star: [120, 120], Table: [240, 140],
}

const SHAPE: Partial<Record<Tool, BoardShape>> = {
  rect: 'Rect', ellipse: 'Ellipse', diamond: 'Diamond', triangle: 'Triangle', sticky: 'Sticky', roundRect: 'RoundRect', star: 'Star', table: 'Table',
}

/** Builds the element for a gesture from (x0, y0) to (x1, y1); a tiny drag counts as a click. */
export function elementFromGesture(tool: Tool, x0: number, y0: number, x1: number, y1: number, pen: number[] = [], options: { stroke?: string; fill?: string | null } = {}): BoardElement | null {
  const dragged = Math.hypot(x1 - x0, y1 - y0) >= 6
  const stroke = options.stroke ?? PALETTE[0]
  const shape = SHAPE[tool]
  if (shape) {
    const el = base(shape, Math.min(x0, x1), Math.min(y0, y1))
    const [w, h] = dragged ? [Math.abs(x1 - x0), Math.abs(y1 - y0)] : CLICK_SIZE[shape]!
    Object.assign(el, { w: Math.max(w, 10), h: Math.max(h, 10), stroke, fill: options.fill ?? (shape === 'Sticky' ? FILLS[0] : null) })
    if (shape === 'Sticky') el.stroke = PALETTE[0]
    if (shape === 'RoundRect') el.cornerRadius = round(Math.min(el.w, el.h) / 5)
    if (shape === 'Table') {
      const rows = 3
      const cols = 3
      Object.assign(el, { rows, cols, cells: Array.from({ length: rows * cols }, () => ''), colWidths: Array.from({ length: cols }, () => round(el.w / cols)), fill: options.fill ?? '#ffffff' })
    }
    return el
  }
  switch (tool) {
    case 'text': {
      const el = base('Text', x0, y0)
      Object.assign(el, { text: '', stroke, fontSize: 24 })
      return el
    }
    case 'arrow':
    case 'line': {
      if (!dragged) return null
      const el = base(tool === 'line' ? 'Line' : 'Arrow', x0, y0)
      Object.assign(el, { points: [0, 0, x1 - x0, y1 - y0], stroke })
      return el
    }
    case 'pen':
    case 'highlighter': {
      const points = simplify(pen)
      if (points.length < 4) return null
      const [ox, oy] = [points[0]!, points[1]!]
      const el = base('Pen', ox, oy)
      Object.assign(el, { points: points.map((v, i) => round(v - (i % 2 === 0 ? ox : oy))), stroke, strokeWidth: tool === 'highlighter' ? HIGHLIGHTER_WIDTH : 2 })
      return el
    }
    default:
      return null
  }
}

/** Drops points closer than `min` pixels to the previous kept point (keeps the last one) and caps the stroke at 2 000 points. */
export function simplify(points: number[], min = 2): number[] {
  const out: number[] = []
  for (let i = 0; i + 1 < points.length; i += 2) {
    const x = points[i]!
    const y = points[i + 1]!
    const lx = out[out.length - 2]
    const ly = out[out.length - 1]
    if (lx === undefined || ly === undefined || Math.hypot(x - lx, y - ly) >= min) out.push(x, y)
  }
  const lastX = points[points.length - 2]
  const lastY = points[points.length - 1]
  if (lastX !== undefined && lastY !== undefined && (out[out.length - 2] !== lastX || out[out.length - 1] !== lastY)) out.push(lastX, lastY)
  if (out.length > 4000) {
    const step = Math.ceil(out.length / 4000)
    const thin: number[] = []
    for (let i = 0; i + 1 < out.length; i += 2 * step) thin.push(out[i]!, out[i + 1]!)
    thin.push(out[out.length - 2]!, out[out.length - 1]!)
    return thin.slice(0, 4000)
  }
  return out
}

export const round = (n: number): number => Math.round(n * 100) / 100

export const isHighlighter = (e: BoardElement): boolean => e.type === 'Pen' && e.strokeWidth >= HIGHLIGHTER_WIDTH

export const labelOf = (e: BoardElement): string => {
  if (e.type === 'Text' || e.type === 'Sticky' || e.type === 'Connector') return e.text.split('\n')[0]!.slice(0, 40)
  if (e.type === 'Image') return (e.alt ?? '').slice(0, 40)
  if (e.type === 'Table') return (e.cells?.find((c) => c.trim()) ?? '').slice(0, 40)
  return ''
}

/** Elements with a new value for the given ids (immutable). Text edits also refresh the direction. */
export function patch(elements: BoardElement[], id: string, changes: Partial<BoardElement>): BoardElement[] {
  return elements.map((e) => {
    if (e.id !== id) return e
    const next = { ...e, ...changes }
    if ('text' in changes && (e.type === 'Text' || e.type === 'Sticky')) next.rtl = isRtlText(next.text)
    return next
  })
}

export const remove = (elements: BoardElement[], id: string): BoardElement[] => elements.filter((e) => e.id !== id)

export function reorder(elements: BoardElement[], id: string, to: 'front' | 'back'): BoardElement[] {
  const el = elements.find((e) => e.id === id)
  if (!el) return elements
  const rest = elements.filter((e) => e.id !== id)
  return to === 'front' ? [...rest, el] : [el, ...rest]
}

/** One step in the stacking order (the list order is the order of drawing): "forward" = drawn later = on top of one more element. */
export function stepOrder(elements: BoardElement[], id: string, dir: 'forward' | 'backward'): BoardElement[] {
  const i = elements.findIndex((e) => e.id === id)
  const j = dir === 'forward' ? i + 1 : i - 1
  if (i < 0 || j < 0 || j >= elements.length) return elements
  const next = [...elements]
  ;[next[i], next[j]] = [next[j]!, next[i]!]
  return next
}

const cloneElement = (e: BoardElement, over: Partial<BoardElement> = {}): BoardElement => ({
  ...e,
  ...over,
  points: [...(over.points ?? e.points)],
  children: (over.children ?? e.children) ? [...(over.children ?? e.children)!] : undefined,
  cells: (over.cells ?? e.cells) ? [...(over.cells ?? e.cells)!] : undefined,
  colWidths: (over.colWidths ?? e.colWidths) ? [...(over.colWidths ?? e.colWidths)!] : undefined,
})

/** Copies of elements for pasting: new ids, shifted by (dx, dy). Arrows and strokes keep their relative points. */
export function pasteCopies(source: BoardElement[], dx: number, dy: number, room: number): BoardElement[] {
  return pasteElements(source.slice(0, Math.max(0, room)), { x: dx, y: dy })
}

/** Deep copy, keeping ids (the clipboard holds this until paste remaps them). */
export function copyElements(els: BoardElement[]): BoardElement[] {
  return els.map((e) => cloneElement(e))
}

/** Paste copies with fresh ids. Connectors, groups and parents are rewired when the other end was copied too. */
export function pasteElements(els: BoardElement[], offset: { x: number; y: number }): BoardElement[] {
  const map = new Map(els.map((e) => [e.id, newId()]))
  const re = (id: string | null | undefined) => (id && map.has(id) ? map.get(id)! : id)
  return els.map((e) => cloneElement(e, {
    id: map.get(e.id)!,
    x: round(e.x + offset.x),
    y: round(e.y + offset.y),
    parentId: re(e.parentId) ?? null,
    fromId: re(e.fromId) ?? null,
    toId: re(e.toId) ?? null,
    children: e.children?.map((id) => map.get(id) ?? id),
  }))
}

export function duplicate(elements: BoardElement[], id: string): { elements: BoardElement[]; id: string | null } {
  const el = elements.find((e) => e.id === id)
  if (!el || elements.length >= MAX_ELEMENTS) return { elements, id: null }
  const copy = cloneElement(el, { id: newId(), x: el.x + 24, y: el.y + 24 })
  return { elements: [...elements, copy], id: copy.id }
}

export function createImageElement(attachmentId: string, x: number, y: number, w: number, h: number, alt = ''): BoardElement {
  const el = base('Image', x, y)
  Object.assign(el, { w, h, attachmentId, alt })
  return el
}

/** `points` are absolute board coordinates. They are stored relative to the first point, like arrows. */
export function createConnector(fromId: string, toId: string, points: number[], extra: Partial<BoardElement> = {}): BoardElement {
  const ox = points[0] ?? 0
  const oy = points[1] ?? 0
  const el = base('Connector', ox, oy)
  Object.assign(el, {
    points: points.map((v, i) => round(v - (i % 2 === 0 ? ox : oy))),
    fromId,
    toId,
    fromPort: extra.fromPort ?? 'Center',
    toPort: extra.toPort ?? 'Center',
    route: extra.route ?? 'Straight',
    startArrow: extra.startArrow ?? false,
    endArrow: extra.endArrow ?? true,
    stroke: extra.stroke ?? PALETTE[0],
    strokeWidth: extra.strokeWidth ?? 2,
    text: extra.text ?? '',
  })
  return el
}

export function portPoint(e: BoardElement, port: BoardPort = 'Center'): { x: number; y: number } {
  const cx = e.x + e.w / 2
  const cy = e.y + e.h / 2
  switch (port) {
    case 'Top': return { x: cx, y: e.y }
    case 'Right': return { x: e.x + e.w, y: cy }
    case 'Bottom': return { x: cx, y: e.y + e.h }
    case 'Left': return { x: e.x, y: cy }
    default: return { x: cx, y: cy }
  }
}

export function nearestPort(e: BoardElement, x: number, y: number): BoardPort {
  const ports: BoardPort[] = ['Center', 'Top', 'Right', 'Bottom', 'Left']
  let best: BoardPort = 'Center'
  let bestD = Infinity
  for (const port of ports) {
    const p = portPoint(e, port)
    const d = Math.hypot(p.x - x, p.y - y)
    if (d < bestD) {
      bestD = d
      best = port
    }
  }
  return best
}

/** Absolute coordinates of a connector between two shapes. */
export function connectorPoints(from: BoardElement, to: BoardElement, fromPort: BoardPort, toPort: BoardPort, route: BoardRoute): number[] {
  const a = portPoint(from, fromPort)
  const b = portPoint(to, toPort)
  if (route === 'Orthogonal') {
    const midX = (a.x + b.x) / 2
    return [a.x, a.y, midX, a.y, midX, b.y, b.x, b.y]
  }
  if (route === 'Curve') {
    const dx = b.x - a.x
    const dy = b.y - a.y
    const len = Math.hypot(dx, dy) || 1
    const bend = Math.min(40, len / 4)
    return [a.x, a.y, (a.x + b.x) / 2 + (-dy / len) * bend, (a.y + b.y) / 2 + (dx / len) * bend, b.x, b.y]
  }
  return [a.x, a.y, b.x, b.y]
}

/** Rewrites connectors whose ends moved so the line stays attached. Returns the same array when nothing changed. */
export function recomputeConnectors(elements: BoardElement[], movedIds?: ReadonlySet<string>): BoardElement[] {
  let changed = false
  const next = elements.map((e) => {
    if (e.type !== 'Connector' || !e.fromId || !e.toId) return e
    if (movedIds && !movedIds.has(e.fromId) && !movedIds.has(e.toId)) return e
    const from = elements.find((x) => x.id === e.fromId)
    const to = elements.find((x) => x.id === e.toId)
    if (!from || !to) return e
    const abs = connectorPoints(from, to, e.fromPort ?? 'Center', e.toPort ?? 'Center', e.route ?? 'Straight')
    const ox = abs[0] ?? 0
    const oy = abs[1] ?? 0
    const points = abs.map((v, i) => round(v - (i % 2 === 0 ? ox : oy)))
    if (e.x === round(ox) && e.y === round(oy) && JSON.stringify(e.points) === JSON.stringify(points)) return e
    changed = true
    return { ...e, x: round(ox), y: round(oy), points }
  })
  return changed ? next : elements
}

/** Top-most shape whose box contains the point (lines, pens and connectors are skipped). */
export function shapeAt(elements: BoardElement[], x: number, y: number): BoardElement | null {
  for (let i = elements.length - 1; i >= 0; i--) {
    const e = elements[i]!
    if (e.hidden || e.type === 'Group' || e.type === 'Arrow' || e.type === 'Pen' || e.type === 'Line' || e.type === 'Connector') continue
    const b = bounds([e])
    if (b && x >= b.x && x <= b.x + b.w && y >= b.y && y <= b.y + b.h) return e
  }
  return null
}

/** Ids whose bounds meet the marquee (either corner order). */
export function hitsMarquee(elements: BoardElement[], box: { x: number; y: number; w: number; h: number }): string[] {
  const left = Math.min(box.x, box.x + box.w)
  const right = Math.max(box.x, box.x + box.w)
  const top = Math.min(box.y, box.y + box.h)
  const bottom = Math.max(box.y, box.y + box.h)
  const ids: string[] = []
  for (const e of elements) {
    if (e.hidden) continue
    const b = bounds([e])
    if (b && b.x < right && b.x + b.w > left && b.y < bottom && b.y + b.h > top) ids.push(e.id)
  }
  return ids
}

type Side = 'left' | 'right' | 'top' | 'bottom' | 'centerX' | 'centerY'

function alignTo(elements: BoardElement[], ids: string[], side: Side): BoardElement[] {
  const selected = elements.filter((e) => ids.includes(e.id) && !e.hidden)
  if (selected.length < 2) return elements
  const all = bounds(selected)
  if (!all) return elements
  let changed = false
  const next = elements.map((e) => {
    if (!ids.includes(e.id) || e.locked) return e
    const b = bounds([e])
    if (!b) return e
    let dx = 0
    let dy = 0
    if (side === 'left') dx = all.x - b.x
    else if (side === 'right') dx = all.x + all.w - (b.x + b.w)
    else if (side === 'top') dy = all.y - b.y
    else if (side === 'bottom') dy = all.y + all.h - (b.y + b.h)
    else if (side === 'centerX') dx = all.x + all.w / 2 - (b.x + b.w / 2)
    else dy = all.y + all.h / 2 - (b.y + b.h / 2)
    if (dx === 0 && dy === 0) return e
    changed = true
    return { ...e, x: round(e.x + dx), y: round(e.y + dy) }
  })
  return changed ? next : elements
}

export const alignLeft = (elements: BoardElement[], ids: string[]): BoardElement[] => alignTo(elements, ids, 'left')
export const alignRight = (elements: BoardElement[], ids: string[]): BoardElement[] => alignTo(elements, ids, 'right')
export const alignTop = (elements: BoardElement[], ids: string[]): BoardElement[] => alignTo(elements, ids, 'top')
export const alignBottom = (elements: BoardElement[], ids: string[]): BoardElement[] => alignTo(elements, ids, 'bottom')
export const alignCenterX = (elements: BoardElement[], ids: string[]): BoardElement[] => alignTo(elements, ids, 'centerX')
export const alignCenterY = (elements: BoardElement[], ids: string[]): BoardElement[] => alignTo(elements, ids, 'centerY')

function distribute(elements: BoardElement[], ids: string[], axis: 'h' | 'v'): BoardElement[] {
  const selected = elements.filter((e) => ids.includes(e.id) && !e.hidden && !e.locked)
  if (selected.length < 3) return elements
  const boxes = selected.map((e) => ({ e, b: bounds([e]) })).filter((x): x is { e: BoardElement; b: { x: number; y: number; w: number; h: number } } => !!x.b)
  boxes.sort((a, c) => (axis === 'h' ? a.b.x - c.b.x : a.b.y - c.b.y))
  const first = boxes[0]!
  const last = boxes[boxes.length - 1]!
  const span = axis === 'h' ? last.b.x + last.b.w - first.b.x : last.b.y + last.b.h - first.b.y
  const total = boxes.reduce((s, x) => s + (axis === 'h' ? x.b.w : x.b.h), 0)
  const gap = (span - total) / (boxes.length - 1)
  let cursor = axis === 'h' ? first.b.x : first.b.y
  const shift = new Map<string, number>()
  for (const item of boxes) {
    const start = axis === 'h' ? item.b.x : item.b.y
    shift.set(item.e.id, cursor - start)
    cursor += (axis === 'h' ? item.b.w : item.b.h) + gap
  }
  let changed = false
  const next = elements.map((e) => {
    const d = shift.get(e.id)
    if (d === undefined || d === 0) return e
    changed = true
    return axis === 'h' ? { ...e, x: round(e.x + d) } : { ...e, y: round(e.y + d) }
  })
  return changed ? next : elements
}

export const distributeH = (elements: BoardElement[], ids: string[]): BoardElement[] => distribute(elements, ids, 'h')
export const distributeV = (elements: BoardElement[], ids: string[]): BoardElement[] => distribute(elements, ids, 'v')

/** Groups the given elements under a new group frame. Connectors and existing groups are not children. */
export function groupElements(elements: BoardElement[], ids: string[]): { elements: BoardElement[]; id: string | null } {
  const wanted = new Set(ids)
  const kids = elements.filter((e) => wanted.has(e.id) && e.type !== 'Group' && e.type !== 'Connector')
  if (kids.length < 2 || elements.length >= MAX_ELEMENTS) return { elements, id: null }
  const b = bounds(kids)
  if (!b) return { elements, id: null }
  const group = base('Group', b.x, b.y)
  Object.assign(group, { w: round(Math.max(b.w, 10)), h: round(Math.max(b.h, 10)), children: kids.map((e) => e.id) })
  const kidIds = new Set(group.children)
  const next = elements.map((e) => (kidIds.has(e.id) ? { ...e, parentId: group.id } : e))
  return { elements: [...next, group], id: group.id }
}

export function ungroupElements(elements: BoardElement[], groupId: string): BoardElement[] {
  const g = elements.find((e) => e.id === groupId && e.type === 'Group')
  if (!g) return elements
  const kids = new Set(g.children ?? [])
  return elements.filter((e) => e.id !== groupId).map((e) => (kids.has(e.id) && e.parentId === groupId ? { ...e, parentId: null } : e))
}

/** Column widths scaled so they add up to the table width. */
export function columnWidths(e: BoardElement): number[] {
  const cols = Math.max(1, e.cols || 1)
  const given = e.colWidths ?? []
  const vals = Array.from({ length: cols }, (_, i) => {
    const n = given[i]
    return typeof n === 'number' && n > 0 ? n : (e.w || cols) / cols
  })
  const sum = vals.reduce((s, n) => s + n, 0) || 1
  return vals.map((n) => (n / sum) * (e.w || 1))
}

/**
 * Splits a pen stroke where the cursor covers it. The original element is returned unchanged when the cursor misses;
 * an empty list means the whole stroke is gone.
 */
export function erasePen(el: BoardElement, x: number, y: number, radius = 14): BoardElement[] {
  if (el.type !== 'Pen' || el.locked || el.points.length < 4) return [el]
  const count = Math.floor(el.points.length / 2)
  const drop = Array.from({ length: count }, () => false)
  const near = (px: number, py: number) => Math.hypot(el.x + px - x, el.y + py - y) <= radius
  for (let i = 0; i < count; i++) if (near(el.points[2 * i]!, el.points[2 * i + 1]!)) drop[i] = true
  for (let i = 0; i < count - 1; i++) {
    if (drop[i] && drop[i + 1]) continue
    const ax = el.x + el.points[2 * i]!
    const ay = el.y + el.points[2 * i + 1]!
    const bx = el.x + el.points[2 * i + 2]!
    const by = el.y + el.points[2 * i + 3]!
    if (distanceToSegment(x, y, ax, ay, bx, by) <= radius) {
      drop[i] = true
      drop[i + 1] = true
    }
  }
  if (drop.every((d) => !d)) return [el]
  if (drop.every(Boolean)) return []
  const runs: number[][] = []
  let cur: number[] = []
  for (let i = 0; i < count; i++) {
    if (drop[i]) {
      if (cur.length >= 4) runs.push(cur)
      cur = []
    } else cur.push(el.points[2 * i]!, el.points[2 * i + 1]!)
  }
  if (cur.length >= 4) runs.push(cur)
  return runs.map((rel, index) => {
    const ox = rel[0]!
    const oy = rel[1]!
    return cloneElement(el, {
      id: index === 0 ? el.id : newId(),
      x: round(el.x + ox),
      y: round(el.y + oy),
      points: rel.map((v, i) => round(v - (i % 2 === 0 ? ox : oy))),
    })
  })
}

function distanceToSegment(px: number, py: number, ax: number, ay: number, bx: number, by: number): number {
  const dx = bx - ax
  const dy = by - ay
  const len2 = dx * dx + dy * dy
  const t = len2 === 0 ? 0 : Math.max(0, Math.min(1, ((px - ax) * dx + (py - ay) * dy) / len2))
  return Math.hypot(px - (ax + t * dx), py - (ay + t * dy))
}

/** Axis-aligned bounds of all elements in board coordinates (rotation included); null for an empty board. */
export function bounds(elements: BoardElement[]): { x: number; y: number; w: number; h: number } | null {
  if (elements.length === 0) return null
  let minX = Infinity
  let minY = Infinity
  let maxX = -Infinity
  let maxY = -Infinity
  for (const e of elements) {
    const corners: [number, number][] =
      e.type === 'Arrow' || e.type === 'Pen' || e.type === 'Line' || e.type === 'Connector'
        ? Array.from({ length: Math.floor(e.points.length / 2) }, (_, i) => [e.points[2 * i]!, e.points[2 * i + 1]!] as [number, number])
        : [[0, 0], [e.w, 0], [e.w, e.h], [0, e.h]]
    const rad = (e.rotation * Math.PI) / 180
    const [sin, cos] = [Math.sin(rad), Math.cos(rad)]
    for (const [cx, cy] of corners) {
      const px = e.x + cx * cos - cy * sin
      const py = e.y + cx * sin + cy * cos
      minX = Math.min(minX, px)
      minY = Math.min(minY, py)
      maxX = Math.max(maxX, px)
      maxY = Math.max(maxY, py)
    }
  }
  if (!Number.isFinite(minX)) return null
  return { x: minX, y: minY, w: maxX - minX, h: maxY - minY }
}

/** Calls `fn` once after `ms` quiet milliseconds. `flush` runs a pending call now; `cancel` drops it. */
export function debounce(fn: () => void, ms: number): { (): void; cancel(): void; flush(): void } {
  let timer: ReturnType<typeof setTimeout> | undefined
  let pending = false
  const run = () => {
    pending = false
    timer = undefined
    fn()
  }
  const wrapped = () => {
    pending = true
    if (timer) clearTimeout(timer)
    timer = setTimeout(run, ms)
  }
  wrapped.cancel = () => {
    if (timer) clearTimeout(timer)
    timer = undefined
    pending = false
  }
  wrapped.flush = () => {
    if (!pending) return
    if (timer) clearTimeout(timer)
    run()
  }
  return wrapped
}

/** Undo/redo over whole-board snapshots (the board is small and immutable updates make snapshots cheap). */
export class History {
  private past: BoardElement[][] = []
  private future: BoardElement[][] = []

  constructor(private readonly limit = 100) {}

  get canUndo(): boolean { return this.past.length > 0 }
  get canRedo(): boolean { return this.future.length > 0 }

  /** Call with the state *before* a change. */
  record(before: BoardElement[]): void {
    this.past.push(before)
    if (this.past.length > this.limit) this.past.shift()
    this.future = []
  }

  undo(current: BoardElement[]): BoardElement[] | null {
    const prev = this.past.pop()
    if (!prev) return null
    this.future.push(current)
    return prev
  }

  redo(current: BoardElement[]): BoardElement[] | null {
    const next = this.future.pop()
    if (!next) return null
    this.past.push(current)
    return next
  }

  clear(): void {
    this.past = []
    this.future = []
  }
}
