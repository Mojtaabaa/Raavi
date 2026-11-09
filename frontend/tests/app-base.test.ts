import { afterEach, describe, expect, it, vi } from 'vitest'
import { apiPrefix, apiUrl, detectBase, prefixed } from '@/core/runtime/base'
import { configureHttp, api, resetAntiforgery } from '@/core/api/http'
import { fetchHostContext } from '@/core/api/hostContext'
import { fakeFetch, json } from './support/fakeApi'

describe('where the app is mounted (path-addressed workspaces and the /system area)', () => {
  afterEach(() => {
    window.history.replaceState(null, '', '/')
    resetAntiforgery()
    vi.unstubAllGlobals()
  })

  it.each([
    ['/', '', ''],
    ['/spaces/ENG', '', ''],
    ['/t/dev', '/t/dev', '/t/dev'],
    ['/t/dev/', '/t/dev', '/t/dev'],
    ['/t/Dev-1/spaces/ENG/pages/x', '/t/dev-1', '/t/dev-1'],
    ['/system', '', '/system'],
    ['/system/login', '', '/system'],
    ['/systemx', '', ''],
    ['/t', '', ''],
    ['/tx/dev', '', ''],
    ['/t/-bad', '', ''],
    ['/t/with_underscore', '', ''],
  ])('%s -> router base "%s", API prefix "%s"', (path, routerBase, prefix) => {
    expect(detectBase(path)).toEqual({ routerBase, apiPrefix: prefix })
  })

  it('prefixes API calls, the host context, the anti-forgery token and absolute server paths for the page being shown', async () => {
    window.history.replaceState(null, '', '/t/dev/spaces/ENG')
    expect(apiPrefix()).toBe('/t/dev')
    expect(apiUrl('/spaces')).toBe('/t/dev/api/v1/spaces')
    expect(prefixed('/hubs/events')).toBe('/t/dev/hubs/events')

    const f = fakeFetch({
      'GET /t/dev/api/v1/host/context': () => json({ kind: 'tenant', tenant: { slug: 'dev', displayName: { fa: 'د', en: 'Dev' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }),
      'GET /t/dev/api/v1/auth/antiforgery': () => json({ token: 'xsrf' }),
      'GET /t/dev/api/v1/spaces': () => json([]),
      'POST /t/dev/api/v1/spaces': () => json({ key: 'X' }, 201),
    })
    configureHttp(f.fn)
    expect((await fetchHostContext(f.fn)).kind).toBe('tenant')
    expect(await api('GET', '/spaces')).toEqual([])
    await api('POST', '/spaces', { name: 'x' })
    expect(f.calls.map((c) => `${c.method} ${c.url}`)).toEqual([
      'GET /t/dev/api/v1/host/context',
      'GET /t/dev/api/v1/spaces',
      'GET /t/dev/api/v1/auth/antiforgery',
      'POST /t/dev/api/v1/spaces',
    ])
  })

  it('uses /system/api on system pages and no prefix on an ordinary address', async () => {
    window.history.replaceState(null, '', '/system/login')
    const f = fakeFetch({ 'GET /system/api/v1/host/context': () => json({ kind: 'system', tenant: null }), 'GET /api/v1/host/context': () => json({ kind: 'tenant', tenant: null }) })
    expect((await fetchHostContext(f.fn)).kind).toBe('system')

    window.history.replaceState(null, '', '/spaces')
    await fetchHostContext(f.fn)
    expect(f.calls.map((c) => c.url)).toEqual(['/system/api/v1/host/context', '/api/v1/host/context'])
  })
})
