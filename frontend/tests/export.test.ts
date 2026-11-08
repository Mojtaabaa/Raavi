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
import { fakeFetch, json } from './support/fakeApi'

const tenantCtx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
const admin = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: true, isSystemAdministrator: false, provider: 'local' }

describe('data export page', () => {
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

  async function open(extra: Record<string, () => Response>) {
    const f = fakeFetch({
      'GET /api/v1/host/context': () => json(tenantCtx),
      'GET /api/v1/auth/me': () => json(admin),
      'GET /api/v1/auth/me/workspaces': () => json([]),
      'GET /api/v1/auth/antiforgery': () => json({ token: 'x' }),
      'GET /api/v1/notifications/unread-count': () => json({ count: 0 }),
      ...extra,
    })
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/admin/export')
    wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    for (let i = 0; i < 5; i++) {
      await new Promise((r) => setTimeout(r, 30))
      await flushPromises()
    }
    return { w: wrapper, f }
  }

  it('states what the export contains and downloads the archive under the server-suggested name', async () => {
    URL.createObjectURL = vi.fn(() => 'blob:x')
    URL.revokeObjectURL = vi.fn()
    const click = vi.spyOn(HTMLAnchorElement.prototype, 'click').mockImplementation(() => undefined)
    const { w, f } = await open({ 'GET /api/v1/tenant/export': () => new Response('PK', { status: 200, headers: { 'Content-Type': 'application/zip', 'Content-Disposition': 'attachment; filename=raavi-export-acme-20261108-1200.zip' } }) })
    expect(w.get('[data-testid="export"]').text()).toContain('Pages you cannot view')
    expect(w.get('[data-testid="export"]').text()).toContain('what you, as the signed-in administrator, may view')
    await w.get('[data-testid="export-download"]').trigger('click')
    await flushPromises()
    expect(f.calls.some((c) => c.url === '/api/v1/tenant/export')).toBe(true)
    expect(click).toHaveBeenCalledTimes(1)
    click.mockRestore()
  })

  it('tells the administrator when an export is already running or fails', async () => {
    const { w } = await open({ 'GET /api/v1/tenant/export': () => json({ code: 'export.in_progress' }, 409) })
    await w.get('[data-testid="export-download"]').trigger('click')
    await flushPromises()
    expect(w.get('[data-testid="export-error"]').text()).toContain('already running')
  })
})
