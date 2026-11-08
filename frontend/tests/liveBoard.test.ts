import fs from 'node:fs'
import path from 'node:path'
import { describe, expect, it } from 'vitest'
import * as Y from 'yjs'
import type { BoardElement } from '@/core/api/whiteboards'
import { LiveBoard, readElement } from '@/modules/whiteboards/liveBoard'

const el = (id: string, over: Partial<BoardElement> = {}): BoardElement => ({
  id, type: 'Rect', x: 0, y: 0, w: 100, h: 50, rotation: 0, stroke: '#1f2937', fill: null, strokeWidth: 2, text: '', fontSize: 20, rtl: false, points: [], ...over,
})

/** Two peers connected through an in-memory "server": every update of one is applied to the other (both ways, like the provider does). */
function pair() {
  const docs = [new Y.Doc(), new Y.Doc()] as const
  let online = true
  const queue: [Y.Doc, Uint8Array][] = []
  docs.forEach((d, i) => d.on('update', (u: Uint8Array, origin: unknown) => {
    if (origin === 'remote') return
    if (online) Y.applyUpdate(docs[1 - i]!, u, 'remote')
    else queue.push([docs[1 - i]!, u])
  }))
  return {
    a: new LiveBoard(docs[0]),
    b: new LiveBoard(docs[1]),
    docs,
    offline: () => (online = false),
    online: () => {
      online = true
      queue.splice(0).forEach(([d, u]) => Y.applyUpdate(d, u, 'remote'))
    },
  }
}

describe('live board binding', () => {
  it('turns a local edit into the smallest set of Yjs changes and does not echo it back', () => {
    const doc = new Y.Doc()
    const live = new LiveBoard(doc)
    const remote: BoardElement[][] = []
    live.onRemote((e) => remote.push(e))

    live.apply([el('r1'), el('t1', { type: 'Text', text: 'hi' })])
    expect(live.toElements().map((e) => e.id)).toEqual(['r1', 't1'])
    expect(remote).toHaveLength(0) // own edits are not reported as remote

    const changed: string[] = []
    ;(doc.getMap('elements').get('r1') as Y.Map<unknown>).observe((ev) => ev.keysChanged.forEach((k) => changed.push(k)))
    live.apply([el('r1', { x: 40 }), el('t1', { type: 'Text', text: 'hi' })])
    expect(changed).toEqual(['x']) // moving writes one field, not the element

    live.apply([el('r1', { x: 40 })]) // delete t1
    expect(live.toElements().map((e) => e.id)).toEqual(['r1'])
    expect(doc.getMap('elements').has('t1')).toBe(false)
    expect(doc.getArray('order').toArray()).toEqual(['r1'])
  })

  it('two people changing different fields of the same element keep both changes; adds and deletes converge', () => {
    const p = pair()
    const { a, b } = p
    a.apply([el('r1'), el('r2')])
    expect(b.toElements().map((e) => e.id)).toEqual(['r1', 'r2'])

    // Truly concurrent: both are offline while they edit.
    p.offline()
    a.apply(a.toElements().map((e) => (e.id === 'r1' ? { ...e, x: 300 } : e)))
    b.apply([...b.toElements().map((e) => (e.id === 'r1' ? { ...e, stroke: '#ef4444' } : e)), el('b1', { type: 'Sticky', text: 'mine' })])
    p.online()
    const result = a.toElements()
    expect(result).toEqual(b.toElements())
    expect(result.find((e) => e.id === 'r1')).toMatchObject({ x: 300, stroke: '#ef4444' })
    expect(result.map((e) => e.id)).toEqual(['r1', 'r2', 'b1'])

    p.offline()
    a.apply(a.toElements().filter((e) => e.id !== 'r2')) // a deletes r2 while b recolours it
    b.apply(b.toElements().map((e) => (e.id === 'r2' ? { ...e, stroke: '#10b981' } : e)))
    p.online()
    expect(a.toElements().map((e) => e.id)).toEqual(b.toElements().map((e) => e.id))
    expect(a.toElements().map((e) => e.id)).toEqual(['r1', 'b1'])
  })

  it('reports remote changes with the full list, and a reorder is a real reorder', () => {
    const { a, b } = pair()
    a.apply([el('r1'), el('r2'), el('r3')])
    const seen: string[][] = []
    b.onRemote((els) => seen.push(els.map((e) => e.id)))

    a.apply([el('r3'), el('r1'), el('r2')]) // r3 to the back
    expect(b.toElements().map((e) => e.id)).toEqual(['r3', 'r1', 'r2'])
    expect(seen.at(-1)).toEqual(['r3', 'r1', 'r2'])
  })

  it('undo and redo only touch the user’s own changes, never a collaborator’s', () => {
    const { a, b } = pair()
    a.apply([el('r1')])
    a.breakUndoStep()
    b.apply([el('r1'), el('b1')]) // b adds an element
    a.breakUndoStep()
    a.apply([el('r1', { x: 99 }), el('b1')]) // a moves r1
    expect(a.canUndo).toBe(true)

    a.undo()
    expect(a.toElements().find((e) => e.id === 'r1')!.x).toBe(0) // a's move is undone …
    expect(a.toElements().map((e) => e.id)).toEqual(['r1', 'b1']) // … b's element is untouched
    expect(b.toElements()).toEqual(a.toElements())

    a.redo()
    expect(b.toElements().find((e) => e.id === 'r1')!.x).toBe(99)

    // b has nothing of its own left to undo once it undid its single step; it never reverts a's work.
    b.undo()
    expect(b.toElements().map((e) => e.id)).toEqual(['r1'])
    expect(b.toElements()[0]!.x).toBe(99)
    expect(b.canUndo).toBe(false)
  })

  it('reports undo and redo to the editor so its model follows', () => {
    const doc = new Y.Doc()
    const live = new LiveBoard(doc)
    const models: number[] = []
    live.onRemote((els) => models.push(els.length))
    let history = 0
    live.onHistory(() => history++)
    live.apply([el('r1')])
    live.breakUndoStep()
    live.apply([el('r1'), el('r2')])
    live.undo()
    expect(models.at(-1)).toBe(1)
    live.redo()
    expect(models.at(-1)).toBe(2)
    expect(history).toBeGreaterThan(0)
  })

  it('edits made while offline merge when the connection returns', () => {
    const p = pair()
    p.a.apply([el('r1')])
    p.offline()
    p.a.apply([el('r1', { x: 5 })])
    p.b.apply([el('r1'), el('b1')])
    expect(p.a.toElements().map((e) => e.id)).toEqual(['r1'])
    p.online()
    expect(p.a.toElements()).toEqual(p.b.toElements())
    expect(p.a.toElements().map((e) => e.id)).toEqual(['r1', 'b1'])
    expect(p.a.toElements()[0]!.x).toBe(5)
  })

  it('reads damaged or partial entries safely: defaults for missing fields, nothing without a valid type, duplicates and orphans handled', () => {
    const doc = new Y.Doc()
    const live = new LiveBoard(doc)
    const map = doc.getMap<Y.Map<unknown>>('elements')
    const mk = (fields: Record<string, unknown>) => {
      const m = new Y.Map<unknown>()
      for (const [k, v] of Object.entries(fields)) m.set(k, v)
      return m
    }
    doc.transact(() => {
      map.set('a', mk({ type: 'Ellipse', w: 'wide', points: [1, 'x', 2] })) // wrong value types
      map.set('bad', mk({ type: 'Hexagon' })) // unknown type
      map.set('z', mk({ type: 'Rect', w: 10, h: 10 })) // not in the order
      doc.getArray<string>('order').push(['a', 'a', 'ghost', 'bad'])
    })
    const els = live.toElements()
    expect(els.map((e) => e.id)).toEqual(['a', 'z'])
    expect(els[0]).toMatchObject({ w: 0, stroke: '#1f2937', fill: null, points: [1, 2], rtl: false })
    expect(readElement('x', mk({}))).toBeNull()
  })
})

describe('contract with the collaboration server', () => {
  // The server converts the Yjs structure with its own code (collaboration/src/board.ts). This fixture is produced by the frontend binding and
  // checked by both sides, so the two implementations cannot drift apart unnoticed. Regenerate with UPDATE_FIXTURES=1 npm test.
  const file = path.resolve(process.cwd(), '../schema/board/live-board.fixture.json')

  it('produces the fixture the server tests decode', () => {
    const doc = new Y.Doc()
    doc.clientID = 4242
    const live = new LiveBoard(doc)
    const sticky = el('s1', { type: 'Sticky', x: 10, y: 20, w: 170, h: 130, fill: '#fde68a', text: 'سلام\nline 2', rtl: true })
    const arrow = el('a1', { type: 'Arrow', x: 5, y: 6, w: 0, h: 0, stroke: '#ef4444', strokeWidth: 3, points: [0, 0, 40, 10] })
    live.apply([el('r1', { fill: '#bfdbfe' }), sticky, arrow])
    live.apply([el('r1', { fill: '#bfdbfe', x: 99 }), arrow, sticky]) // moved r1, sent sticky to the front
    const expected = live.toElements()
    const generated = { note: 'Generated by frontend/tests/liveBoard.test.ts; verified by collaboration/tests/unit.test.ts', update: Buffer.from(Y.encodeStateAsUpdate(doc)).toString('base64'), elements: expected }
    if (process.env.UPDATE_FIXTURES) fs.writeFileSync(file, JSON.stringify(generated, null, 2) + '\n')
    const stored = JSON.parse(fs.readFileSync(file, 'utf8'))
    expect(stored.elements).toEqual(JSON.parse(JSON.stringify(expected)))
    expect(stored.update).toBe(generated.update)
    expect(expected.map((e) => e.id)).toEqual(['r1', 'a1', 's1'])
  })
})
