import { beforeEach, describe, expect, it, vi } from 'vitest'
import { createPinia, setActivePinia } from 'pinia'
import { createMemoryHistory } from 'vue-router'
import { api, ApiError, configureHttp } from '@/core/api/http'
import { takeFragmentToken } from '@/core/api/identity'
import { errorMessage } from '@/core/api/errors'
import { createRaaviRouter, resetRouterBootstrap, safeRedirect } from '@/router'
import { createRaaviI18n } from '@/core/i18n'
import { fakeFetch, json, problem } from './support/fakeApi'

const tenantCtx = {
  kind: 'tenant',
  tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'fa', timeZone: 'Asia/Tehran', calendar: 'persian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } },
}
const member = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: null, hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }

describe('http client', () => {
  it('fetches the antiforgery token once and sends it only on unsafe requests', async () => {
    const f = fakeFetch({
      'GET /api/v1/auth/antiforgery': () => json({ token: 'tok-1' }),
      'GET /api/v1/x': () => json({ ok: true }),
      'POST /api/v1/x': () => json({ ok: true }),
      'DELETE /api/v1/x': () => new Response(null, { status: 204 }),
    })
    configureHttp(f.fn)
    await api('GET', '/x')
    await api('POST', '/x', { a: 1 })
    await api('DELETE', '/x')
    expect(f.calls.filter((c) => c.url === '/api/v1/auth/antiforgery')).toHaveLength(1)
    expect(f.calls.find((c) => c.method === 'GET' && c.url === '/api/v1/x')!.headers['X-XSRF-TOKEN']).toBeUndefined()
    expect(f.calls.find((c) => c.method === 'POST')!.headers['X-XSRF-TOKEN']).toBe('tok-1')
    expect(f.calls.every((c) => !JSON.stringify(c).toLowerCase().includes('tenantid'))).toBe(true)
  })

  it('retries once with a fresh token after csrf.invalid, then surfaces the error', async () => {
    let n = 0
    const f = fakeFetch({
      'GET /api/v1/auth/antiforgery': () => json({ token: `tok-${++n}` }),
      'POST /api/v1/x': (init) => ((init.headers as Record<string, string>)['X-XSRF-TOKEN'] === 'tok-2' ? json({ ok: 1 }) : problem(400, 'csrf.invalid')),
    })
    configureHttp(f.fn)
    await expect(api('POST', '/x', {})).resolves.toEqual({ ok: 1 })

    const g = fakeFetch({ 'GET /api/v1/auth/antiforgery': () => json({ token: 't' }), 'POST /api/v1/y': () => problem(409, 'group.name_taken') })
    configureHttp(g.fn)
    await expect(api('POST', '/y', {})).rejects.toMatchObject({ status: 409, code: 'group.name_taken' })
  })
})

describe('errorMessage', () => {
  it('maps dotted error codes to nested catalog entries and hides unknown server text', () => {
    const i18n = createRaaviI18n('en').global
    const t = (k: string) => i18n.t(k)
    const te = (k: string) => i18n.te(k)
    expect(errorMessage(new ApiError(401, 'auth.invalid_credentials', 'x'), t, te)).toContain('incorrect')
    expect(errorMessage(new ApiError(500, 'internal.stacktrace', 'SqlException at ...'), t, te)).toBe('Something went wrong. Please try again.')
    expect(errorMessage(new Error('boom'), t, te)).toBe('Something went wrong. Please try again.')
  })
})

describe('fragment tokens and redirects', () => {
  it('reads the token from the fragment and removes it from the address bar', () => {
    const replace = vi.fn()
    const loc = { hash: '#token=abcDEF_123-abcDEF_123-xyz', pathname: '/invitations/accept', search: '' } as Location
    expect(takeFragmentToken(loc, { state: null, replaceState: replace } as unknown as History)).toBe('abcDEF_123-abcDEF_123-xyz')
    expect(replace).toHaveBeenCalledWith(null, '', '/invitations/accept')
    expect(takeFragmentToken({ hash: '#token=<script>', pathname: '/', search: '' } as Location, { replaceState: vi.fn() } as unknown as History)).toBeNull()
  })

  it('accepts only same-app relative redirects', () => {
    expect(safeRedirect('/admin/members')).toBe('/admin/members')
    expect(safeRedirect('//evil.com')).toBeNull()
    expect(safeRedirect('https://evil.com')).toBeNull()
    expect(safeRedirect('/\\evil.com')).toBeNull()
    expect(safeRedirect(undefined)).toBeNull()
  })
})

describe('router guards', () => {
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
    resetRouterBootstrap()
  })

  async function routerFor(routes: Parameters<typeof fakeFetch>[0]) {
    const f = fakeFetch(routes)
    configureHttp(f.fn)
    vi.stubGlobal('fetch', f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    return router
  }

  it('sends anonymous users to the login page with a safe redirect', async () => {
    const router = await routerFor({ 'GET /api/v1/host/context': () => json(tenantCtx), 'GET /api/v1/auth/me': () => problem(401, 'auth.required') })
    await router.push('/admin/members')
    expect(router.currentRoute.value.name).toBe('login')
    expect(router.currentRoute.value.query.redirect).toBe('/admin/members')
  })

  it('keeps non-administrators out of tenant administration', async () => {
    const router = await routerFor({ 'GET /api/v1/host/context': () => json(tenantCtx), 'GET /api/v1/auth/me': () => json(member) })
    await router.push('/admin/members')
    expect(router.currentRoute.value.name).toBe('home')
  })

  it('routes are bound to their host kind', async () => {
    const router = await routerFor({ 'GET /api/v1/host/context': () => json({ kind: 'system', tenant: null }), 'GET /api/v1/auth/me': () => problem(401, 'auth.required') })
    await router.push('/login')
    expect(router.currentRoute.value.name).toBe('system-home')
    await router.push('/system/tenants')
    expect(router.currentRoute.value.name).toBe('system-login')
  })

  it('unknown hosts only ever show the not-found page', async () => {
    const router = await routerFor({ 'GET /api/v1/host/context': () => problem(404, 'tenant.unavailable') })
    await router.push('/login')
    expect(router.currentRoute.value.name).toBe('not-found')
  })

  it('signed-in users skip the login page', async () => {
    const router = await routerFor({ 'GET /api/v1/host/context': () => json(tenantCtx), 'GET /api/v1/auth/me': () => json(member) })
    await router.push('/login')
    expect(router.currentRoute.value.name).toBe('home')
  })
})
