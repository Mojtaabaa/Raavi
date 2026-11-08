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

const hook = (over: Record<string, unknown> = {}) => ({
  id: 'w1', name: 'ERP sync', url: 'https://hooks.example.test/in', events: ['content.created', 'content.published'], spaceId: null, enabled: true, consecutiveFailures: 0, disabledReason: null, lastSuccessAt: '2026-11-08T08:00:00Z', createdAt: '2026-11-07T08:00:00Z', revision: 3, ...over,
})
const catalog = (configured = true) => ({ events: ['space.created', 'content.created', 'content.published', 'comment.created', 'approval.requested'], hostPolicyConfigured: configured })

describe('webhooks', () => {
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
    'GET /api/v1/spaces': () => json([{ id: 's1', key: 'ENG', name: 'Engineering', description: null, type: 'Team', status: 'Active', homePageId: null, updatedAt: '2026-11-07T08:00:00Z' }]),
  }

  async function open(f: ReturnType<typeof fakeFetch>) {
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/admin/webhooks')
    wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    await settle()
    return wrapper
  }

  const settle = async () => {
    for (let i = 0; i < 6; i++) {
      await new Promise((r) => setTimeout(r, 30))
      await flushPromises()
    }
  }
  const q = <T extends HTMLElement>(sel: string) => document.body.querySelector<T>(sel)!

  it('lists webhooks, creates one with events and shows the secret exactly once', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/integrations/webhooks/catalog': () => json(catalog()),
      'GET /api/v1/integrations/webhooks': () => json([hook()]),
      'POST /api/v1/integrations/webhooks': () => json({ webhook: hook({ id: 'w2', name: 'Chat' }), secret: 'whsec_abc123' }, 201),
    })
    const w = await open(f)
    const row = w.get('[data-testid="webhook"]')
    expect(row.text()).toContain('ERP sync')
    expect(row.text()).toContain('https://hooks.example.test/in')
    expect(row.text()).toContain('All spaces')
    expect(row.text()).toContain('2 events')
    expect(w.find('[data-testid="secret"]').exists()).toBe(false) // never shown for existing webhooks

    await w.get('[data-testid="new-webhook"]').trigger('click')
    await settle()
    expect(q<HTMLButtonElement>('[data-testid="save-webhook"]').disabled).toBe(true)
    const set = async (sel: string, v: string) => {
      const el = q<HTMLInputElement>(sel)
      el.value = v
      el.dispatchEvent(new Event('input'))
      await flushPromises()
    }
    await set('[data-testid="wh-name"]', 'Chat')
    await set('[data-testid="wh-url"]', 'https://hooks.example.test/chat')
    q<HTMLInputElement>('[data-testid="ev-content.published"]').click()
    q<HTMLInputElement>('[data-testid="ev-comment.created"]').click()
    const space = q<HTMLSelectElement>('[data-testid="wh-space"]')
    space.value = 's1'
    space.dispatchEvent(new Event('change'))
    await settle()
    expect(q<HTMLButtonElement>('[data-testid="save-webhook"]').disabled).toBe(false)
    q<HTMLButtonElement>('[data-testid="save-webhook"]').click()
    await settle()
    expect(f.calls.find((c) => c.method === 'POST')!.body).toEqual({ name: 'Chat', url: 'https://hooks.example.test/chat', events: ['content.published', 'comment.created'], spaceId: 's1', enabled: true })
    expect(q('[data-testid="secret"]').textContent).toBe('whsec_abc123')
    q<HTMLButtonElement>('[data-testid="secret-done"]').click()
    await settle()
    expect(document.body.querySelector('[data-testid="secret"]')).toBeNull() // gone for good
  })

  it('tests, rotates, reads the delivery log and warns about switched-off webhooks', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/integrations/webhooks/catalog': () => json(catalog()),
      'GET /api/v1/integrations/webhooks': () => json([hook({ enabled: false, disabledReason: 'repeated_failures', consecutiveFailures: 20 })]),
      'POST /api/v1/integrations/webhooks/w1/test': () => json({ success: false, httpStatus: 500, error: 'http_5xx', durationMs: 30 }),
      'POST /api/v1/integrations/webhooks/w1/rotate-secret': () => json({ webhook: hook({ revision: 4 }), secret: 'whsec_new' }),
      'GET /api/v1/integrations/webhooks/w1/deliveries': () =>
        json([
          { id: 'd1', eventId: 'e1', eventType: 'content.published', status: 'Succeeded', httpStatus: 200, attempts: 1, durationMs: 20, error: null, at: '2026-11-08T08:00:00Z' },
          { id: 'd2', eventId: 'e2', eventType: 'content.created', status: 'Failed', httpStatus: 500, attempts: 3, durationMs: 40, error: 'http_5xx', at: '2026-11-08T08:05:00Z' },
        ]),
    })
    const w = await open(f)
    expect(w.get('[data-testid="webhook-off"]').text()).toContain('Switched off after repeated failures')

    await w.get('[data-testid="test-webhook"]').trigger('click')
    await settle()
    expect(f.calls.some((c) => c.url === '/api/v1/integrations/webhooks/w1/test')).toBe(true)
    expect(document.body.textContent).toContain('The test failed: http_5xx.')

    await w.get('[data-testid="log-webhook"]').trigger('click')
    await settle()
    const rows = [...document.body.querySelectorAll('[data-testid="delivery"]')].map((r) => r.textContent ?? '')
    expect(rows).toHaveLength(2)
    expect(rows[0]).toContain('Delivered')
    expect(rows[1]).toContain('Failed')
    expect(rows[1]).toContain('3')
    q<HTMLButtonElement>('.p-dialog-close-button').click()
    await settle()

    await w.get('[data-testid="rotate-webhook"]').trigger('click')
    await settle()
    expect(q('[data-testid="secret"]').textContent).toBe('whsec_new')
  })

  it('explains that nothing can be created until the installation allows a webhook host', async () => {
    const f = fakeFetch({ ...base, 'GET /api/v1/integrations/webhooks/catalog': () => json(catalog(false)), 'GET /api/v1/integrations/webhooks': () => json([]) })
    const w = await open(f)
    expect(w.get('[data-testid="no-hosts"]').text()).toContain('has not allowed any webhook host')
    expect((w.get('[data-testid="new-webhook"]').element as HTMLButtonElement).disabled).toBe(true)
    expect(w.find('[data-testid="no-webhooks"]').exists()).toBe(true)
  })
})
