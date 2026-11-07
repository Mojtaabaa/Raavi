import { beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import { createMemoryHistory } from 'vue-router'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createRaaviRouter, resetRouterBootstrap } from '@/router'
import { configureHttp } from '@/core/api/http'
import App from '@/app/App.vue'
import { createRaaviI18n } from '@/core/i18n'
import { fetchHostContext, type TenantPublicContext } from '@/core/api/hostContext'
import { applyBranding, isHexColor } from '@/core/theme/branding'
import { useLocaleStore } from '@/stores/locale'

const tenant: TenantPublicContext = {
  slug: 'acme',
  displayName: { fa: 'شرکت اکمی', en: 'Acme Corp' },
  defaultLocale: 'en',
  timeZone: 'Europe/Berlin',
  calendar: 'gregorian',
  branding: { primaryColor: '#0f766e', secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: '<img src=x onerror=alert(1)>Welcome' },
}

function json(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } })
}

describe('fetchHostContext', () => {
  it('maps tenant, system, not-found and failures', async () => {
    expect(await fetchHostContext(async () => json({ kind: 'tenant', tenant }))).toEqual({ kind: 'tenant', tenant })
    expect(await fetchHostContext(async () => json({ kind: 'system', tenant: null }))).toEqual({ kind: 'system' })
    expect(await fetchHostContext(async () => json({ code: 'tenant.unavailable' }, 404))).toEqual({ kind: 'not-found' })
    expect(await fetchHostContext(async () => json({}, 500))).toEqual({ kind: 'unavailable' })
    expect(await fetchHostContext(async () => { throw new TypeError('offline') })).toEqual({ kind: 'unavailable' })
  })
})

describe('branding', () => {
  it('accepts only #rrggbb colors', () => {
    expect(isHexColor('#0F766E')).toBe(true)
    expect(isHexColor('red')).toBe(false)
    expect(isHexColor('#fff')).toBe(false)
    expect(isHexColor('#0f766e;background:url(x)')).toBe(false)
  })

  it('applies a generated palette for a valid color and ignores invalid ones', () => {
    const update = vi.fn()
    expect(applyBranding({ ...tenant.branding, primaryColor: 'expression(alert(1))' }, update)).toBe(false)
    expect(update).not.toHaveBeenCalled()
    expect(applyBranding(tenant.branding, update)).toBe(true)
    const palette = update.mock.calls[0]![0] as Record<string, string>
    expect(Object.keys(palette)).toEqual(expect.arrayContaining(['50', '500', '950']))
  })
})

describe('tenant default locale', () => {
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
  })

  it('applies the tenant default when the user has not chosen a language', () => {
    const store = useLocaleStore()
    expect(store.applyTenantDefault('en')).toBe('en')
    expect(document.documentElement.dir).toBe('ltr')
  })

  it('keeps the user choice over the tenant default', () => {
    localStorage.setItem('raavi.locale', 'fa')
    setActivePinia(createPinia())
    expect(useLocaleStore().applyTenantDefault('en')).toBe('fa')
  })
})

describe('App bootstrap', () => {
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
    resetRouterBootstrap()
  })

  async function mountApp(hostResponse: Response, meResponse: Response = json({ status: 401, code: 'auth.required' }, 401), path = '/') {
    const fetchFn = vi.fn(async (input: RequestInfo | URL) => {
      const url = String(input)
      if (url.startsWith('/api/v1/host/context')) return hostResponse.clone()
      if (url.startsWith('/api/v1/auth/me')) return meResponse.clone()
      if (url.startsWith('/api/v1/auth/providers')) return json([])
      return json({}, 404)
    }) as unknown as typeof fetch
    vi.stubGlobal('fetch', fetchFn)
    configureHttp(fetchFn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push(path)
    const wrapper = mount(App, { global: { plugins: [createRaaviI18n('fa'), PrimeVue, router, ToastService, ConfirmationService] } })
    await flushPromises()
    await flushPromises()
    return wrapper
  }

  const signedIn = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: null, hostKind: 'tenant', isTenantAdministrator: true, isSystemAdministrator: false, provider: 'local' }

  it('shows the workspace-not-found page for unknown hosts', async () => {
    const wrapper = await mountApp(json({ code: 'tenant.unavailable' }, 404))
    expect(wrapper.find('[data-testid="workspace-not-found"]').exists()).toBe(true)
    vi.unstubAllGlobals()
  })

  it('shows the tenant name in its default language and renders the welcome message as text', async () => {
    const wrapper = await mountApp(json({ kind: 'tenant', tenant }), json(signedIn))
    expect(wrapper.get('[data-testid="brand-name"]').text()).toBe('Acme Corp')
    const welcome = wrapper.get('[data-testid="welcome"]')
    expect(welcome.text()).toContain('<img src=x onerror=alert(1)>Welcome')
    expect(welcome.find('img').exists()).toBe(false)
    expect(wrapper.get('[data-testid="user-name"]').text()).toBe('Sara')
    vi.unstubAllGlobals()
  })

  it('sends anonymous visitors of a tenant to the sign-in page', async () => {
    const wrapper = await mountApp(json({ kind: 'tenant', tenant }))
    expect(wrapper.text()).toContain('Sign in to Acme Corp')
    vi.unstubAllGlobals()
  })

  it('marks the system host', async () => {
    const wrapper = await mountApp(json({ kind: 'system', tenant: null }), undefined, '/system')
    expect(wrapper.find('[data-testid="system-badge"]').exists()).toBe(true)
    vi.unstubAllGlobals()
  })
})
