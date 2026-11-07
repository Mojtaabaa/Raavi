import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createMemoryHistory } from 'vue-router'
import App from '@/app/App.vue'
import { createRaaviRouter, resetRouterBootstrap } from '@/router'
import { createRaaviI18n } from '@/core/i18n'
import { configureHttp } from '@/core/api/http'
import { safeExternalUrl } from '@/core/api/identity'
import { fakeFetch, json, problem } from './support/fakeApi'

const tenantCtx = {
  kind: 'tenant',
  tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } },
}
const providers = [
  { id: 'local', type: 'Local', flow: 'credentials', displayName: { fa: 'حساب راوی', en: 'Raavi account' }, isDefault: true },
  { id: 'corp-ad', type: 'ActiveDirectory', flow: 'credentials', displayName: { fa: 'اکتیو دایرکتوری', en: 'Corporate AD' }, isDefault: false },
  { id: 'jira-eng', type: 'JiraDataCenter', flow: 'redirect', displayName: { fa: 'جیرا', en: 'Engineering Jira' }, isDefault: false },
]
const me = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: null, hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }

async function mountAt(path: string, routes: Parameters<typeof fakeFetch>[0]) {
  const f = fakeFetch(routes)
  configureHttp(f.fn)
  vi.stubGlobal('fetch', f.fn)
  const router = createRaaviRouter(createMemoryHistory())
  await router.push(path)
  const wrapper = mount(App, { global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
  await flushPromises()
  await flushPromises()
  return { wrapper, calls: f.calls, router }
}

describe('provider sign-in', () => {
  beforeEach(() => {
    localStorage.clear()
    localStorage.setItem('raavi.locale', 'en')
    setActivePinia(createPinia())
    resetRouterBootstrap()
  })

  it('offers redirect providers and starts the flow with the safe return path', async () => {
    const assign = vi.fn()
    vi.stubGlobal('location', { ...window.location, assign })
    const { wrapper, calls } = await mountAt('/login?redirect=/admin/members', {
      'GET /api/v1/host/context': () => json(tenantCtx),
      'GET /api/v1/auth/me': () => problem(401, 'auth.required'),
      'GET /api/v1/auth/providers': () => json(providers),
      'GET /api/v1/auth/antiforgery': () => json({ token: 't' }),
      'POST /api/v1/auth/login/jira-eng/begin': () => json({ redirectUrl: 'https://jira.company.local/rest/oauth2/latest/authorize?state=s' }),
    })
    const button = wrapper.get('[data-testid="provider-jira-eng"]')
    expect(button.text()).toContain('Continue with Engineering Jira')
    await button.trigger('click')
    await flushPromises()
    expect(calls.find((c) => c.url.endsWith('/begin'))!.body).toEqual({ returnPath: '/admin/members' })
    expect(assign).toHaveBeenCalledWith('https://jira.company.local/rest/oauth2/latest/authorize?state=s')
    vi.unstubAllGlobals()
  })

  it('switches between credential providers (local / directory)', async () => {
    const { wrapper, calls } = await mountAt('/login', {
      'GET /api/v1/host/context': () => json(tenantCtx),
      'GET /api/v1/auth/me': () => problem(401, 'auth.required'),
      'GET /api/v1/auth/providers': () => json(providers),
      'GET /api/v1/auth/antiforgery': () => json({ token: 't' }),
      'POST /api/v1/auth/login/credentials/corp-ad': () => problem(401, 'auth.invalid_credentials'),
    })
    const ad = wrapper.findAll('[data-testid="credential-provider"] button').find((b) => b.text() === 'Corporate AD')!
    await ad.trigger('click')
    expect(wrapper.text()).toContain('Username')
    await wrapper.get('[data-testid="login-email"]').setValue('sara')
    await wrapper.get('[data-testid="login-password"] input').setValue('x')
    await wrapper.get('form').trigger('submit')
    await flushPromises()
    expect(calls.some((c) => c.method === 'POST' && c.url === '/api/v1/auth/login/credentials/corp-ad')).toBe(true)
    expect(wrapper.get('[data-testid="login-error"]').text()).toContain('incorrect')
    vi.unstubAllGlobals()
  })

  it('shows provider outcomes passed back as error codes, ignoring anything else', async () => {
    const { wrapper } = await mountAt('/login?error=auth.pending_approval', {
      'GET /api/v1/host/context': () => json(tenantCtx),
      'GET /api/v1/auth/me': () => problem(401, 'auth.required'),
      'GET /api/v1/auth/providers': () => json(providers),
    })
    expect(wrapper.get('[data-testid="login-error"]').text()).toContain('awaits approval')

    const injected = await mountAt('/login?error=%3Cimg%20src%3Dx%3E', {
      'GET /api/v1/host/context': () => json(tenantCtx),
      'GET /api/v1/auth/me': () => problem(401, 'auth.required'),
      'GET /api/v1/auth/providers': () => json(providers),
    })
    expect(injected.wrapper.find('[data-testid="login-error"]').exists()).toBe(false)
    vi.unstubAllGlobals()
  })

  it('only follows http(s) provider URLs', () => {
    expect(safeExternalUrl('https://idp.example/authorize?x=1')).toBe('https://idp.example/authorize?x=1')
    expect(safeExternalUrl('javascript:alert(1)')).toBeNull()
    expect(safeExternalUrl('data:text/html,hi')).toBeNull()
    expect(safeExternalUrl('not a url')).toBeNull()
  })
})

describe('workspace switcher', () => {
  beforeEach(() => {
    localStorage.clear()
    localStorage.setItem('raavi.locale', 'en')
    setActivePinia(createPinia())
    resetRouterBootstrap()
  })

  it('lists my workspaces when I belong to more than one', async () => {
    const { wrapper } = await mountAt('/', {
      'GET /api/v1/host/context': () => json(tenantCtx),
      'GET /api/v1/auth/me': () => json(me),
      'GET /api/v1/auth/me/workspaces': () => json([
        { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, url: 'https://acme.raavi.ir/', isCurrent: true },
        { slug: 'beta', displayName: { fa: 'بتا', en: 'Beta' }, url: 'https://beta.raavi.ir/', isCurrent: false },
      ]),
    })
    expect(wrapper.find('[data-testid="workspace-switcher"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="user-name"]').attributes('href')).toBe('/me')
    vi.unstubAllGlobals()
  })
})
