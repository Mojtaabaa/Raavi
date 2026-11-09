import type { BoardElement, BoardShape } from '@/core/api/whiteboards'

/** Pure whiteboard logic (no canvas): element factories, edits, history and geometry. The canvas component only draws and reports gestures. */

export type Tool = 'select' | 'hand' | 'rect' | 'ellipse' | 'diamond' | 'triangle' | 'sticky' | 'text' | 'arrow' | 'line' | 'pen'

/** Grid step in board units: the size of the grid dots, and the step positions snap to when "snap to grid" is on. */
export const GRID = 20

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
const CLICK_SIZE: Partial<Record<BoardShape, [number, number]>> = { Rect: [140, 90], Ellipse: [140, 90], Diamond: [140, 110], Triangle: [130, 110], Sticky: [170, 130] }

/** Builds the element for a gesture from (x0, y0) to (x1, y1); a tiny drag counts as a click. */
export function elementFromGesture(tool: Tool, x0: number, y0: number, x1: number, y1: number, pen: number[] = [], options: { stroke?: string; fill?: string | null } = {}): BoardElement | null {
  const dragged = Math.hypot(x1 - x0, y1 - y0) >= 6
  const stroke = options.stroke ?? PALETTE[0]
  switch (tool) {
    case 'rect':
    case 'ellipse':
    case 'diamond':
    case 'triangle':
    case 'sticky': {
      const type: BoardShape = tool === 'rect' ? 'Rect' : tool === 'ellipse' ? 'Ellipse' : tool === 'diamond' ? 'Diamond' : tool === 'triangle' ? 'Triangle' : 'Sticky'
      const el = base(type, Math.min(x0, x1), Math.min(y0, y1))
      const [w, h] = dragged ? [Math.abs(x1 - x0), Math.abs(y1 - y0)] : CLICK_SIZE[type]!
      Object.assign(el, { w: Math.max(w, 10), h: Math.max(h, 10), stroke, fill: options.fill ?? (type === 'Sticky' ? FILLS[0] : null) })
      if (type === 'Sticky') el.stroke = PALETTE[0]
      return el
    }
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
    case 'pen': {
      const points = simplify(pen)
      if (points.length < 4) return null
      const [ox, oy] = [points[0]!, points[1]!]
      const el = base('Pen', ox, oy)
      Object.assign(el, { points: points.map((v, i) => round(v - (i % 2 === 0 ? ox : oy))), stroke })
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

export const labelOf = (e: BoardElement): string => (e.type === 'Text' || e.type === 'Sticky' ? e.text.split('\n')[0]!.slice(0, 40) : '')

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

/** Copies of elements for pasting: new ids, shifted by (dx, dy). Arrows and strokes keep their relative points. */
export function pasteCopies(source: BoardElement[], dx: number, dy: number, room: number): BoardElement[] {
  return source.slice(0, Math.max(0, room)).map((e) => ({ ...e, id: newId(), x: round(e.x + dx), y: round(e.y + dy), points: [...e.points] }))
}

export function duplicate(elements: BoardElement[], id: string): { elements: BoardElement[]; id: string | null } {
  const el = elements.find((e) => e.id === id)
  if (!el || elements.length >= MAX_ELEMENTS) return { elements, id: null }
  const copy: BoardElement = { ...el, id: newId(), x: el.x + 24, y: el.y + 24, points: [...el.points] }
  return { elements: [...elements, copy], id: copy.id }
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
      e.type === 'Arrow' || e.type === 'Pen' || e.type === 'Line'
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
  return { x: minX, y: minY, w: maxX - minX, h: maxY - minY }
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
