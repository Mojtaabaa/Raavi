import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createMemoryHistory } from 'vue-router'
import * as Y from 'yjs'
import { ref } from 'vue'
import App from '@/app/App.vue'
import { configureHttp } from '@/core/api/http'
import { createRaaviI18n } from '@/core/i18n'
import { createRaaviRouter, resetRouterBootstrap } from '@/router'
import type { BoardElement } from '@/core/api/whiteboards'
import { LiveBoard } from '@/modules/whiteboards/liveBoard'
import { fakeFetch, json } from './support/fakeApi'

// Konva needs a real canvas (see whiteboards.test.ts); the live session is replaced by an in-memory Yjs document.
const live = vi.hoisted(() => ({ available: true, fail: false, joins: 0, current: null as null | { doc: unknown; status: { value: string }; peers: { value: unknown[] }; board: unknown } }))

vi.mock('@/modules/whiteboards/BoardCanvas.vue', async () => {
  const { defineComponent, h } = await import('vue')
  return {
    default: defineComponent({
      props: ['elements', 'tool', 'selectedId', 'readOnly', 'stroke', 'fill', 'label'],
      emits: ['select', 'create', 'change'],
      setup(props, { emit, expose }) {
        expose({ zoomBy: () => undefined, resetView: () => undefined, toPng: () => null })
        const el = { id: 'new1', type: 'Sticky', x: 5, y: 6, w: 170, h: 130, rotation: 0, stroke: '#1f2937', fill: '#fde68a', strokeWidth: 2, text: '', fontSize: 20, rtl: false, points: [] }
        return () =>
          h('div', { 'data-testid': 'board-canvas', 'data-count': props.elements.length, 'data-readonly': String(props.readOnly) }, [
            h('button', { 'data-testid': 'stub-create', onClick: () => emit('create', el) }),
          ])
      },
    }),
  }
})

vi.mock('@/modules/whiteboards/liveSession', async () => {
  const { LiveBoard: Board } = await import('@/modules/whiteboards/liveBoard')
  const Yjs = await import('yjs')
  const { ref: vueRef } = await import('vue')
  return {
    liveBoardsAvailable: async () => live.available,
    startBoardSession: async () => {
      live.joins++
      if (live.fail) throw new Error('no ticket')
      const doc = new Yjs.Doc()
      const session = { doc, board: new Board(doc), status: vueRef('connecting'), peers: vueRef([] as unknown[]), destroy: () => undefined }
      live.current = session
      return session
    },
  }
})

const tenantCtx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
const me = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }
const el = (id: string, over: Partial<BoardElement> = {}): BoardElement => ({ id, type: 'Rect', x: 0, y: 0, w: 100, h: 50, rotation: 0, stroke: '#1f2937', fill: null, strokeWidth: 2, text: '', fontSize: 20, rtl: false, points: [], ...over })
const board = (canEdit: boolean, over: Record<string, unknown> = {}) => ({
  id: 'b1', spaceKey: 'ENG', spaceName: 'Engineering', title: 'Retro', elementCount: 1, elements: [el('r1')], createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', updatedAt: '2026-11-07T08:00:00Z', revision: 5, canEdit, ...over,
})

describe('live whiteboard editing', () => {
  let wrapper: VueWrapper | null = null
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
    resetRouterBootstrap()
    Object.assign(live, { available: true, fail: false, joins: 0, current: null })
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
  }

  async function open(f: ReturnType<typeof fakeFetch>) {
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/whiteboards/b1')
    wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    await settle()
    return wrapper
  }

  const settle = async () => {
    for (let i = 0; i < 6; i++) {
      await new Promise((r) => setTimeout(r, 30))
      await flushPromises()
    }
  }

  const session = () => live.current as unknown as { doc: Y.Doc; board: LiveBoard; status: { value: string }; peers: { value: unknown[] } }
  /** What the collaboration server does on the first load: the content arrives as a *remote* update, so it is not part of the user's undo history. */
  const seedFromServer = (elements: BoardElement[]) => {
    const server = new Y.Doc()
    new LiveBoard(server).apply(elements)
    Y.applyUpdate(session().doc, Y.encodeStateAsUpdate(server), 'remote')
  }
  const count = (w: VueWrapper) => w.get('[data-testid="board-canvas"]').attributes('data-count')

  it('waits for the first sync (read-only), then edits go to Yjs, remote changes appear, there is no Save, and undo is the user’s own', async () => {
    const f = fakeFetch({ ...base, 'GET /api/v1/whiteboards/b1': () => json(board(true)), 'PUT /api/v1/whiteboards/b1/title': () => json(board(true, { title: 'Sprint retro', revision: 6 })) })
    const w = await open(f)
    expect(live.joins).toBe(1)
    expect(w.get('[data-testid="live-status"]').text()).toContain('Connecting')
    expect(w.get('[data-testid="board-canvas"]').attributes('data-readonly')).toBe('true') // no edits until the first sync
    expect(w.find('[data-testid="tool-rect"]').exists()).toBe(false)

    // The server seeds the document; once synced the view switches to it and becomes editable.
    seedFromServer([el('r1'), el('seeded', { type: 'Sticky', text: 'from server' })])
    session().status.value = 'live'
    await settle()
    expect(count(w)).toBe('2')
    expect(w.get('[data-testid="live-status"]').text()).toContain('Live')
    expect(w.get('[data-testid="board-canvas"]').attributes('data-readonly')).toBe('false')
    expect(w.find('[data-testid="save-board"]').exists()).toBe(false) // saving is continuous
    expect(w.find('[data-testid="unsaved"]').exists()).toBe(false)

    // A local change is written into the Yjs document, not sent as a whole-board save.
    await w.get('[data-testid="stub-create"]').trigger('click')
    expect(count(w)).toBe('3')
    expect(session().board.toElements().map((e) => e.id)).toEqual(['r1', 'seeded', 'new1'])
    expect(f.calls.some((c) => c.method === 'PUT' && c.url === '/api/v1/whiteboards/b1')).toBe(false)

    // Someone else adds an element: it shows up and an undo does not remove it.
    const remote = new Y.Doc()
    Y.applyUpdate(remote, Y.encodeStateAsUpdate(session().doc))
    const other = new LiveBoard(remote)
    other.apply([...other.toElements(), el('theirs', { type: 'Ellipse' })])
    Y.applyUpdate(session().doc, Y.encodeStateAsUpdate(remote, Y.encodeStateVector(session().doc)), 'remote')
    await settle()
    expect(count(w)).toBe('4')

    await w.get('[data-testid="undo"]').trigger('click')
    await settle()
    expect(session().board.toElements().map((e) => e.id)).toEqual(['r1', 'seeded', 'theirs']) // own sticky gone, theirs kept
    expect(count(w)).toBe('3')

    // Peers are listed; the title is saved on its own.
    session().peers.value = [{ clientId: 7, name: 'Reza', color: '#123456' }]
    await settle()
    expect(w.get('[data-testid="peers"]').text()).toBe('Reza')
    const input = w.get('[data-testid="board-title-input"]')
    ;(input.element as HTMLInputElement).value = 'Sprint retro'
    await input.trigger('input')
    await input.trigger('change')
    await flushPromises()
    expect(f.calls.find((c) => c.method === 'PUT' && c.url === '/api/v1/whiteboards/b1/title')!.body).toEqual({ title: 'Sprint retro' })
  })

  it('falls back to ordinary editing when live editing is unavailable, and for people who cannot edit', async () => {
    live.available = false
    const f = fakeFetch({ ...base, 'GET /api/v1/whiteboards/b1': () => json(board(true)) })
    const w = await open(f)
    expect(live.joins).toBe(0)
    expect(w.find('[data-testid="live-status"]').exists()).toBe(false)
    expect(w.find('[data-testid="tool-rect"]').exists()).toBe(true)
    await w.get('[data-testid="stub-create"]').trigger('click')
    expect(w.find('[data-testid="save-board"]').exists()).toBe(true) // whole-board saving as before
    expect((w.get('[data-testid="save-board"]').element as HTMLButtonElement).disabled).toBe(false)

    wrapper?.unmount()
    resetRouterBootstrap()
    live.available = true
    const g = fakeFetch({ ...base, 'GET /api/v1/whiteboards/b1': () => json(board(false)) })
    const w2 = await open(g)
    expect(live.joins).toBe(0) // readers never join the session
    expect(w2.find('[data-testid="live-status"]').exists()).toBe(false)

    wrapper?.unmount()
    resetRouterBootstrap()
    live.fail = true // the ticket is refused or the server is down
    const h = fakeFetch({ ...base, 'GET /api/v1/whiteboards/b1': () => json(board(true)) })
    const w3 = await open(h)
    expect(live.joins).toBe(1)
    expect(w3.find('[data-testid="live-status"]').exists()).toBe(false)
    expect(w3.find('[data-testid="tool-rect"]').exists()).toBe(true)
  })

  it('shows offline while disconnected and re-joins from the stored content after a reset', async () => {
    let loads = 0
    const f = fakeFetch({ ...base, 'GET /api/v1/whiteboards/b1': () => json(board(true, { elements: [el('r1'), el('fresh')], elementCount: 2 + loads++ * 0 })) })
    const w = await open(f)
    seedFromServer([el('r1')])
    session().status.value = 'live'
    await settle()
    session().status.value = 'offline'
    await settle()
    expect(w.get('[data-testid="live-status"]').text()).toContain('Offline')
    expect(w.get('[data-testid="board-canvas"]').attributes('data-readonly')).toBe('false') // edits are kept and sync later

    session().status.value = 'reset' // someone saved the whole board
    await settle()
    expect(live.joins).toBe(2) // joined again after reloading the board
    expect(f.calls.filter((c) => c.url === '/api/v1/whiteboards/b1' && c.method === 'GET')).toHaveLength(2)
    expect(ref(0).value).toBe(0)
  })
})
