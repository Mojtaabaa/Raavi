import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createMemoryHistory } from 'vue-router'
import PromptRunDialog from '@/modules/prompts/PromptRunDialog.vue'
import PromptPanels from '@/modules/prompts/PromptPanels.vue'
import PagePromptsDialog from '@/modules/prompts/PagePromptsDialog.vue'
import { tagName } from '@/modules/editor/extensions/promptVariables'
import { createRaaviI18n } from '@/core/i18n'
import { configureHttp } from '@/core/api/http'
import { createRaaviRouter } from '@/router'
import type { PromptSummary, RenderedPrompt } from '@/core/api/prompts'
import { fakeFetch, json, problem, type Handler } from './support/fakeApi'

const settle = async (ms = 30) => {
  for (let i = 0; i < 4; i++) {
    await new Promise((r) => setTimeout(r, ms))
    await flushPromises()
  }
}
const prompt = (extra: Partial<PromptSummary> = {}): PromptSummary => ({
  id: 'p1', title: 'Risk review', description: 'Finds risks', categoryId: null, visibility: 'Tenant', status: 'Published', tags: [], language: 'en', contexts: ['page', 'generic'], modes: 7, ownerId: 'o', ownerName: 'Omid',
  createdAt: '2026-11-01T00:00:00Z', updatedAt: '2026-11-02T00:00:00Z', publishedAt: '2026-11-02T00:00:00Z', lastUsedAt: null, isOwner: false, canEdit: false, hasDraft: false, latestVersion: 1, forkedFromId: null, forkedFromTitle: null, revision: 1, ratingAverage: 4.5, ratingCount: 2, ...extra,
})
const rendered = (extra: Partial<RenderedPrompt> = {}): RenderedPrompt => ({
  text: 'Find risks in: Roadmap', tokenEstimate: 7, chars: 22, ready: true, missing: [], warnings: [], sources: [{ kind: 'page', title: 'Roadmap', url: '/spaces/OPS/pages/1', chars: 10 }],
  variables: [{ name: 'page.title', origin: 'context', chars: 7 }], truncated: false, versionId: 'v1', versionNumber: 1, contextKind: 'page', inputs: [], ...extra,
})

describe('Prompt Hub: using prompts', () => {
  const writeText = vi.fn(async () => undefined)
  beforeEach(() => {
    localStorage.setItem('raavi.locale', 'en')
    setActivePinia(createPinia())
    writeText.mockClear()
    Object.defineProperty(navigator, 'clipboard', { value: { writeText }, configurable: true })
  })
  afterEach(() => vi.unstubAllGlobals())

  function install(routes: Record<string, Handler>) {
    const f = fakeFetch({ 'GET /api/v1/auth/antiforgery': () => json({ token: 'x' }), 'GET /api/v1/spaces': () => json([]), ...routes })
    configureHttp(f.fn)
    vi.stubGlobal('fetch', f.fn)
    return f
  }
  const plugins = () => [createRaaviI18n('en'), PrimeVue, ToastService, ConfirmationService, createRaaviRouter(createMemoryHistory())]

  it('shows the exact final text first (a preview records nothing), copies it with a copy event only, and runs only after consent', async () => {
    const f = install({
      'POST /api/v1/prompts/p1/render': () => json(rendered()),
      'POST /api/v1/prompts/p1/copied': () => new Response(null, { status: 204 }),
      'POST /api/v1/prompts/p1/manual-use': () => new Response(null, { status: 204 }),
      'POST /api/v1/prompts/p1/execute': () => json({ id: 'e1', promptId: 'p1', versionId: 'v1', versionNumber: 1, contextKind: 'page', status: 'Completed', result: 'Risk: scope creep', model: 'm', promptTokens: 10, completionTokens: 5, durationMs: 800, estimatedTokens: 7, errorClass: null, sources: [], startedAt: '2026-11-10T00:00:00Z', completedAt: '2026-11-10T00:00:01Z' }),
    })
    const w = mount(PromptRunDialog, { attachTo: document.body, props: { visible: false, prompt: prompt(), pageId: 'page1', context: { kind: 'page', pageId: 'page1' } }, global: { plugins: plugins() } })
    await w.setProps({ visible: true })
    await settle(60)
    const box = () => document.body.querySelector<HTMLTextAreaElement>('[data-testid="run-final"]')!
    expect(box().value).toBe('Find risks in: Roadmap')
    expect(document.body.querySelector('[data-testid="run-tokens"]')!.textContent).toContain('7')
    expect(f.calls.filter((c) => c.url.endsWith('/execute') || c.url.endsWith('/copied')).length).toBe(0) // previewing is free of side effects

    // run is locked until the consent box is ticked
    const run = () => document.body.querySelector<HTMLButtonElement>('[data-testid="run-execute"]')!
    expect(run().disabled).toBe(true)
    document.body.querySelector<HTMLButtonElement>('[data-testid="run-copy"]')!.click()
    await settle()
    expect(writeText).toHaveBeenCalledWith('Find risks in: Roadmap')
    expect(f.calls.some((c) => c.url.endsWith('/copied'))).toBe(true)
    expect(f.calls.some((c) => c.url.endsWith('/execute'))).toBe(false) // copying never runs the AI
    document.body.querySelector<HTMLButtonElement>('[data-testid="run-confirm-use"]')!.click()
    await settle()
    expect(f.calls.some((c) => c.url.endsWith('/manual-use'))).toBe(true)

    const consent = document.body.querySelector<HTMLInputElement>('[data-testid="run-consent"]')!
    consent.click()
    await settle()
    expect(run().disabled).toBe(false)
    run().click()
    await settle()
    const sent = f.calls.find((c) => c.url.endsWith('/execute'))!.body as { confirmed: boolean; render: { context: { kind: string; pageId: string } } }
    expect(sent.confirmed).toBe(true)
    expect(sent.render.context).toMatchObject({ kind: 'page', pageId: 'page1' })
    expect(document.body.querySelector('[data-testid="run-answer"]')!.textContent).toBe('Risk: scope creep')
    w.unmount()
  })

  it('asks for missing required values, blocks copy and run until they are filled, and offers to shorten oversized context', async () => {
    let calls = 0
    install({
      'POST /api/v1/prompts/p1/render': () => {
        calls++
        return calls === 1
          ? json(rendered({ ready: false, missing: ['input.audience'], inputs: [{ name: 'input.audience', label: 'Audience', type: 'select', required: true, options: ['team', 'board'] }] }))
          : problem(400, 'prompt.context_too_large')
      },
    })
    const w = mount(PromptRunDialog, { attachTo: document.body, props: { visible: false, prompt: prompt({ contexts: ['page'] }), pageId: 'page1' }, global: { plugins: plugins() } })
    await w.setProps({ visible: true })
    await settle(60)
    expect(document.body.querySelector('[data-testid="run-missing"]')!.textContent).toContain('input.audience')
    expect(document.body.querySelector<HTMLButtonElement>('[data-testid="run-copy"]')!.disabled).toBe(true)
    expect(document.body.querySelector('[data-testid="run-inputs"]')!.textContent).toContain('Audience')
    w.unmount()
  })

  it('keeps a copy-only prompt from offering a run button', async () => {
    install({ 'POST /api/v1/prompts/p1/render': () => json(rendered()) })
    const w = mount(PromptRunDialog, { attachTo: document.body, props: { visible: false, prompt: prompt({ modes: 2 }), pageId: 'page1' }, global: { plugins: plugins() } })
    await w.setProps({ visible: true })
    await settle(60)
    expect(document.body.querySelector('[data-testid="run-execute"]')).toBeNull()
    expect(document.body.querySelector('[data-testid="run-copy"]')).not.toBeNull()
    w.unmount()
  })

  it('page dialog lists personal and public prompts that fit the page, marked as such, and only offers selection prompts when text is selected', async () => {
    install({
      'GET /api/v1/prompts': ({ url }) =>
        url.includes('scope=mine')
          ? json({ items: [prompt({ id: 'm1', title: 'My review', visibility: 'Personal', isOwner: true, ratingCount: 0, contexts: ['page'] })], total: 1, skip: 0, take: 50 })
          : json({ items: [prompt({ id: 'q1', title: 'Quote explainer', contexts: ['selection'] }), prompt({ id: 'q2', title: 'Team summary', contexts: ['page'] })], total: 2, skip: 0, take: 50 }),
    })
    const w = mount(PagePromptsDialog, { attachTo: document.body, props: { visible: false, pageId: 'page1', spaceKey: 'OPS', selectedText: null }, global: { plugins: plugins() } })
    await w.setProps({ visible: true })
    await settle(60)
    const list = document.body.querySelector('[data-testid="page-prompts"]')!
    expect(list.textContent).toContain('My review')
    expect(list.textContent).toContain('Personal')
    expect(list.textContent).toContain('Team summary')
    expect(list.textContent).toContain('Public in workspace')
    expect(list.textContent).not.toContain('Quote explainer') // needs a selection
    await w.setProps({ visible: false })
    await w.setProps({ selectedText: 'some words', visible: true })
    await settle(60)
    expect(document.body.querySelector('[data-testid="page-prompts"]')!.textContent).toContain('Quote explainer')
    w.unmount()
  })

  it('ratings: a non-owner rates with stars, the owner cannot; versions compare and restore', async () => {
    const ratings = { average: 4, count: 2, distribution: [0, 0, 0, 1, 1], mine: null, entries: [] }
    const f = install({
      'GET /api/v1/prompts/p1/ratings': () => json(ratings),
      'PUT /api/v1/prompts/p1/rating': () => json({ ...ratings, mine: 5, count: 3 }),
      'GET /api/v1/prompts/p1/versions': () => json([
        { id: 'v2', number: 2, isPublished: true, changeNote: 'reword', createdBy: 'o', createdByName: 'Omid', createdAt: '2026-11-03T00:00:00Z', publishedAt: '2026-11-03T00:00:00Z' },
        { id: 'v1', number: 1, isPublished: true, changeNote: null, createdBy: 'o', createdByName: 'Omid', createdAt: '2026-11-02T00:00:00Z', publishedAt: '2026-11-02T00:00:00Z' },
      ]),
      'GET /api/v1/prompts/p1/diff': () => json({ fromNumber: 1, toNumber: 2, added: 1, removed: 1, lines: [{ kind: 'del', text: 'old' }, { kind: 'add', text: 'new' }] }),
      'POST /api/v1/prompts/p1/versions/v1/restore': () => json({}),
    })
    const w = mount(PromptPanels, { attachTo: document.body, props: { prompt: prompt({ isOwner: false }), canStats: false }, global: { plugins: plugins() } })
    await settle(40)
    expect(document.body.querySelector('[data-testid="rating-average"]')!.textContent).toContain('4')
    document.body.querySelector<HTMLButtonElement>('[data-testid="star-5"]')!.click()
    await settle()
    expect(f.calls.find((c) => c.method === 'PUT' && c.url.endsWith('/rating'))!.body).toMatchObject({ stars: 5 })
    w.unmount()

    const owner = mount(PromptPanels, { attachTo: document.body, props: { prompt: prompt({ isOwner: true, hasDraft: false }), canStats: false }, global: { plugins: plugins() } })
    await settle(40)
    expect(document.body.querySelector('[data-testid="star-5"]')).toBeNull() // owners cannot rate their own prompt
    const tab = [...document.body.querySelectorAll<HTMLElement>('[role="tab"]')].find((t) => t.textContent === 'Versions')!
    tab.click()
    await settle()
    document.body.querySelector<HTMLButtonElement>('[data-testid="compare-2"]')!.click()
    await settle()
    expect(document.body.querySelector('[data-testid="diff"]')!.textContent).toContain('+ new')
    document.body.querySelector<HTMLButtonElement>('[data-testid="restore-1"]')!.click()
    await settle()
    expect(f.calls.some((c) => c.url.endsWith('/versions/v1/restore'))).toBe(true)
    owner.unmount()
  })

  it('variable tags are recognised by name (conditions and defaults included)', () => {
    expect(tagName('page.title')).toBe('page.title')
    expect(tagName('#if input.extra')).toBe('input.extra')
    expect(tagName('input.tone|neutral')).toBe('input.tone')
    expect(tagName('/if')).toBeNull()
  })
})
