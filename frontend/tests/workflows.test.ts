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
const me = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }
const space = (canAdminister: boolean) => ({ id: 's1', key: 'ENG', name: 'Engineering', description: null, type: 'Team', status: 'Active', homePageId: 'home', updatedAt: '2026-11-07T08:00:00Z', createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', revision: 1, requirePublishApproval: false, rights: { canView: true, canCreate: true, canAdminister, canManageTemplates: false } })
const withEtag = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"r1"' } })

const workflow = (over: Record<string, unknown> = {}) => ({
  id: 'w1', spaceKey: 'ENG', spaceName: 'Engineering', name: 'RFC intake', enabled: true, trigger: 'content.created', staleDays: null,
  conditions: [{ field: 'Title', operator: 'StartsWith', value: 'rfc:' }],
  actions: [{ type: 'AddLabel', label: 'needs-review', recipients: null, message: null }, { type: 'Notify', label: null, recipients: 'Actor', message: 'Received' }],
  createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', updatedAt: '2026-11-07T08:00:00Z', revision: 3, ...over,
})
const catalog = { triggers: ['content.created', 'content.published', 'content.stale'] }

describe('workflows', () => {
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
    'GET /api/v1/auth/me': () => json(me),
    'GET /api/v1/auth/me/workspaces': () => json([]),
    'GET /api/v1/auth/antiforgery': () => json({ token: 'xsrf' }),
    'GET /api/v1/notifications/unread-count': () => json({ count: 0 }),
    'GET /api/v1/spaces/ENG/tree': () => json([]),
    'GET /api/v1/workflows/catalog': () => json(catalog),
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

  const settle = async () => {
    for (let i = 0; i < 6; i++) {
      await new Promise((r) => setTimeout(r, 30))
      await flushPromises()
    }
  }

  it('shows an explanation, and never asks for the rules, to people who cannot administer the space', async () => {
    const f = fakeFetch({ ...base, 'GET /api/v1/spaces/ENG': () => withEtag(space(false)) })
    const w = await open('/spaces/ENG/workflows', f)
    expect(w.get('[data-testid="not-admin"]').text()).toContain('Only space administrators')
    expect(w.find('[data-testid="new-workflow"]').exists()).toBe(false)
    expect(f.calls.some((c) => c.url === '/api/v1/spaces/ENG/workflows')).toBe(false)
    expect(w.find('[data-testid="space-workflows-link"]').exists()).toBe(false)
  })

  it('lists rules, creates one from the form, switches one off with its ETag and shows the run history', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => withEtag(space(true)),
      'GET /api/v1/spaces/ENG/workflows': () => json([workflow()]),
      'POST /api/v1/spaces/ENG/workflows': () => json(workflow({ id: 'w2', name: 'Label RFCs' }), 201),
      'PUT /api/v1/workflows/w1': () => json(workflow({ enabled: false, revision: 4 })),
      'GET /api/v1/workflows/w1/runs': () =>
        json([
          { id: 'r1', eventId: 'e1', eventType: 'content.created', contentId: 'c1', contentTitle: 'RFC: Billing', status: 'Succeeded', message: "label 'needs-review' added", attempts: 1, startedAt: '2026-11-08T08:00:00Z', finishedAt: '2026-11-08T08:00:01Z' },
          { id: 'r2', eventId: 'e2', eventType: 'content.created', contentId: 'c2', contentTitle: 'Notes', status: 'Skipped', message: 'conditions_not_met', attempts: 1, startedAt: '2026-11-08T08:01:00Z', finishedAt: '2026-11-08T08:01:00Z' },
        ]),
    })
    const w = await open('/spaces/ENG/workflows', f)
    const row = w.get('[data-testid="workflow"]')
    expect(row.text()).toContain('RFC intake')
    expect(row.text()).toContain('a page is created')
    expect(row.text()).toContain('1 conditions · 2 actions')

    // Switch off: the whole definition goes back with the revision from the list.
    await w.get('[data-testid="toggle-workflow"]').trigger('click')
    await settle()
    const put = f.calls.find((c) => c.method === 'PUT' && c.url === '/api/v1/workflows/w1')!
    expect(put.headers['if-match'] ?? put.headers['If-Match']).toBe('"r3"')
    expect(put.body).toMatchObject({ name: 'RFC intake', enabled: false, trigger: 'content.created' })

    // Run history: reasons are translated, counts and states shown.
    await w.get('[data-testid="show-runs"]').trigger('click')
    await settle()
    const runs = document.body.querySelectorAll('[data-testid="run"]')
    expect(runs).toHaveLength(2)
    expect(runs[0]!.textContent).toContain('RFC: Billing')
    expect(runs[1]!.textContent).toContain('Conditions not met')
    document.body.querySelector<HTMLButtonElement>('.p-dialog-close-button')?.click()
    await settle()

    // Create: needs a name and a valid action before Save is enabled; label with a space is refused client-side.
    await w.get('[data-testid="new-workflow"]').trigger('click')
    await settle()
    const q = <T extends HTMLElement>(sel: string) => document.body.querySelector<T>(sel)!
    const save = q<HTMLButtonElement>('[data-testid="save-workflow"]')
    expect(save.disabled).toBe(true)
    const set = async (el: HTMLInputElement, v: string) => {
      el.value = v
      el.dispatchEvent(new Event('input'))
      await flushPromises()
    }
    await set(q<HTMLInputElement>('[data-testid="wf-name"]'), 'Label RFCs')
    await set(q<HTMLInputElement>('[data-testid="wf-action-label"]'), 'has space')
    expect(q<HTMLButtonElement>('[data-testid="save-workflow"]').disabled).toBe(true)
    await set(q<HTMLInputElement>('[data-testid="wf-action-label"]'), 'rfc')
    q<HTMLButtonElement>('[data-testid="add-condition"]').click()
    await flushPromises()
    await set(q<HTMLInputElement>('[data-testid="wf-condition-value"]'), 'RFC:')
    expect(q<HTMLButtonElement>('[data-testid="save-workflow"]').disabled).toBe(false)
    q<HTMLButtonElement>('[data-testid="save-workflow"]').click()
    await settle()
    const post = f.calls.find((c) => c.method === 'POST' && c.url === '/api/v1/spaces/ENG/workflows')!
    expect(post.body).toEqual({
      name: 'Label RFCs',
      enabled: true,
      trigger: 'content.created',
      staleDays: null,
      conditions: [{ field: 'Title', operator: 'Contains', value: 'RFC:' }],
      actions: [{ type: 'AddLabel', label: 'rfc', recipients: null, message: null }],
    })
  })

  it('draws the rule as a flow, and the stale trigger asks for a day count', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => withEtag(space(true)),
      'GET /api/v1/spaces/ENG/workflows': () => json([workflow({ trigger: 'content.stale', staleDays: 45 })]),
      'POST /api/v1/spaces/ENG/workflows': () => json(workflow({ id: 'w3' }), 201),
    })
    const w = await open('/spaces/ENG/workflows', f)
    expect(w.get('[data-testid="workflow"]').text()).toContain('not been updated for 45 days')
    expect(w.find('[data-testid="flow"]').exists()).toBe(false)
    await w.get('[data-testid="toggle-flow"]').trigger('click')
    expect(w.get('[data-testid="flow-trigger"]').text()).toContain('45 days')
    expect(w.findAll('[data-testid="flow-condition"]').map((c) => c.text())).toEqual(['Title starts with “rfc:”'])
    expect(w.findAll('[data-testid="flow-action"]').map((a) => a.text())).toEqual(['Add label: needs-review', 'Send notification (Whoever triggered it): Received'])

    await w.get('[data-testid="new-workflow"]').trigger('click')
    await settle()
    const q = <T extends HTMLElement>(sel: string) => document.body.querySelector<T>(sel)!
    const set = async (el: HTMLInputElement, v: string) => {
      el.value = v
      el.dispatchEvent(new Event('input'))
      await flushPromises()
    }
    await set(q<HTMLInputElement>('[data-testid="wf-name"]'), 'Stale pages')
    await set(q<HTMLInputElement>('[data-testid="wf-action-label"]'), 'stale')
    const trigger = q<HTMLSelectElement>('[data-testid="wf-trigger"]')
    trigger.value = 'content.stale'
    trigger.dispatchEvent(new Event('change'))
    await settle()
    const days = q<HTMLInputElement>('[data-testid="wf-stale-days"]')
    expect(days.value).toBe('90') // sensible default
    expect([...document.body.querySelectorAll('[data-testid="flow-trigger"]')].at(-1)!.textContent).toContain('90 days') // the preview follows the form
    await set(days, '0')
    expect(q<HTMLButtonElement>('[data-testid="save-workflow"]').disabled).toBe(true)
    await set(days, '60')
    q<HTMLButtonElement>('[data-testid="save-workflow"]').click()
    await settle()
    expect(f.calls.find((c) => c.method === 'POST')!.body).toMatchObject({ trigger: 'content.stale', staleDays: 60 })
  })
})
