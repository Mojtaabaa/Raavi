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

const connector = (over: Record<string, unknown> = {}) => ({
  id: 'c1', name: 'Company GitHub', kind: 'GitHub', baseUrl: 'https://git.example.test/api/v3', hasToken: true, enabled: true, createdAt: '2026-11-07T08:00:00Z', revision: 2, ...over,
})
const catalog = (configured = true) => ({ kinds: ['GitHub', 'GitLab', 'Jira'], hostPolicyConfigured: configured })

describe('connectors', () => {
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

  async function open(f: ReturnType<typeof fakeFetch>) {
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/admin/connectors')
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
  const set = async (sel: string, v: string) => {
    const el = q<HTMLInputElement>(sel)
    el.value = v
    el.dispatchEvent(new Event('input'))
    await flushPromises()
  }

  it('lists connectors without any token, creates one and sends the token once', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/integrations/connectors/catalog': () => json(catalog()),
      'GET /api/v1/integrations/connectors': () => json([connector()]),
      'POST /api/v1/integrations/connectors': () => json(connector({ id: 'c2', name: 'GitLab', kind: 'GitLab' }), 201),
    })
    const w = await open(f)
    const row = w.get('[data-testid="connector"]')
    expect(row.text()).toContain('Company GitHub')
    expect(row.text()).toContain('token set')
    expect(row.text()).not.toContain('ghp_')

    await w.get('[data-testid="new-connector"]').trigger('click')
    await settle()
    expect(q<HTMLButtonElement>('[data-testid="save-connector"]').disabled).toBe(true)
    await set('[data-testid="cn-name"]', 'GitLab')
    const kind = q<HTMLSelectElement>('[data-testid="cn-kind"]')
    kind.value = 'GitLab'
    kind.dispatchEvent(new Event('change'))
    await set('[data-testid="cn-url"]', 'https://git.example.test/gitlab')
    await set('[data-testid="cn-token"]', 'glpat-secret')
    expect(q<HTMLInputElement>('[data-testid="cn-token"]').type).toBe('password')
    q<HTMLButtonElement>('[data-testid="save-connector"]').click()
    await settle()
    expect(f.calls.find((c) => c.method === 'POST')!.body).toEqual({ name: 'GitLab', kind: 'GitLab', baseUrl: 'https://git.example.test/gitlab', token: 'glpat-secret', enabled: true })
  })

  it('keeps the stored token on edit unless asked to remove it', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/integrations/connectors/catalog': () => json(catalog()),
      'GET /api/v1/integrations/connectors': () => json([connector()]),
      'PUT /api/v1/integrations/connectors/c1': () => json(connector({ revision: 3 })),
    })
    const w = await open(f)
    await w.get('[data-testid="edit-connector"]').trigger('click')
    await settle()
    expect(q<HTMLInputElement>('[data-testid="cn-token"]').value).toBe('') // the token is never shown
    q<HTMLButtonElement>('[data-testid="save-connector"]').click()
    await settle()
    const keep = f.calls.find((c) => c.method === 'PUT')!
    expect(JSON.stringify(keep.body)).not.toContain('token') // omitted -> the server keeps it

    await w.get('[data-testid="edit-connector"]').trigger('click')
    await settle()
    q<HTMLInputElement>('[data-testid="cn-remove-token"]').click()
    await settle()
    q<HTMLButtonElement>('[data-testid="save-connector"]').click()
    await settle()
    expect((f.calls.filter((c) => c.method === 'PUT')[1]!.body as { token: string }).token).toBe('')
  })

  it('checks a reference through a connector and shows title, status and a safe link', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/integrations/connectors/catalog': () => json(catalog()),
      'GET /api/v1/integrations/connectors': () => json([connector(), connector({ id: 'c2', name: 'Off', enabled: false })]),
      'GET /api/v1/integrations/issues/lookup': () => json({ reference: 'acme/wiki#42', title: 'Fix the login', status: 'open', url: 'https://github.com/acme/wiki/issues/42', type: 'issue' }),
    })
    const w = await open(f)
    expect(w.get('[data-testid="connector-off"]').text()).toBe('switched off')
    expect(q<HTMLSelectElement>('[data-testid="check-connector"]').options).toHaveLength(1) // only enabled ones
    await set('[data-testid="check-reference"]', 'acme/wiki#42')
    q<HTMLButtonElement>('[data-testid="check-run"]').click()
    await settle()
    expect(f.calls.some((c) => c.url === '/api/v1/integrations/issues/lookup?connector=c1&reference=acme%2Fwiki%2342')).toBe(true)
    const res = w.get('[data-testid="check-result"]')
    expect(res.text()).toContain('Fix the login')
    expect(res.text()).toContain('Issue · open')
    const link = w.get('[data-testid="check-link"]')
    expect(link.attributes('href')).toBe('https://github.com/acme/wiki/issues/42')
    expect(link.attributes('rel')).toContain('noopener')
  })

  it('reaches the administration pages through one menu that closes on Escape and after navigating', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/integrations/connectors/catalog': () => json(catalog()),
      'GET /api/v1/integrations/connectors': () => json([]),
    })
    const w = await open(f)
    const links = () => [...document.body.querySelectorAll('[data-testid="admin-list"] a')].map((a) => a.getAttribute('href'))
    expect(links()).toEqual([]) // closed by default: nine admin links no longer crowd the header
    const toggle = w.get('[data-testid="admin-menu"]')
    expect(toggle.attributes('aria-expanded')).toBe('false')
    await toggle.trigger('click')
    expect(toggle.attributes('aria-expanded')).toBe('true')
    expect(links()).toEqual(expect.arrayContaining(['/admin/members', '/admin/connectors', '/admin/webhooks', '/admin/migrations/confluence']))
    await w.get('[data-testid="admin-list"]').trigger('keydown', { key: 'Escape' })
    expect(links()).toEqual([])
  })

  it('explains that nothing can be created until the installation allows a connector host', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/integrations/connectors/catalog': () => json(catalog(false)),
      'GET /api/v1/integrations/connectors': () => json([]),
    })
    const w = await open(f)
    expect(w.get('[data-testid="no-hosts"]').text()).toContain('AllowedConnectorHosts')
    expect(w.get('[data-testid="new-connector"]').attributes('disabled')).toBeDefined()
    expect(w.find('[data-testid="check-run"]').exists()).toBe(false)
  })
})
