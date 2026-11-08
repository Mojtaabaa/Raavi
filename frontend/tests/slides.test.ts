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
import { fakeFetch, json } from './support/fakeApi'

const tenantCtx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
const me = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }
const space = (canCreate: boolean) => ({ id: 's1', key: 'ENG', name: 'Engineering', description: null, type: 'Team', status: 'Active', homePageId: 'home', updatedAt: '2026-11-07T08:00:00Z', createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', revision: 1, requirePublishApproval: false, rights: { canView: true, canCreate, canAdminister: false, canManageTemplates: false } })
const withEtag = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"r1"' } })

const slides = [
  { id: 'sa', layout: 'Title', title: 'Q3 review', body: 'Engineering', notes: '' },
  { id: 'sb', layout: 'Bullets', title: 'Wins', body: 'Tasks\nCalendar', notes: 'Mention databases' },
]
const deck = (canEdit: boolean, over: Record<string, unknown> = {}) => ({
  id: 'k1', spaceKey: 'ENG', spaceName: 'Engineering', title: 'Quarterly', direction: 'Ltr', slideCount: 2, slides, createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', updatedAt: '2026-11-07T08:00:00Z', revision: 3, canEdit, ...over,
})

describe('slides', () => {
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
    for (let i = 0; i < 4; i++) {
      await new Promise((r) => setTimeout(r, 30))
      await flushPromises()
    }
    return wrapper
  }

  it('lists decks and creates one with a title slide only for members who may create', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => withEtag(space(true)),
      'GET /api/v1/spaces/ENG/slides': () => json([deck(true, { slides: [] })]),
      'POST /api/v1/spaces/ENG/slides': () => json(deck(true, { id: 'k2', title: 'Kickoff' }), 201),
      'GET /api/v1/slides/k2': () => json(deck(true, { id: 'k2', title: 'Kickoff' })),
    })
    const w = await open('/spaces/ENG/slides', f)
    expect(w.get('[data-testid="deck"]').text()).toContain('Quarterly')
    expect(w.get('[data-testid="deck"]').text()).toContain('2 slides')

    await w.get('[data-testid="new-deck"]').trigger('click')
    await flushPromises()
    const input = document.body.querySelector<HTMLInputElement>('[data-testid="deck-title"]')!
    input.value = 'Kickoff'
    input.dispatchEvent(new Event('input'))
    await flushPromises()
    document.body.querySelector<HTMLButtonElement>('[data-testid="save-deck"]')!.click()
    for (let i = 0; i < 12; i++) {
      await new Promise((r) => setTimeout(r, 30))
      await flushPromises()
    }
    const post = f.calls.find((c) => c.method === 'POST' && c.url === '/api/v1/spaces/ENG/slides')!
    expect(post.body).toEqual({ title: 'Kickoff', direction: 'Ltr', slides: [{ layout: 'Title', title: 'Kickoff', body: '', notes: '' }] })
    expect(f.calls.some((c) => c.url === '/api/v1/slides/k2')).toBe(true)

    wrapper?.unmount()
    resetRouterBootstrap()
    const g = fakeFetch({ ...base, 'GET /api/v1/spaces/ENG': () => withEtag(space(false)), 'GET /api/v1/spaces/ENG/slides': () => json([]) })
    const w2 = await open('/spaces/ENG/slides', g)
    expect(w2.find('[data-testid="new-deck"]').exists()).toBe(false)
    expect(w2.get('[data-testid="no-decks"]').text()).toContain('No slide decks')
  })

  it('edits, adds and reorders slides locally and saves the whole deck with its ETag', async () => {
    const saved = deck(true, { revision: 4, slides: [slides[1], { id: 'sn', layout: 'Bullets', title: 'Next', body: '', notes: '' }, slides[0]], slideCount: 3 })
    const f = fakeFetch({
      ...base,
      'GET /api/v1/slides/k1': () => json(deck(true)),
      'PUT /api/v1/slides/k1': () => json(saved),
    })
    const w = await open('/slides/k1', f)
    expect(w.findAll('[data-testid="thumb"]')).toHaveLength(2)
    expect(w.get('[data-testid="slide-canvas"]').text()).toContain('Q3 review')
    expect((w.get('[data-testid="save-deck"]').element as HTMLButtonElement).disabled).toBe(true)

    // Edit the second slide's title; the preview follows and saving becomes possible.
    await w.findAll('[data-testid="thumb"]')[1]!.trigger('click')
    const title = w.get('[data-testid="slide-title"]')
    ;(title.element as HTMLInputElement).value = 'Wins and misses'
    await title.trigger('input')
    expect(w.get('[data-testid="slide-canvas"]').text()).toContain('Wins and misses')
    expect((w.get('[data-testid="save-deck"]').element as HTMLButtonElement).disabled).toBe(false)
    expect(w.find('[data-testid="unsaved"]').exists()).toBe(true)

    // Add a slide after it, then move the Wins slide to the top.
    await w.get('[data-testid="add-slide"]').trigger('click')
    expect(w.findAll('[data-testid="thumb"]')).toHaveLength(3)
    await w.findAll('[data-testid="thumb"]')[1]!.trigger('click')
    await w.get('[data-testid="move-up"]').trigger('click')
    await w.get('[data-testid="save-deck"]').trigger('click')
    await flushPromises()

    const put = f.calls.find((c) => c.method === 'PUT' && c.url === '/api/v1/slides/k1')!
    expect(put.headers['if-match'] ?? put.headers['If-Match']).toBe('"r3"')
    expect(put.body).toEqual({
      title: 'Quarterly',
      direction: 'Ltr',
      slides: [
        { id: 'sb', layout: 'Bullets', title: 'Wins and misses', body: 'Tasks\nCalendar', notes: 'Mention databases' },
        { id: 'sa', layout: 'Title', title: 'Q3 review', body: 'Engineering', notes: '' },
        { layout: 'Bullets', title: '', body: '', notes: '' },
      ],
    })
    expect(w.find('[data-testid="unsaved"]').exists()).toBe(false) // state follows the server response
  })

  it('presents with the keyboard, reverses arrows for right-to-left decks, and shows notes', async () => {
    const f = fakeFetch({ ...base, 'GET /api/v1/slides/k1': () => json(deck(true, { direction: 'Rtl' })) })
    const w = await open('/slides/k1', f)
    await w.get('[data-testid="present"]').trigger('click')
    expect(w.get('[data-testid="present-position"]').text()).toBe('1 / 2')

    const press = async (key: string) => {
      window.dispatchEvent(new KeyboardEvent('keydown', { key }))
      await flushPromises()
    }
    await press('ArrowRight') // backwards in RTL: already on the first slide
    expect(w.get('[data-testid="present-position"]').text()).toBe('1 / 2')
    await press('ArrowLeft') // forwards in RTL
    expect(w.get('[data-testid="present-position"]').text()).toBe('2 / 2')
    expect(w.get('[data-testid="present-notes"]').text()).toBe('Mention databases')
    await press('ArrowLeft') // already on the last slide
    expect(w.get('[data-testid="present-position"]').text()).toBe('2 / 2')
    await press('Escape')
    expect(w.find('[data-testid="presenting"]').exists()).toBe(false)
  })

  it('is read-only without Edit and links to the PPTX export', async () => {
    const f = fakeFetch({ ...base, 'GET /api/v1/slides/k1': () => json(deck(false)) })
    const w = await open('/slides/k1', f)
    expect(w.find('[data-testid="slide-title"]').exists()).toBe(false)
    expect(w.find('[data-testid="add-slide"]').exists()).toBe(false)
    expect(w.find('[data-testid="save-deck"]').exists()).toBe(false)
    expect(w.get('h1').text()).toBe('Quarterly')
    expect(w.get('[data-testid="export-pptx"]').attributes('href')).toBe('/api/v1/slides/k1/export')
    expect(w.get('[data-testid="export-pdf"]').attributes('href')).toBe('/slides/k1/print?autoprint=1')
  })

  it('renders every slide on the shell-less print page and opens the print dialog only when asked', async () => {
    const print = vi.fn()
    vi.stubGlobal('print', print)
    const f = fakeFetch({ ...base, 'GET /api/v1/slides/k1': () => json(deck(false)) })
    const w = await open('/slides/k1/print', f)
    expect(w.findAll('[data-testid="print-slide"]')).toHaveLength(2)
    expect(w.find('nav, header').exists()).toBe(false) // no application shell
    expect(print).not.toHaveBeenCalled()
    await w.get('[data-testid="print-now"]').trigger('click')
    expect(print).toHaveBeenCalledTimes(1)

    wrapper?.unmount()
    resetRouterBootstrap()
    const g = fakeFetch({ ...base, 'GET /api/v1/slides/k1': () => json(deck(false)) })
    await open('/slides/k1/print?autoprint=1', g)
    await new Promise((r) => setTimeout(r, 250))
    expect(print).toHaveBeenCalledTimes(2)
  })
})
