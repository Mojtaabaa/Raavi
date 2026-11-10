import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createMemoryHistory } from 'vue-router'
import App from '@/app/App.vue'
import SmartSearchView from '@/modules/ai/SmartSearchView.vue'
import { createRaaviI18n } from '@/core/i18n'
import { configureHttp } from '@/core/api/http'
import { createRaaviRouter, resetRouterBootstrap } from '@/router'
import { normalizeSearchText } from '@/core/runtime/textNormalize'
import { fakeFetch, json } from './support/fakeApi'

describe('search text folding (twin of the server table)', () => {
  it('folds Arabic/Persian letter forms, digits, marks, half-spaces and case like Raavi.Application.Common.SearchText', () => {
    const n = normalizeSearchText
    expect(n('كتاب')).toBe(n('کتاب'))
    expect(n('يك علي')).toBe(n('یک علی'))
    expect(n('على')).toBe(n('علی'))
    expect(n('١٢٣ ۴۵۶')).toBe('123 456')
    expect(n('مُحَمَّد')).toBe(n('محمد'))
    expect(n('سلــام')).toBe(n('سلام'))
    expect(n('می‌شود')).toBe(n('می شود'))
    expect(n('إسلام')).toBe(n('اسلام'))
    expect(n('HeLLo')).toBe('hello')
    expect(n(null)).toBe('')
    // the exact table the server checks, spelled out: a change on one side must be made on the other
    expect(n('ي ى ئ ی ك ک ة ۀ ە أ إ ٱ آ ؤ')).toBe('ی ی ی ی ک ک ه ه ه ا ا ا ا و')
  })
})

describe('smart search highlighting follows the folding', () => {
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => vi.unstubAllGlobals())

  it('marks the original characters when the person typed another spelling', async () => {
    const f = fakeFetch({
      'GET /api/v1/spaces': () => json([]),
      'GET /api/v1/ai/status': () => json({ available: false }),
      'GET /api/v1/ai/search': () => json({ hits: [{ contentId: 'p', title: 'راهنما', spaceKey: 'OPS', snippet: 'این كتاب در سال ١٤٠٥ چاپ شد', score: 1, semantic: false }], mode: 'keyword' }),
    })
    configureHttp(f.fn)
    vi.stubGlobal('fetch', f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/search')
    const w = mount(SmartSearchView, { attachTo: document.body, global: { plugins: [createRaaviI18n('fa'), PrimeVue, router] } })
    await flushPromises()
    await w.get('[data-testid="smart-q"]').setValue('کتاب ۱۴۰۵')
    await new Promise((r) => setTimeout(r, 450))
    await flushPromises()
    expect(w.findAll('mark').map((m) => m.text())).toEqual(['كتاب', '١٤٠٥']) // the page's own spelling is what gets marked
    w.unmount()
  })
})

describe('application shell improvements', () => {
  let wrapper: VueWrapper | null = null
  beforeEach(() => {
    localStorage.clear()
    localStorage.setItem('raavi.locale', 'en')
    setActivePinia(createPinia())
    resetRouterBootstrap()
  })
  afterEach(() => {
    wrapper?.unmount()
    wrapper = null
    vi.unstubAllGlobals()
  })

  async function open(path: string, extra: Parameters<typeof fakeFetch>[0] = {}) {
    const ctx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
    const me = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }
    const f = fakeFetch({
      'GET /api/v1/host/context': () => json(ctx),
      'GET /api/v1/auth/me': () => json(me),
      'GET /api/v1/auth/me/workspaces': () => json([]),
      'GET /api/v1/auth/antiforgery': () => json({ token: 'x' }),
      'GET /api/v1/notifications/unread-count': () => json({ count: 0 }),
      'GET /api/v1/quick-find': () => json({ spaces: [], content: [] }),
      ...extra,
    })
    configureHttp(f.fn)
    vi.stubGlobal('fetch', f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push(path)
    wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    for (let i = 0; i < 5; i++) {
      await new Promise((r) => setTimeout(r, 30))
      await flushPromises()
    }
    return { f, router }
  }

  it('shows a real "page does not exist" page for unknown addresses, with a way to search for it', async () => {
    await open('/no/such/thing')
    const page = document.body.querySelector('[data-testid="page-not-found"]')!
    expect(page).not.toBeNull()
    expect(page.textContent).toContain('This page does not exist')
    expect(page.textContent).toContain('/no/such/thing')
    expect(page.querySelector('a[href^="/search"]')!.getAttribute('href')).toContain('q=thing')
  })

  it('has a Menu button for the folded navigation and opens the shortcut list with ? (but not while typing)', async () => {
    await open('/tasks', { 'GET /api/v1/tasks': () => json({ items: [], total: 0 }) })
    const toggle = document.body.querySelector<HTMLButtonElement>('[data-testid="menu-toggle"]')!
    expect(toggle.getAttribute('aria-expanded')).toBe('false')
    toggle.click()
    await flushPromises()
    expect(toggle.getAttribute('aria-expanded')).toBe('true')
    expect(document.body.querySelector('#main-nav')!.classList.contains('open')).toBe(true)

    const input = document.createElement('input')
    document.body.appendChild(input)
    input.focus()
    input.dispatchEvent(new KeyboardEvent('keydown', { key: '?', bubbles: true }))
    await flushPromises()
    expect(document.body.textContent).not.toContain('Keyboard shortcuts') // typing a question mark in a field is just typing
    input.remove()

    window.dispatchEvent(new KeyboardEvent('keydown', { key: '?' }))
    await flushPromises()
    await new Promise((r) => setTimeout(r, 50))
    expect(document.body.textContent).toContain('Keyboard shortcuts')
    expect(document.body.textContent).toContain('Quick find by title')
  })

  it('keeps rarely used pages (calendar, approvals) behind a "More" menu that closes on Escape', async () => {
    await open('/tasks', { 'GET /api/v1/tasks': () => json({ items: [], total: 0 }) })
    const nav = document.body.querySelector('#main-nav')!
    expect(nav.querySelector('a[href="/calendar"]')).toBeNull()
    expect(nav.querySelector('a[href="/tasks"]')).not.toBeNull()
    nav.querySelector<HTMLButtonElement>('[data-testid="more-menu"]')!.click()
    await flushPromises()
    const list = nav.querySelector('[data-testid="more-list"]')!
    expect(list.querySelector('a[href="/calendar"]')).not.toBeNull()
    expect(list.querySelector('a[href="/approvals"]')).not.toBeNull()
    list.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true }))
    await flushPromises()
    expect(nav.querySelector('[data-testid="more-list"]')).toBeNull()
  })

  it('offers a search of the page text in the quick-find palette and goes there on Enter when no title matches', async () => {
    const { router } = await open('/tasks', { 'GET /api/v1/tasks': () => json({ items: [], total: 0 }) })
    document.body.querySelector<HTMLButtonElement>('[data-testid="quick-find"]')!.click()
    await flushPromises()
    await new Promise((r) => setTimeout(r, 60))
    const input = document.body.querySelector<HTMLInputElement>('#quick-find')!
    input.value = 'backup policy'
    input.dispatchEvent(new Event('input'))
    await new Promise((r) => setTimeout(r, 300))
    await flushPromises()
    expect(document.body.querySelector('[data-testid="palette-text-search"]')!.textContent).toContain('backup policy')
    input.dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))
    await flushPromises()
    await new Promise((r) => setTimeout(r, 60))
    expect(router.currentRoute.value.name).toBe('search')
    expect(router.currentRoute.value.query.q).toBe('backup policy')
  })
})
