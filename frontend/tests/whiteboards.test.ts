import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createMemoryHistory } from 'vue-router'
import App from '@/app/App.vue'
import { configureHttp } from '@/core/api/http'
import { createRaaviI18n } from '@/core/i18n'
import { createRaaviRouter, resetRouterBootstrap } from '@/router'
import type { BoardElement } from '@/core/api/whiteboards'
import { alignBottom, alignCenterX, alignLeft, alignRight, alignTop, bounds, copyElements, createConnector, createImageElement, debounce, distributeH, duplicate, elementFromGesture, groupElements, History, isRtlText, pasteElements, patch, remove, reorder, simplify, ungroupElements } from '@/modules/whiteboards/boardModel'
import { fakeFetch, json } from './support/fakeApi'

// Konva needs a real canvas, which jsdom does not have: the view is tested with a stand-in that reports the same gestures.
vi.mock('@/modules/whiteboards/BoardCanvas.vue', async () => {
  const { defineComponent, h } = await import('vue')
  return {
    default: defineComponent({
      props: ['elements', 'tool', 'selectedId', 'readOnly', 'stroke', 'fill', 'label'],
      emits: ['select', 'create', 'change'],
      setup(props, { emit, expose }) {
        expose({ zoomBy: () => undefined, resetView: () => undefined, toPng: () => (props.elements.length ? 'data:image/png;base64,AAAA' : null) })
        const el = { id: 'new1', type: 'Sticky', x: 5, y: 6, w: 170, h: 130, rotation: 0, stroke: '#1f2937', fill: '#fde68a', strokeWidth: 2, text: '', fontSize: 20, rtl: false, points: [] }
        return () =>
          h('div', { 'data-testid': 'board-canvas', 'data-tool': props.tool, 'data-count': props.elements.length }, [
            h('button', { 'data-testid': 'stub-create', onClick: () => emit('create', el) }),
            h('button', { 'data-testid': 'stub-move', onClick: () => emit('change', 'r1', { x: 99, y: 98 }) }),
          ])
      },
    }),
  }
})

const el = (over: Partial<BoardElement> & Pick<BoardElement, 'id' | 'type'>): BoardElement => ({ x: 0, y: 0, w: 0, h: 0, rotation: 0, stroke: '#1f2937', fill: null, strokeWidth: 2, text: '', fontSize: 20, rtl: false, points: [], ...over })

describe('board model', () => {
  it('detects text direction from the first strong character', () => {
    expect(isRtlText('سلام world')).toBe(true)
    expect(isRtlText('hello سلام')).toBe(false)
    expect(isRtlText('123 …')).toBe(false)
    expect(isRtlText('  12 مرحبا')).toBe(true)
  })

  it('turns gestures into elements: click sizes, drag sizes, arrows need a drag, pen strokes are simplified and made relative', () => {
    const click = elementFromGesture('rect', 100, 100, 101, 101)!
    expect([click.type, click.w, click.h, click.x, click.y]).toEqual(['Rect', 140, 90, 100, 100])
    const drag = elementFromGesture('ellipse', 200, 150, 100, 50)!
    expect([drag.x, drag.y, drag.w, drag.h]).toEqual([100, 50, 100, 100]) // dragged up-left: normalised
    const sticky = elementFromGesture('sticky', 0, 0, 0, 0)!
    expect([sticky.fill, sticky.stroke]).toEqual(['#fde68a', '#1f2937'])
    expect(elementFromGesture('arrow', 5, 5, 6, 6)).toBeNull()
    const arrow = elementFromGesture('arrow', 10, 10, 60, 30, [], { stroke: '#ef4444' })!
    expect([arrow.points, arrow.stroke]).toEqual([[0, 0, 50, 20], '#ef4444'])
    expect(elementFromGesture('text', 7, 8, 7, 8)!.type).toBe('Text')
    expect(elementFromGesture('select', 0, 0, 10, 10)).toBeNull()

    const pen = elementFromGesture('pen', 0, 0, 0, 0, [10, 10, 10.5, 10.5, 20, 10, 30, 20])!
    expect(pen.points).toEqual([0, 0, 10, 0, 20, 10]) // the near-duplicate point is dropped; coordinates are relative to the first point
    expect([pen.x, pen.y]).toEqual([10, 10])
    expect(elementFromGesture('pen', 0, 0, 0, 0, [1, 1])).toBeNull()
    expect(simplify(Array.from({ length: 20000 }, (_, i) => i * 3)).length).toBeLessThanOrEqual(4000)
  })

  it('edits immutably: patch refreshes direction, reorder, remove, duplicate, bounds with rotation', () => {
    const a = el({ id: 'a', type: 'Text', text: 'hi' })
    const b = el({ id: 'b', type: 'Rect', x: 10, y: 10, w: 20, h: 10 })
    const list = [a, b]
    const edited = patch(list, 'a', { text: 'سلام' })
    expect(edited[0]!.rtl).toBe(true)
    expect(list[0]!.rtl).toBe(false) // original untouched
    expect(patch(list, 'b', { x: 1 })[1]!.rtl).toBe(false)
    expect(reorder(list, 'a', 'front').map((e) => e.id)).toEqual(['b', 'a'])
    expect(reorder(list, 'b', 'back').map((e) => e.id)).toEqual(['b', 'a'])
    expect(remove(list, 'a').map((e) => e.id)).toEqual(['b'])
    const dup = duplicate(list, 'b')
    expect(dup.elements).toHaveLength(3)
    expect(dup.elements[2]).toMatchObject({ type: 'Rect', x: 34, y: 34 })
    expect(dup.elements[2]!.id).not.toBe('b')
    expect(duplicate(list, 'zzz').id).toBeNull()

    expect(bounds([])).toBeNull()
    expect(bounds([b])).toEqual({ x: 10, y: 10, w: 20, h: 10 })
    const turned = bounds([el({ id: 'r', type: 'Rect', w: 20, h: 10, rotation: 90 })])!
    expect([Math.round(turned.x), Math.round(turned.y), Math.round(turned.w), Math.round(turned.h)]).toEqual([-10, 0, 10, 20])
    const stroke = bounds([el({ id: 's', type: 'Pen', x: 5, y: 5, points: [0, 0, 10, 4] })])!
    expect([stroke.x, stroke.y, stroke.w, stroke.h]).toEqual([5, 5, 10, 4])
  })

  it('undoes and redoes whole-board snapshots, drops redo after a new change and caps its size', () => {
    const h = new History(3)
    const s0: BoardElement[] = []
    const s1 = [el({ id: 'a', type: 'Rect', w: 1, h: 1 })]
    const s2 = [...s1, el({ id: 'b', type: 'Rect', w: 1, h: 1 })]
    h.record(s0)
    h.record(s1)
    expect([h.canUndo, h.canRedo]).toEqual([true, false])
    expect(h.undo(s2)).toBe(s1)
    expect(h.undo(s1)).toBe(s0)
    expect(h.undo(s0)).toBeNull()
    expect(h.redo(s0)).toBe(s1)
    h.record(s1) // a new change forgets the redo branch
    expect(h.canRedo).toBe(false)
    for (let i = 0; i < 10; i++) h.record([])
    let steps = 0
    while (h.undo([]) !== null) steps++
    expect(steps).toBe(3)
  })

  it('creates images, aligns a selection, remaps clipboard ids and debounces autosave', () => {
    const img = createImageElement('att-1', 4, 5, 80, 60, 'Logo')
    expect(img).toMatchObject({ type: 'Image', attachmentId: 'att-1', x: 4, y: 5, w: 80, h: 60, alt: 'Logo' })

    const round = elementFromGesture('roundRect', 0, 0, 100, 40)!
    expect(round.type).toBe('RoundRect')
    expect(round.cornerRadius).toBeGreaterThan(0)
    const star = elementFromGesture('star', 0, 0, 1, 1)!
    expect([star.type, star.w, star.h]).toEqual(['Star', 120, 120])
    const table = elementFromGesture('table', 0, 0, 90, 60)!
    expect(table.type).toBe('Table')
    expect(table.cells).toHaveLength(9)
    const hi = elementFromGesture('highlighter', 0, 0, 0, 0, [0, 0, 12, 0, 24, 6])!
    expect([hi.type, hi.strokeWidth]).toEqual(['Pen', 12])

    const a = el({ id: 'a', type: 'Rect', x: 0, y: 10, w: 20, h: 10 })
    const b = el({ id: 'b', type: 'Rect', x: 50, y: 0, w: 10, h: 40 })
    const c = el({ id: 'c', type: 'Rect', x: 100, y: 0, w: 10, h: 10 })
    const ids = ['a', 'b']
    expect(alignLeft([a, b], ids).find((e) => e.id === 'b')!.x).toBe(0)
    expect(alignRight([a, b], ids).find((e) => e.id === 'a')!.x).toBe(40)
    expect(alignTop([a, b], ids).find((e) => e.id === 'a')!.y).toBe(0)
    expect(alignBottom([a, b], ids).find((e) => e.id === 'a')!.y).toBe(30)
    expect(alignCenterX([a, b], ids).map((e) => e.x)).toEqual([20, 25])
    expect(distributeH([a, b, c], ['a', 'b', 'c']).find((e) => e.id === 'b')!.x).toBe(55)
    const alone = [a]
    expect(alignLeft(alone, ['a'])).toBe(alone)

    const link = createConnector('a', 'b', [20, 15, 50, 20])
    const pasted = pasteElements(copyElements([a, b, link]), { x: 8, y: 4 })
    expect(pasted[0]!.id).not.toBe('a')
    expect(pasted[1]!.id).not.toBe('b')
    expect(pasted[2]!.fromId).toBe(pasted[0]!.id)
    expect(pasted[2]!.toId).toBe(pasted[1]!.id)
    expect(pasted[0]).toMatchObject({ x: 8, y: 14 })
    expect(pasted[2]!.id).not.toBe(link.id)

    const grouped = groupElements([a, b], ['a', 'b'])
    expect(grouped.elements.find((e) => e.id === 'a')!.parentId).toBe(grouped.id)
    const flat = ungroupElements(grouped.elements, grouped.id!)
    expect(flat.some((e) => e.type === 'Group')).toBe(false)
    expect(flat.find((e) => e.id === 'a')!.parentId).toBeNull()

    vi.useFakeTimers()
    try {
      const fn = vi.fn()
      const queued = debounce(fn, 800)
      queued()
      queued()
      vi.advanceTimersByTime(799)
      expect(fn).not.toHaveBeenCalled()
      vi.advanceTimersByTime(1)
      expect(fn).toHaveBeenCalledTimes(1)
      queued()
      queued.cancel()
      vi.advanceTimersByTime(800)
      expect(fn).toHaveBeenCalledTimes(1)
    } finally {
      vi.useRealTimers()
    }
  })
})

const tenantCtx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
const me = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }
const space = (canCreate: boolean) => ({ id: 's1', key: 'ENG', name: 'Engineering', description: null, type: 'Team', status: 'Active', homePageId: 'home', updatedAt: '2026-11-07T08:00:00Z', createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', revision: 1, requirePublishApproval: false, rights: { canView: true, canCreate, canAdminister: false, canManageTemplates: false } })
const withEtag = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"r1"' } })

const elements = [el({ id: 'r1', type: 'Rect', x: 10, y: 20, w: 100, h: 60 }), el({ id: 't1', type: 'Text', x: 0, y: 100, text: 'Plan' })]
const board = (canEdit: boolean, over: Record<string, unknown> = {}) => ({
  id: 'b1', spaceKey: 'ENG', spaceName: 'Engineering', title: 'Retro', elementCount: 2, elements, createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', updatedAt: '2026-11-07T08:00:00Z', revision: 5, canEdit, ...over,
})

describe('whiteboards', () => {
  let wrapper: VueWrapper | null = null
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
    resetRouterBootstrap()
  })
  afterEach(() => {
    wrapper?.unmount()
    wrapper = null
    vi.unstubAllGlobals()
  })

  const base = {
    'GET /api/v1/host/context': () => json(tenantCtx),
    'GET /api/v1/auth/me': () => json(me),
    'GET /api/v1/auth/me/workspaces': () => json([]),
    'GET /api/v1/auth/antiforgery': () => json({ token: 'xsrf' }),
    'GET /api/v1/notifications/unread-count': () => json({ count: 0 }),
    'GET /api/v1/spaces/ENG/tree': () => json([]),
  }

  async function open(path: string, f: ReturnType<typeof fakeFetch>) {
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push(path)
    wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    for (let i = 0; i < 6; i++) {
      await new Promise((r) => setTimeout(r, 30))
      await flushPromises()
    }
    return wrapper
  }

  it('lists boards and creates an empty one only for members who may create', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => withEtag(space(true)),
      'GET /api/v1/spaces/ENG/whiteboards': () => json([board(true, { elements: [] })]),
      'POST /api/v1/spaces/ENG/whiteboards': () => json(board(true, { id: 'b2', title: 'Ideas', elements: [], elementCount: 0 }), 201),
      'GET /api/v1/whiteboards/b2': () => json(board(true, { id: 'b2', title: 'Ideas', elements: [], elementCount: 0 })),
    })
    const w = await open('/spaces/ENG/whiteboards', f)
    expect(w.get('[data-testid="board"]').text()).toContain('Retro')
    expect(w.get('[data-testid="board"]').text()).toContain('2 elements')

    await w.get('[data-testid="new-board"]').trigger('click')
    // the dialog is rendered asynchronously; wait for it instead of assuming one tick is enough (flaky on a loaded machine)
    for (let i = 0; i < 100 && !document.body.querySelector('[data-testid="board-title"]'); i++) {
      await new Promise((r) => setTimeout(r, 20))
      await flushPromises()
    }
    const input = document.body.querySelector<HTMLInputElement>('[data-testid="board-title"]')!
    input.value = 'Ideas'
    input.dispatchEvent(new Event('input'))
    await flushPromises()
    document.body.querySelector<HTMLButtonElement>('[data-testid="save-board"]')!.click()
    for (let i = 0; i < 100 && !f.calls.some((c) => c.url === '/api/v1/whiteboards/b2'); i++) {
      await new Promise((r) => setTimeout(r, 30))
      await flushPromises()
    }
    const post = f.calls.find((c) => c.method === 'POST' && c.url === '/api/v1/spaces/ENG/whiteboards')!
    expect(post.body).toEqual({ title: 'Ideas', elements: [] })
    expect(f.calls.some((c) => c.url === '/api/v1/whiteboards/b2')).toBe(true)

    wrapper?.unmount()
    resetRouterBootstrap()
    const g = fakeFetch({ ...base, 'GET /api/v1/spaces/ENG': () => withEtag(space(false)), 'GET /api/v1/spaces/ENG/whiteboards': () => json([]) })
    const w2 = await open('/spaces/ENG/whiteboards', g)
    expect(w2.find('[data-testid="new-board"]').exists()).toBe(false)
    expect(w2.get('[data-testid="no-boards"]').text()).toContain('No whiteboards')
  })

  it('creates, edits, undoes and saves elements with the board ETag', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/whiteboards/b1': () => json(board(true)),
      'PUT /api/v1/whiteboards/b1': () => json(board(true, { revision: 6, elementCount: 3 })),
    })
    const w = await open('/whiteboards/b1', f)
    expect(w.get('[data-testid="board-canvas"]').attributes('data-count')).toBe('2')
    expect(w.findAll('[data-testid="outline-item"]')).toHaveLength(2)
    expect((w.get('[data-testid="save-board"]').element as HTMLButtonElement).disabled).toBe(true)

    // A drawn sticky note arrives from the canvas: it is added, selected, the tool returns to "select", and the text goes through the inspector.
    await w.get('[data-testid="tool-sticky"]').trigger('click')
    expect(w.get('[data-testid="board-canvas"]').attributes('data-tool')).toBe('sticky')
    await w.get('[data-testid="stub-create"]').trigger('click')
    expect(w.get('[data-testid="board-canvas"]').attributes('data-count')).toBe('3')
    expect(w.get('[data-testid="board-canvas"]').attributes('data-tool')).toBe('select')
    const text = w.get('[data-testid="element-text"]')
    ;(text.element as HTMLTextAreaElement).value = 'سلام'
    await text.trigger('input')
    expect(w.find('[data-testid="unsaved"]').exists()).toBe(true)

    // Undo walks back the text edit, then the creation; redo replays both.
    await w.get('[data-testid="undo"]').trigger('click')
    await w.get('[data-testid="undo"]').trigger('click')
    expect(w.get('[data-testid="board-canvas"]').attributes('data-count')).toBe('2')
    expect(w.find('[data-testid="unsaved"]').exists()).toBe(false)
    await w.get('[data-testid="redo"]').trigger('click')
    expect(w.get('[data-testid="board-canvas"]').attributes('data-count')).toBe('3')
    await w.get('[data-testid="redo"]').trigger('click') // second redo restores the text edit

    // A move reported by the canvas is a change like any other; deleting uses the keyboard on the selected element.
    await w.get('[data-testid="stub-move"]').trigger('click')
    await w.findAll('[data-testid="outline-item"]')[0]!.trigger('click')
    await w.get('[data-testid="whiteboard"]').trigger('keydown', { key: 'Delete' })
    expect(w.get('[data-testid="board-canvas"]').attributes('data-count')).toBe('2')

    await w.get('[data-testid="save-board"]').trigger('click')
    await flushPromises()
    const put = f.calls.find((c) => c.method === 'PUT' && c.url === '/api/v1/whiteboards/b1')!
    expect(put.headers['if-match'] ?? put.headers['If-Match']).toBe('"r5"')
    const sent = put.body as { title: string; elements: BoardElement[] }
    expect(sent.title).toBe('Retro')
    expect(sent.elements.map((e) => e.id)).toEqual(['t1', 'new1']) // r1 was deleted, order kept
    expect(sent.elements[1]).toMatchObject({ type: 'Sticky', text: 'سلام', rtl: true })
    expect(w.find('[data-testid="unsaved"]').exists()).toBe(false)
  })

  it('is read-only without Edit, exports PNG from the canvas and links to the SVG export', async () => {
    const f = fakeFetch({ ...base, 'GET /api/v1/whiteboards/b1': () => json(board(false)) })
    const w = await open('/whiteboards/b1', f)
    expect(w.find('[data-testid="tool-rect"]').exists()).toBe(false)
    expect(w.find('[data-testid="save-board"]').exists()).toBe(false)
    expect(w.find('[data-testid="inspector"]').exists()).toBe(false)
    expect(w.find('[data-testid="delete-board"]').exists()).toBe(false)
    expect(w.get('h1').text()).toBe('Retro')
    expect(w.get('[data-testid="export-svg"]').attributes('href')).toBe('/api/v1/whiteboards/b1/export')

    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    await w.get('[data-testid="export-png"]').trigger('click')
    expect(click).toHaveBeenCalledTimes(1)
    click.mockRestore()
  })
})
