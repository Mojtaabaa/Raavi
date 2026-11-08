import * as Y from 'yjs'

/**
 * Whiteboard documents (Phase 11c, ADR-035). The Yjs structure of a board is
 *   elements: Y.Map<id, Y.Map<field, value>>   one inner map per element, so two people changing different fields of the same element
 *                                               (one moves it, the other recolours it) both keep their change
 *   order:    Y.Array<id>                       stacking order, last = on top
 * Values are plain JSON (numbers, strings, booleans, number arrays). The API validates the converted list exactly like a REST save; this
 * module only converts. The frontend (`liveBoard.ts`) implements the same structure — keep both in step.
 */
export const ELEMENTS = 'elements'
export const ORDER = 'order'

export type BoardElementJson = { id: string } & Record<string, unknown>

/** Fills an empty document from the stored element list (first live session since the last REST save). */
export function seedBoard(doc: Y.Doc, elements: readonly BoardElementJson[]): void {
  doc.transact(() => {
    const map = doc.getMap<Y.Map<unknown>>(ELEMENTS)
    const order = doc.getArray<string>(ORDER)
    for (const e of elements) {
      if (typeof e?.id !== 'string' || map.has(e.id)) continue
      const inner = new Y.Map<unknown>()
      for (const [k, v] of Object.entries(e)) if (k !== 'id') inner.set(k, v)
      map.set(e.id, inner)
      order.push([e.id])
    }
  }, 'seed')
}

/**
 * The element list in stacking order. Tolerates what concurrent edits can leave behind: ids twice in `order` (first wins), ids in `order`
 * without an element (deleted concurrently — dropped) and elements missing from `order` (appended, by id, so they are never lost).
 */
export function boardToJson(doc: Y.Doc): BoardElementJson[] {
  const map = doc.getMap<Y.Map<unknown>>(ELEMENTS)
  const seen = new Set<string>()
  const out: BoardElementJson[] = []
  const push = (id: string) => {
    if (seen.has(id)) return
    const inner = map.get(id)
    if (!(inner instanceof Y.Map)) return
    seen.add(id)
    out.push({ id, ...inner.toJSON() })
  }
  for (const id of doc.getArray<string>(ORDER).toArray()) push(id)
  for (const id of [...map.keys()].sort()) push(id)
  return out
}
