import * as Y from 'yjs'
import type { BoardElement, BoardShape } from '@/core/api/whiteboards'

/**
 * Yjs binding of a whiteboard (Phase 11c, ADR-035). Structure — identical to `collaboration/src/board.ts`, keep both in step:
 *   elements: Y.Map<id, Y.Map<field, value>>   one inner map per element → changes to different fields of one element both survive
 *   order:    Y.Array<id>                       stacking order, last = on top
 *
 * The editor keeps its plain `BoardElement[]` model; this class turns every local edit into the smallest set of Yjs changes (`apply`), reports
 * changes from other people as a fresh element list (`onRemote`), and provides undo/redo of the user's *own* changes only — never someone else's.
 */
const LOCAL = Symbol('local-edit')
const TYPES: readonly BoardShape[] = ['Rect', 'Ellipse', 'Text', 'Sticky', 'Arrow', 'Pen', 'Diamond', 'Triangle', 'Line']

const DEFAULTS = { x: 0, y: 0, w: 0, h: 0, rotation: 0, stroke: '#1f2937', fill: null, strokeWidth: 2, text: '', fontSize: 20, rtl: false } as const

/** One stored element as a complete `BoardElement`; null when it cannot be drawn (no valid type), so a damaged entry never breaks the canvas. */
export function readElement(id: string, inner: Y.Map<unknown>): BoardElement | null {
  const raw = inner.toJSON() as Record<string, unknown>
  if (!TYPES.includes(raw.type as BoardShape)) return null
  const num = (k: keyof typeof DEFAULTS) => (typeof raw[k] === 'number' && Number.isFinite(raw[k] as number) ? (raw[k] as number) : (DEFAULTS[k] as number))
  return {
    id,
    type: raw.type as BoardShape,
    x: num('x'),
    y: num('y'),
    w: num('w'),
    h: num('h'),
    rotation: num('rotation'),
    stroke: typeof raw.stroke === 'string' ? raw.stroke : DEFAULTS.stroke,
    fill: typeof raw.fill === 'string' ? raw.fill : null,
    strokeWidth: num('strokeWidth'),
    text: typeof raw.text === 'string' ? raw.text : '',
    fontSize: num('fontSize'),
    rtl: raw.rtl === true,
    points: Array.isArray(raw.points) ? (raw.points as unknown[]).filter((p): p is number => typeof p === 'number') : [],
  }
}

const FIELDS: (keyof BoardElement)[] = ['type', 'x', 'y', 'w', 'h', 'rotation', 'stroke', 'fill', 'strokeWidth', 'text', 'fontSize', 'rtl', 'points']
const same = (a: unknown, b: unknown) => (Array.isArray(a) || Array.isArray(b) ? JSON.stringify(a) === JSON.stringify(b) : a === b)

export class LiveBoard {
  private readonly map: Y.Map<Y.Map<unknown>>
  private readonly order: Y.Array<string>
  private readonly undoManager: Y.UndoManager
  private remoteCallbacks: ((elements: BoardElement[]) => void)[] = []
  private historyCallbacks: (() => void)[] = []

  constructor(readonly doc: Y.Doc) {
    this.map = doc.getMap<Y.Map<unknown>>('elements')
    this.order = doc.getArray<string>('order')
    this.undoManager = new Y.UndoManager([this.map, this.order], { trackedOrigins: new Set([LOCAL]), captureTimeout: 400 })
    const notifyHistory = () => this.historyCallbacks.forEach((cb) => cb())
    this.undoManager.on('stack-item-added', notifyHistory)
    this.undoManager.on('stack-item-popped', notifyHistory)
    this.undoManager.on('stack-cleared', notifyHistory)
    this.map.observeDeep((_events, tx) => this.changed(tx))
    this.order.observe((_event, tx) => this.changed(tx))
  }

  private changed(tx: Y.Transaction) {
    if (tx.origin === LOCAL) return // our own edit: the editor already holds this state
    const elements = this.toElements()
    this.remoteCallbacks.forEach((cb) => cb(elements))
  }

  /** Current elements in stacking order (duplicates in the order are ignored; elements missing from it are appended by id, never lost). */
  toElements(): BoardElement[] {
    const seen = new Set<string>()
    const out: BoardElement[] = []
    const push = (id: string) => {
      if (seen.has(id)) return
      const inner = this.map.get(id)
      if (!(inner instanceof Y.Map)) return
      seen.add(id)
      const el = readElement(id, inner)
      if (el) out.push(el)
    }
    for (const id of this.order.toArray()) push(id)
    for (const id of [...this.map.keys()].sort()) push(id)
    return out
  }

  /** Writes the difference between what is stored now and `next` (the editor's new model) as one tracked transaction. */
  apply(next: readonly BoardElement[]): void {
    const current = new Map(this.toElements().map((e) => [e.id, e]))
    const nextIds = next.map((e) => e.id)
    const keptCurrent = this.toElements().map((e) => e.id).filter((id) => nextIds.includes(id))
    const keptNext = nextIds.filter((id) => current.has(id))
    const reordered = keptCurrent.length !== keptNext.length || keptCurrent.some((id, i) => id !== keptNext[i])
    this.doc.transact(() => {
      for (const id of current.keys()) if (!nextIds.includes(id)) this.map.delete(id)
      for (const e of next) {
        let inner = this.map.get(e.id)
        if (!(inner instanceof Y.Map)) {
          inner = new Y.Map<unknown>()
          this.map.set(e.id, inner)
        }
        const before = current.get(e.id)
        for (const f of FIELDS) {
          if (!before || !same(before[f], e[f])) inner.set(f, e[f])
        }
      }
      // Stacking order: appended elements go to the end, deleted ones leave; a real reorder rewrites the array.
      if (reordered) {
        this.order.delete(0, this.order.length)
        this.order.push(nextIds)
      } else {
        const present = new Set(this.order.toArray())
        for (let i = this.order.length - 1; i >= 0; i--) if (!nextIds.includes(this.order.get(i))) this.order.delete(i, 1)
        const fresh = nextIds.filter((id) => !present.has(id))
        if (fresh.length) this.order.push(fresh)
      }
    }, LOCAL)
  }

  /** Called with the full element list after a change made by someone else (or by this user's undo/redo). */
  onRemote(cb: (elements: BoardElement[]) => void): () => void {
    this.remoteCallbacks.push(cb)
    return () => (this.remoteCallbacks = this.remoteCallbacks.filter((c) => c !== cb))
  }

  onHistory(cb: () => void): () => void {
    this.historyCallbacks.push(cb)
    return () => (this.historyCallbacks = this.historyCallbacks.filter((c) => c !== cb))
  }

  get canUndo(): boolean {
    return this.undoManager.undoStack.length > 0
  }

  get canRedo(): boolean {
    return this.undoManager.redoStack.length > 0
  }

  undo(): void {
    this.undoManager.stopCapturing()
    this.undoManager.undo()
  }

  redo(): void {
    this.undoManager.stopCapturing()
    this.undoManager.redo()
  }

  /** Makes the next local edit a separate undo step (call between distinct user actions). */
  breakUndoStep(): void {
    this.undoManager.stopCapturing()
  }

  destroy(): void {
    this.undoManager.destroy()
    this.remoteCallbacks = []
    this.historyCallbacks = []
  }
}
