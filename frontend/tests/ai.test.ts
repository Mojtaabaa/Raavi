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
import { fakeFetch, json, problem } from './support/fakeApi'

const tenantCtx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
const admin = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: true, isSystemAdministrator: false, provider: 'local' }
const withEtag = (body: unknown, tag = '"r3"') => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json', ETag: tag } })

const settings = (over: Record<string, unknown> = {}) => ({
  enabled: true, baseUrl: 'https://ai.example.test/v1/', model: 'wiki-model', hasApiKey: true, monthlyTokenCap: 1000, maxOutputTokens: 800, revision: 3, hostPolicyConfigured: true, tokensThisMonth: 250, ...over,
})
const usage = { tokensThisMonth: 250, requestsThisMonth: 4, failedThisMonth: 1, monthlyTokenCap: 1000, byFeature: [], topUsers: [{ userId: 'u1', name: 'Sara', requests: 4, tokens: 250 }] }

describe('ai', () => {
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
    'GET /api/v1/auth/me': () => json(admin),
    'GET /api/v1/auth/me/workspaces': () => json([]),
    'GET /api/v1/auth/antiforgery': () => json({ token: 'xsrf' }),
    'GET /api/v1/notifications/unread-count': () => json({ count: 0 }),
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

  it('shows the settings without the key, keeps the stored key unless told otherwise, and sends the revision', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/ai/settings': () => withEtag(settings()),
      'GET /api/v1/ai/usage': () => json(usage),
      'PUT /api/v1/ai/settings': () => withEtag(settings({ model: 'other-model', revision: 4 }), '"r4"'),
    })
    const w = await open('/admin/ai', f)
    expect((w.get('[data-testid="ai-url"]').element as HTMLInputElement).value).toBe('https://ai.example.test/v1/')
    expect((w.get('[data-testid="ai-key"]').element as HTMLInputElement).value).toBe('') // never filled in
    expect((w.get('[data-testid="ai-key"]').element as HTMLInputElement).placeholder).toContain('A key is stored')
    expect(w.get('[data-testid="ai-usage"]').text()).toContain('250 tokens / 1000 (25%)')
    expect(w.find('[data-testid="no-hosts"]').exists()).toBe(false)

    const model = w.get('[data-testid="ai-model"]')
    ;(model.element as HTMLInputElement).value = 'other-model'
    await model.trigger('input')
    await w.get('[data-testid="ai-save"]').trigger('submit')
    await flushPromises()
    const put = f.calls.find((c) => c.method === 'PUT')!
    expect(put.headers['if-match'] ?? put.headers['If-Match']).toBe('"r3"')
    expect(put.body).toEqual({ enabled: true, baseUrl: 'https://ai.example.test/v1/', model: 'other-model', embeddingModel: '', monthlyTokenCap: 1000, maxOutputTokens: 800 }) // no apiKey: the stored one stays

    // Typing a key sends it; removing sends an empty string.
    const key = w.get('[data-testid="ai-key"]')
    ;(key.element as HTMLInputElement).value = 'sk-new'
    await key.trigger('input')
    await w.get('[data-testid="ai-save"]').trigger('submit')
    await flushPromises()
    expect(f.calls.filter((c) => c.method === 'PUT')[1]!.body).toMatchObject({ apiKey: 'sk-new' })
    expect((w.get('[data-testid="ai-key"]').element as HTMLInputElement).value).toBe('') // cleared after saving

    await w.get('[data-testid="ai-remove-key"]').setValue(true)
    await w.get('[data-testid="ai-enabled"]').setValue(false)
    await w.get('[data-testid="ai-save"]').trigger('submit')
    await flushPromises()
    expect(f.calls.filter((c) => c.method === 'PUT')[2]!.body).toMatchObject({ enabled: false, apiKey: '' })
  })

  it('warns when the installation allows no AI host and will not switch AI on without a key', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/ai/settings': () => withEtag(settings({ enabled: false, hasApiKey: false, revision: 0, hostPolicyConfigured: false, baseUrl: '', model: '' }), '"r0"'),
      'GET /api/v1/ai/usage': () => json({ ...usage, tokensThisMonth: 0, requestsThisMonth: 0, failedThisMonth: 0, topUsers: [] }),
    })
    const w = await open('/admin/ai', f)
    expect(w.get('[data-testid="no-hosts"]').text()).toContain('has not allowed any AI host')
    const set = async (sel: string, v: string) => {
      const el = w.get(sel)
      ;(el.element as HTMLInputElement).value = v
      await el.trigger('input')
    }
    await set('[data-testid="ai-url"]', 'https://ai.example.test/v1')
    await set('[data-testid="ai-model"]', 'm')
    await w.get('[data-testid="ai-enabled"]').setValue(true)
    expect((w.get('[data-testid="ai-save"]').element as HTMLButtonElement).disabled).toBe(true) // enabled, but no key yet
    await set('[data-testid="ai-key"]', 'sk-1')
    expect((w.get('[data-testid="ai-save"]').element as HTMLButtonElement).disabled).toBe(false)
  })

  it('the page assistant appears only when AI is on, shows answers as plain text and explains failures', async () => {
    const page = {
      id: 'p1', spaceKey: 'ENG', status: 'Current', contentType: 'page', title: 'Plan',
    }
    // The assistant is tested on its own: the full page view needs a lot of unrelated endpoints.
    const { default: PageAssistant } = await import('@/modules/ai/PageAssistant.vue')
    const mountIt = async (f: ReturnType<typeof fakeFetch>) => {
      vi.stubGlobal('fetch', f.fn)
      configureHttp(f.fn)
      const router = createRaaviRouter(createMemoryHistory())
      wrapper?.unmount()
      wrapper = mount(PageAssistant, { props: { pageId: page.id }, attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
      await flushPromises()
      return wrapper
    }

    const off = await mountIt(fakeFetch({ 'GET /api/v1/ai/status': () => json({ available: false }) }))
    expect(off.find('[data-testid="assistant"]').exists()).toBe(false)

    let failNext = false
    const f = fakeFetch({
      'GET /api/v1/auth/antiforgery': () => json({ token: 'xsrf' }),
      'GET /api/v1/ai/status': () => json({ available: true }),
      'POST /api/v1/ai/pages/p1/summary': () => json({ text: 'We ship <b>Friday</b>.\nSecond line.', model: 'wiki-model', promptTokens: 10, completionTokens: 5, truncated: true }),
      'POST /api/v1/ai/pages/p1/ask': () => (failNext ? problem(429, 'ai.rate_limited') : json({ text: 'On Friday.', model: 'wiki-model', promptTokens: 8, completionTokens: 2, truncated: false })),
    })
    const on = await mountIt(f)
    expect(on.find('[data-testid="summarize"]').exists()).toBe(false) // collapsed until opened
    await on.get('[data-testid="assistant-toggle"]').trigger('click')
    await on.get('[data-testid="summarize"]').trigger('click')
    await flushPromises()
    const answer = on.get('[data-testid="answer"]')
    expect(answer.get('.text').text()).toBe('We ship <b>Friday</b>.\nSecond line.') // text, never HTML
    expect(answer.find('b').exists()).toBe(false)
    expect(answer.text()).toContain('Generated by wiki-model')
    expect(answer.text()).toContain('only its beginning was used')

    expect((on.get('[data-testid="ask"]').element as HTMLButtonElement).disabled).toBe(true)
    const q = on.get('[data-testid="question"]')
    ;(q.element as HTMLInputElement).value = 'When do we ship?'
    await q.trigger('input')
    await on.get('[data-testid="ask"]').trigger('submit')
    await flushPromises()
    expect(f.calls.find((c) => c.url.endsWith('/ask'))!.body).toEqual({ question: 'When do we ship?' })
    expect(on.get('[data-testid="answer"]').text()).toContain('On Friday.')

    failNext = true
    await on.get('[data-testid="ask"]').trigger('submit')
    await flushPromises()
    expect(on.find('[data-testid="answer"]').exists()).toBe(false)
    expect(on.get('[data-testid="ai-error"]').text()).toContain('hourly AI limit')
  })
})
