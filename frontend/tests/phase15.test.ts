import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import { createMemoryHistory, createRouter } from 'vue-router'
import { createRaaviI18n } from '@/core/i18n'
import { spaceTrail, staticTrail } from '@/core/navigation/trail'
import { formatDuration, ping, probe, reportText, type StatusReport } from '@/modules/status/statusApi'
import { connection, markDown, markUp } from '@/core/api/connection'
import ConnectionBanner from '@/app/ConnectionBanner.vue'
import Breadcrumbs from '@/app/Breadcrumbs.vue'
import { useCrumbsStore } from '@/stores/crumbs'
import { buildTemplate, TEMPLATE_IDS } from '@/modules/whiteboards/boardTemplates'
import { MAX_ELEMENTS, pasteCopies, stepOrder } from '@/modules/whiteboards/boardModel'
import type { BoardElement } from '@/core/api/whiteboards'
import { reportClientError } from '@/core/runtime/errorReporter'
import { configureHttp } from '@/core/api/http'
import { themeVariables, THEMES } from '@/core/theme/themes'
import { fakeFetch, json } from './support/fakeApi'

const i18n = createRaaviI18n('en')
const t = (k: string) => i18n.global.t(k) as string

const report: StatusReport = {
  status: 'Degraded', checkedAt: '2026-11-09T10:00:00Z', durationMs: 12, version: '0.18.0', environment: 'Production', startedAt: '2026-11-09T09:00:00Z', uptimeSeconds: 3600,
  components: [{ name: 'sql', status: 'Healthy', durationMs: 4, description: null }, { name: 'workers', status: 'Degraded', durationMs: 5, description: 'No worker is running' }],
  process: { memoryMb: 150, threads: 30, gcGen2Collections: 1, cpuSeconds: 3 },
  traffic: { requests: 10, serverErrors: 1, clientErrors: 0, errorRatePercent: 10, averageMs: 20, p95Ms: 100, maxMs: 300, series: [] },
}

const response = (status: number, body: unknown, type = 'application/json') => new Response(typeof body === 'string' ? body : JSON.stringify(body), { status, headers: { 'content-type': type } })

describe('status probe: tells apart WHY the API cannot be reached', () => {
  it('reads a good answer', async () => {
    const p = await probe((async () => response(200, report)) as unknown as typeof fetch)
    expect(p.ok).toBe(true)
  })

  it('classifies gateway errors, a non-API answer, a switched-off status, a network failure and a timeout', async () => {
    const reasons = async (f: typeof fetch, timeout?: number) => {
      const p = await probe(f, timeout)
      return p.ok ? 'ok' : p.reason
    }
    expect(await reasons((async () => response(502, 'Bad Gateway', 'text/html')) as unknown as typeof fetch)).toBe('gateway')
    expect(await reasons((async () => response(504, '', 'text/plain')) as unknown as typeof fetch)).toBe('gateway')
    expect(await reasons((async () => response(200, '<html>spa</html>', 'text/html')) as unknown as typeof fetch)).toBe('notApi')
    expect(await reasons((async () => response(200, { hello: 'world' })) as unknown as typeof fetch)).toBe('notApi')
    expect(await reasons((async () => response(404, '', 'text/plain')) as unknown as typeof fetch)).toBe('disabled')
    expect(await reasons((async () => { throw new TypeError('Failed to fetch') }) as unknown as typeof fetch)).toBe('network')
    expect(await reasons(((_u: string, init?: RequestInit) => new Promise((_r, reject) => init?.signal?.addEventListener('abort', () => reject(new DOMException('x', 'AbortError'))))) as unknown as typeof fetch, 20)).toBe('timeout')
  })

  it('pings the liveness endpoint and formats times and the pasteable report', async () => {
    expect(await ping((async () => new Response('Healthy', { status: 200 })) as unknown as typeof fetch)).toBe(true)
    expect(await ping((async () => new Response('', { status: 503 })) as unknown as typeof fetch)).toBe(false)
    expect(await ping((async () => { throw new TypeError('x') }) as unknown as typeof fetch)).toBe(false)
    const u = { d: 'd', h: 'h', m: 'm', s: 's' }
    expect([formatDuration(5, u), formatDuration(3725, u), formatDuration(90061, u)]).toEqual(['5s', '1h 2m', '1d 1h 1m'])
    const text = reportText({ ok: true, report, roundTripMs: 40 }, new Date('2026-11-09T10:00:00Z'))
    expect(text).toContain('Overall: Degraded  version 0.18.0')
    expect(text).toContain('- workers: Degraded (5 ms) No worker is running')
    expect(reportText({ ok: false, reason: 'gateway', httpStatus: 502, roundTripMs: 9 }, new Date())).toContain('UNREACHABLE: gateway (HTTP 502)')
  })
})

describe('connection banner', () => {
  beforeEach(() => {
    markUp()
    connection.restored.value = false
  })

  it('shows when real requests fail, links to the status page and goes away when the API answers again', async () => {
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', component: { template: '<div/>' } }, { path: '/status', component: { template: '<div/>' } }] })
    const w = mount(ConnectionBanner, { global: { plugins: [i18n, router] } })
    expect(w.find('[data-testid="connection-down"]').exists()).toBe(false)
    markDown()
    await w.vm.$nextTick()
    expect(w.get('[data-testid="connection-down"]').text()).toContain('Cannot reach the server')
    expect(w.get('a').attributes('href')).toBe('/status')
    markUp()
    await w.vm.$nextTick()
    expect(w.find('[data-testid="connection-down"]').exists()).toBe(false)
    expect(w.find('[data-testid="connection-restored"]').exists()).toBe(true)
    w.unmount()
  })

  it('is set by the HTTP layer on network failures and gateway errors, and cleared by any answer', async () => {
    let mode: 'down' | 'gateway' | 'ok' = 'down'
    const f = (async () => {
      if (mode === 'down') throw new TypeError('Failed to fetch')
      return mode === 'gateway' ? response(502, '', 'text/plain') : response(200, { ok: true })
    }) as unknown as typeof fetch
    configureHttp(f)
    const { api } = await import('@/core/api/http')
    await expect(api('GET', '/x')).rejects.toBeTruthy()
    expect(connection.down.value).toBe(true)
    mode = 'ok'
    await api('GET', '/x')
    expect(connection.down.value).toBe(false)
    mode = 'gateway'
    await expect(api('GET', '/x')).rejects.toBeTruthy()
    expect(connection.down.value).toBe(true)
    markUp()
  })
})

describe('breadcrumbs', () => {
  it('builds trails from the route, or from what a view provides', () => {
    expect(staticTrail('home', t)).toEqual([])
    expect(staticTrail('admin-members', t).map((c) => c.label)).toEqual(['Home', 'Administration', 'Members'])
    expect(staticTrail('tasks', t).map((c) => c.label)).toEqual(['Home', 'Tasks'])
    expect(staticTrail('unknown-route', t)).toEqual([])
    const trail = spaceTrail({ key: 'ENG', name: 'Engineering' }, t, { label: 'Page' })
    expect(trail.map((c) => c.label)).toEqual(['Home', 'Spaces', 'Engineering', 'Page'])
    expect(trail[2]!.to).toEqual({ name: 'space', params: { key: 'ENG' } })
  })

  it('renders links for the way up and the current page as plain text; folds the middle on phones (kept reachable)', async () => {
    setActivePinia(createPinia())
    const router = createRouter({ history: createMemoryHistory(), routes: [{ path: '/', name: 'home', component: { template: '<div/>' } }, { path: '/spaces', name: 'spaces', component: { template: '<div/>' } }, { path: '/spaces/:key', name: 'space', component: { template: '<div/>' } }] })
    await router.push('/')
    useCrumbsStore().set(spaceTrail({ key: 'ENG', name: 'Engineering' }, t, { label: 'Parent', to: { name: 'space', params: { key: 'ENG' } } }, { label: 'Child' }, { label: 'Current page' }))
    const w = mount(Breadcrumbs, { global: { plugins: [i18n, router, PrimeVue] } })
    const text = w.get('[data-testid="breadcrumbs"]').text()
    expect(text).toContain('Home')
    expect(text).toContain('Current page')
    const last = w.get('[data-last]')
    expect(last.find('a').exists()).toBe(false)
    expect(last.get('[aria-current="page"]').text()).toBe('Current page')
    expect(w.findAll('li.middle').length).toBeGreaterThan(0) // CSS hides these on phones …
    expect(w.get('details.fold').findAll('li').length).toBe(w.findAll('li.middle').length) // … and the menu lists exactly them
    useCrumbsStore().clear()
    w.unmount()
  })
})

describe('whiteboard templates and editing helpers', () => {
  const words = Object.fromEntries(['start', 'step', 'step2', 'decision', 'yes', 'no', 'end', 'todo', 'doing', 'done', 'task', 'strengths', 'weaknesses', 'opportunities', 'threats', 'topic', 'idea'].map((k) => [k, `w-${k}`]))

  it('every template makes valid elements: unique ids, text where required, two points for arrows, sizes above zero', () => {
    for (const id of TEMPLATE_IDS) {
      const els = buildTemplate(id, 500, 300, words)
      expect(els.length, id).toBeGreaterThan(4)
      expect(els.length, id).toBeLessThan(MAX_ELEMENTS)
      expect(new Set(els.map((e) => e.id)).size, id).toBe(els.length)
      for (const e of els) {
        if (e.type === 'Text') expect(e.text.trim().length, `${id} text`).toBeGreaterThan(0)
        if (e.type === 'Arrow') expect(e.points.length, `${id} arrow`).toBe(4)
        if (['Rect', 'Ellipse', 'Diamond', 'Triangle', 'Sticky'].includes(e.type)) expect(e.w > 0 && e.h > 0, `${id} ${e.type}`).toBe(true)
        expect(e.id).toMatch(/^[A-Za-z0-9_-]{1,40}$/)
      }
    }
    const words2 = buildTemplate('swot', 0, 0, words).filter((e) => e.type === 'Text').map((e) => e.text)
    expect(words2).toEqual(['w-strengths', 'w-weaknesses', 'w-opportunities', 'w-threats'])
  })

  it('steps one layer at a time and pastes shifted copies with new ids, within the limit', () => {
    const mk = (id: string, x = 0): BoardElement => ({ id, type: 'Rect', x, y: 0, w: 10, h: 10, rotation: 0, stroke: '#000000', fill: null, strokeWidth: 2, text: '', fontSize: 20, rtl: false, points: [] })
    const list = [mk('a'), mk('b'), mk('c')]
    expect(stepOrder(list, 'a', 'forward').map((e) => e.id)).toEqual(['b', 'a', 'c'])
    expect(stepOrder(list, 'c', 'forward')).toBe(list) // already on top: nothing changes
    expect(stepOrder(list, 'a', 'backward')).toBe(list)
    expect(stepOrder(list, 'c', 'backward').map((e) => e.id)).toEqual(['a', 'c', 'b'])
    const copies = pasteCopies([mk('a', 5)], 30, 30, 10)
    expect(copies).toHaveLength(1)
    expect([copies[0]!.id === 'a', copies[0]!.x, copies[0]!.y]).toEqual([false, 35, 30])
    expect(pasteCopies([mk('a')], 30, 30, 0)).toEqual([])
  })
})

describe('browser error reporting', () => {
  afterEach(() => markUp())

  it('reports an error once, at most five per page load, and nothing while the server is unreachable', async () => {
    const f = fakeFetch({ 'GET /api/v1/auth/antiforgery': () => json({ token: 'x' }), 'POST /api/v1/errors/client': () => new Response(null, { status: 204 }) })
    configureHttp(f.fn)
    const boom = new Error('boom one')
    reportClientError(boom, 'ctx')
    reportClientError(boom, 'ctx') // the same error again: reported once
    await new Promise((r) => setTimeout(r, 30))
    const posts = () => f.calls.filter((c) => c.method === 'POST' && c.url.endsWith('/errors/client'))
    expect(posts()).toHaveLength(1)
    expect((posts()[0]!.body as { message: string }).message).toBe('ctx: boom one')
    for (let i = 0; i < 10; i++) reportClientError(new Error(`other ${i}`))
    await new Promise((r) => setTimeout(r, 30))
    expect(posts().length).toBeLessThanOrEqual(5)
    markDown()
    const before = posts().length
    reportClientError(new Error('while down'))
    await new Promise((r) => setTimeout(r, 30))
    expect(posts().length).toBe(before)
  })
})

describe('Material Design themes', () => {
  it('exist in a light and a dark variant with the Material baseline colours', () => {
    const light = themeVariables(THEMES.find((x) => x.id === 'material')!)
    const dark = themeVariables(THEMES.find((x) => x.id === 'materialdark')!)
    expect([light['--raavi-bg'], light['--raavi-icon']]).toEqual(['#fef7ff', '#6750a4'])
    expect(THEMES.find((x) => x.id === 'materialdark')!.dark).toBe(true)
    expect(dark['--raavi-bg']).toBe('#141218')
    expect(i18n.global.t('theme.names.material')).toBe('Material Design')
  })
})

beforeEach(() => vi.restoreAllMocks())
