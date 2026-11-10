import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createMemoryHistory } from 'vue-router'
import PromptHubView from '@/modules/prompts/PromptHubView.vue'
import PromptEditView from '@/modules/prompts/PromptEditView.vue'
import CategoryTree from '@/modules/prompts/CategoryTree.vue'
import { createRaaviI18n } from '@/core/i18n'
import { configureHttp } from '@/core/api/http'
import { createRaaviRouter, resetRouterBootstrap } from '@/router'
import type { PromptCategory, PromptSummary } from '@/core/api/prompts'
import { fakeFetch, json, type Handler } from './support/fakeApi'

const settle = async () => {
  for (let i = 0; i < 4; i++) {
    await new Promise((r) => setTimeout(r, 25))
    await flushPromises()
  }
}
const cat = (id: string, name: string, parentId: string | null, sortOrder = 0, mine = 0, pub = 0): PromptCategory => ({ id, parentId, name, description: null, sortOrder, status: 'Active', minePrompts: mine, publicPrompts: pub })
const prompt = (id: string, title: string, extra: Partial<PromptSummary> = {}): PromptSummary => ({
  id, title, description: 'Does a thing', categoryId: null, visibility: 'Personal', status: 'Draft', tags: ['review'], language: 'en', contexts: ['page'], modes: 7, ownerId: 'u', ownerName: 'Sara',
  createdAt: '2026-11-01T00:00:00Z', updatedAt: '2026-11-02T00:00:00Z', publishedAt: null, lastUsedAt: null, isOwner: true, canEdit: true, hasDraft: true, latestVersion: 1, forkedFromId: null, forkedFromTitle: null, revision: 1, ...extra,
})
const cats = [cat('eng', 'Engineering', null, 0, 2, 1), cat('rev', 'Reviews', 'eng', 0, 1, 1), cat('ops', 'Operations', null, 1)]

describe('Prompt Hub', () => {
  beforeEach(() => {
    localStorage.setItem('raavi.locale', 'en')
    setActivePinia(createPinia())
    resetRouterBootstrap()
  })
  afterEach(() => vi.unstubAllGlobals())

  function install(routes: Record<string, Handler>, admin = true) {
    const ctx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'a', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
    const me = { id: 'u', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: admin, isSystemAdministrator: false, provider: 'local' }
    const f = fakeFetch({ 'GET /api/v1/auth/antiforgery': () => json({ token: 'x' }), 'GET /api/v1/host/context': () => json(ctx), 'GET /api/v1/auth/me': () => json(me), 'GET /api/v1/auth/me/workspaces': () => json([]), ...routes })
    configureHttp(f.fn)
    vi.stubGlobal('fetch', f.fn)
    return f
  }
  const plugins = (router: unknown) => [createRaaviI18n('en'), PrimeVue, ToastService, ConfirmationService, router as never]

  it('shows the tree with counts for the chosen library, filters by category and searches with a delay', async () => {
    const f = install({
      'GET /api/v1/prompt-categories': () => json(cats),
      'GET /api/v1/prompts': () => json({ items: [prompt('1', 'Code review', { status: 'Published', visibility: 'Tenant' })], total: 1, skip: 0, take: 20 }),
    })
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/prompts')
    const w = mount(PromptHubView, { attachTo: document.body, global: { plugins: plugins(router) } })
    await settle()
    expect(document.body.querySelector('[data-testid="prompt-Code review"]')!.textContent).toContain('Public in workspace')
    expect(document.body.querySelector('[data-testid="count-Engineering"]')!.textContent).toBe('1') // the public library counts (default scope)
    document.body.querySelector<HTMLButtonElement>('[data-testid="category-Reviews"] .label')!.click()
    await settle()
    expect(f.calls.some((c) => c.url.includes('categoryId=rev') && c.url.includes('scope=public'))).toBe(true)
    const search = document.body.querySelector<HTMLInputElement>('[data-testid="prompt-search"]')!
    search.value = 'risk'
    search.dispatchEvent(new Event('input'))
    await new Promise((r) => setTimeout(r, 400))
    await settle()
    expect(f.calls.filter((c) => c.url.includes('q=risk')).length).toBe(1) // one request, not one per keystroke
    w.unmount()
  })

  it('asks what to do with the prompts before a category is deleted and sends the chosen strategy', async () => {
    const f = install({
      'GET /api/v1/prompt-categories': () => json(cats),
      'GET /api/v1/prompts': () => json({ items: [], total: 0, skip: 0, take: 20 }),
      'POST /api/v1/prompt-categories/eng/delete': () => json([cats[2]]),
    })
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/prompts')
    const w = mount(PromptHubView, { attachTo: document.body, global: { plugins: plugins(router) } })
    await settle()
    document.body.querySelector<HTMLButtonElement>('[data-testid="category-delete-Engineering"]')!.click()
    await settle()
    const dialog = document.body.querySelector('[data-testid="category-delete-dialog"]')!
    expect(dialog.textContent).toContain('not deleted')
    document.body.querySelector<HTMLInputElement>('#st-none')!.click()
    await settle()
    document.body.querySelector<HTMLButtonElement>('[data-testid="category-delete-confirm"]')!.click()
    await settle()
    expect(f.calls.find((c) => c.url.endsWith('/eng/delete'))!.body).toEqual({ strategy: 'uncategorized', targetCategoryId: null })
    w.unmount()
  })

  it('offers no category management to people who may not manage categories', async () => {
    install({ 'GET /api/v1/prompt-categories': () => json(cats), 'GET /api/v1/prompts': () => json({ items: [], total: 0, skip: 0, take: 20 }) }, false)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/prompts')
    const w = mount(PromptHubView, { attachTo: document.body, global: { plugins: plugins(router) } })
    await settle()
    expect(document.body.querySelector('[data-testid="category-add-root"]')).toBeNull()
    expect(document.body.querySelector('[data-testid="category-delete-Engineering"]')).toBeNull()
    expect(document.body.querySelector('[data-testid="category-root-drop"]')).toBeNull()
    w.unmount()
  })

  it('tree: collapses, filters by name keeping ancestors, and reports a drop as a move to the right parent and position', async () => {
    const w = mount(CategoryTree, { attachTo: document.body, props: { categories: cats, selected: null, scope: 'mine', canManage: true }, global: { plugins: [createRaaviI18n('en'), PrimeVue] } })
    expect(w.text()).toContain('Reviews')
    await w.get('.twisty:not(:empty)').trigger('click') // collapse Engineering
    expect(w.text()).not.toContain('Reviews')
    await w.get('[data-testid="category-search"]').setValue('review')
    expect(w.text()).toContain('Engineering') // the ancestor stays so the match can be found
    expect(w.text()).toContain('Reviews')
    expect(w.text()).not.toContain('Operations')
    await w.get('[data-testid="category-search"]').setValue('')

    // drag Operations into the middle of Engineering = become its child (after its existing child)
    const rows = w.findAll('[role="treeitem"][draggable="true"]')
    const ops = rows.find((r) => r.text().includes('Operations'))!
    const eng = rows.find((r) => r.text().startsWith('Engineering'))!
    await ops.trigger('dragstart')
    const rect = { top: 0, height: 100, bottom: 100, left: 0, right: 100, width: 100, x: 0, y: 0, toJSON: () => ({}) }
    vi.spyOn(eng.element.querySelector('.row')!.parentElement!, 'getBoundingClientRect').mockReturnValue(rect as DOMRect)
    await eng.trigger('dragover', { clientY: 50 })
    await eng.trigger('drop', { clientY: 50 })
    expect(w.emitted('move')![0]).toEqual(['ops', 'eng', 1])
    w.unmount()
  })

  it('creates a prompt from the form with the document, tags and modes the person chose', async () => {
    const created = { prompt: prompt('new1', 'Risk finder', { revision: 1 }), content: { schema: 'raavi.doc', schemaVersion: 2, doc: { type: 'doc', content: [{ type: 'paragraph' }] } }, contentText: 'Find {{page.title}}', shown: null, draft: null, published: null }
    const f = install({
      'GET /api/v1/prompt-categories': () => json(cats),
      'POST /api/v1/prompts': () => json(created, 201),
      'GET /api/v1/prompts/new1': () => json(created),
    }, false)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/prompts/new')
    const w = mount(PromptEditView, { attachTo: document.body, global: { plugins: plugins(router) } })
    await settle()
    const title = document.body.querySelector<HTMLInputElement>('[data-testid="prompt-title"]')!
    title.value = 'Risk finder'
    title.dispatchEvent(new Event('input'))
    const tags = document.body.querySelector<HTMLInputElement>('#p-tags')!
    tags.value = 'risk, quality'
    tags.dispatchEvent(new Event('input'))
    document.body.querySelector<HTMLInputElement>('[data-testid="mode-preview"]')!.click() // switch preview off
    await settle()
    document.body.querySelector<HTMLButtonElement>('[data-testid="prompt-save"]')!.click()
    await settle()
    const sent = f.calls.find((c) => c.method === 'POST' && c.url.endsWith('/api/v1/prompts'))!.body as Record<string, unknown>
    expect(sent.title).toBe('Risk finder')
    expect(sent.tags).toEqual(['risk', 'quality'])
    expect(sent.modes).toBe(3)
    expect((sent.content as { schema: string }).schema).toBe('raavi.doc')
    w.unmount()
  })
})
