import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import { createMemoryHistory, createRouter } from 'vue-router'
import SmartSearchView from '@/modules/ai/SmartSearchView.vue'
import { createRaaviI18n } from '@/core/i18n'
import { configureHttp } from '@/core/api/http'
import { fakeFetch, json, problem } from './support/fakeApi'

const hit = (over: Record<string, unknown> = {}) => ({ contentId: 'p1', title: 'Release runbook', spaceKey: 'OPS', snippet: 'Run the deploy checklist before you deploy <img src=x onerror=alert(1)>.', score: 0.03, semantic: false, ...over })

async function open(routes: Parameters<typeof fakeFetch>[0], query = '') {
  const f = fakeFetch({ 'GET /api/v1/spaces': () => json([{ key: 'OPS', name: 'Operations' }]), ...routes })
  configureHttp(f.fn)
  vi.stubGlobal('fetch', f.fn)
  const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/search', name: 'search', component: SmartSearchView }, { path: '/spaces/:key/pages/:id', name: 'page', component: { template: '<div/>' } }, { path: '/', name: 'home', component: { template: '<div/>' } }] })
  await router.push('/search' + query)
  const w = mount(SmartSearchView, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router] } })
  for (let i = 0; i < 4; i++) {
    await new Promise((r) => setTimeout(r, 30))
    await flushPromises()
  }
  return { w, f }
}

const type = async (w: VueWrapper, value: string) => {
  await w.get('[data-testid="smart-q"]').setValue(value)
  await new Promise((r) => setTimeout(r, 450))
  await flushPromises()
}

describe('smart search page', () => {
  let wrapper: VueWrapper | null = null
  beforeEach(() => setActivePinia(createPinia()))
  afterEach(() => {
    wrapper?.unmount()
    wrapper = null
    vi.unstubAllGlobals()
  })

  it('searches as you type, shows how it searched, marks the words and never turns page text into markup', async () => {
    const { w, f } = await open({
      'GET /api/v1/ai/status': () => json({ available: false }),
      'GET /api/v1/ai/search': () => json({ hits: [hit(), hit({ contentId: 'p2', title: 'Other', semantic: true })], mode: 'hybrid' }),
    })
    wrapper = w
    expect(w.get('[data-testid="mode-ask"]').attributes('disabled')).toBeDefined() // AI is off: asking is unavailable, searching is not
    await type(w, 'deploy checklist')
    const call = f.calls.find((c) => c.url.startsWith('/api/v1/ai/search'))!
    expect(call!.url).toMatch(/q=deploy(%20|\+)checklist/)
    expect(w.get('[data-testid="search-mode"]').text()).toContain('words and by meaning')
    const hits = w.findAll('[data-testid="hit"]')
    expect(hits).toHaveLength(2)
    expect(hits[0]!.attributes('href')).toBe('/spaces/OPS/pages/p1')
    expect(w.findAll('mark').map((m) => m.text().toLowerCase())).toContain('deploy')
    expect(w.find('img').exists()).toBe(false) // the <img onerror> in the page text is plain text
    expect(w.text()).toContain('<img src=x onerror=alert(1)>')
    expect(w.findAll('.sem')).toHaveLength(1) // only the semantic hit carries the marker
  })

  it('says so when nothing is found, searches in a chosen space and reports errors in words', async () => {
    let fail = false
    const { w, f } = await open({
      'GET /api/v1/ai/status': () => json({ available: false }),
      'GET /api/v1/ai/search': () => (fail ? problem(400, 'ai.query_invalid') : json({ hits: [], mode: 'keyword' })),
    })
    wrapper = w
    await w.get('[data-testid="smart-space"]').setValue('OPS')
    await type(w, 'nothing here')
    expect(f.calls.some((c) => c.url.includes('space=OPS'))).toBe(true)
    expect(w.get('[data-testid="no-hits"]').text()).toContain('Nothing found')
    expect(w.get('[data-testid="search-mode"]').text()).toContain('words')
    fail = true
    await type(w, 'again please')
    expect(w.get('[data-testid="smart-error"]').text()).toContain('Type at least two characters')
  })

  it('asks a question, links only valid [n] cites, marks cited sources, and keeps the notice', async () => {
    const { w, f } = await open(
      {
        'GET /api/v1/ai/status': () => json({ available: true }),
        'GET /api/v1/auth/antiforgery': () => json({ token: 'x' }),
        'POST /api/v1/ai/ask': () => json({
          text: 'Run the checklist [1] and invent [9].',
          model: 'm',
          promptTokens: 1,
          completionTokens: 1,
          sources: [
            { number: 1, contentId: 'p1', title: 'Release runbook', spaceKey: 'OPS', snippet: 'checklist…', cited: true },
            { number: 2, contentId: 'p2', title: 'On-call', spaceKey: 'OPS', snippet: 'engineer…', cited: false },
          ],
        }),
      },
      '?mode=ask',
    )
    wrapper = w
    expect(w.get('[data-testid="mode-ask"]').attributes('aria-pressed')).toBe('true')
    await w.get('[data-testid="smart-q"]').setValue('How do we deploy?')
    await w.get('form').trigger('submit')
    await flushPromises()
    expect(f.calls.find((c) => c.method === 'POST')!.body).toEqual({ question: 'How do we deploy?', space: null })
    const answer = w.get('[data-testid="answer"]')
    expect(answer.findAll('[data-testid="cite"]').map((a) => a.attributes('href'))).toEqual(['#source-1'])
    expect(answer.text()).toContain('[9]')
    expect(answer.text()).toContain('Machine-generated')
    expect(answer.get('[data-testid="cited-badge"]').text()).toMatch(/Cited|ارجاع/)
    expect(answer.get('[data-testid="source-2"]').classes()).toContain('uncited')
    expect(answer.findAll('ol a').map((a) => a.attributes('href'))).toEqual(['/spaces/OPS/pages/p1', '/spaces/OPS/pages/p2'])
  })
})
