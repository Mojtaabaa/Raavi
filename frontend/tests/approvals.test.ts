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

const approval = (over: Record<string, unknown> = {}) => ({
  id: 'a1', contentId: 'c1', contentTitle: 'Security policy', spaceKey: 'ENG', note: 'Ready for sign-off', mode: 'All', status: 'Pending', versionNumber: 2, currentVersionNumber: 2, pageChanged: false,
  requestedBy: 'u9', requestedByName: 'Omid', requestedAt: '2026-11-08T08:00:00Z', dueAt: null, overdue: false, decidedAt: null,
  approvers: [{ userId: 'u1', name: 'Sara', decision: 'Pending', comment: null, decidedAt: null }, { userId: 'u2', name: 'Reza', decision: 'Approved', comment: 'fine', decidedAt: '2026-11-08T09:00:00Z' }],
  canDecide: true, canCancel: false, ...over,
})

describe('approvals', () => {
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

  it('shows the inbox, approves with one click and removes the decided request', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/approvals': () => json([approval()]),
      'POST /api/v1/approvals/a1/decision': () => json(approval({ status: 'Approved', canDecide: false, approvers: [{ userId: 'u1', name: 'Sara', decision: 'Approved', comment: null, decidedAt: '2026-11-08T10:00:00Z' }] })),
    })
    const w = await open('/approvals', f)
    expect(f.calls.some((c) => c.url === '/api/v1/approvals?scope=inbox')).toBe(true)
    const card = w.get('[data-testid="approval"]')
    expect(card.text()).toContain('Security policy')
    expect(card.text()).toContain('Requested by Omid')
    expect(card.text()).toContain('Everyone must approve')
    expect(card.findAll('[data-testid="approver"]')).toHaveLength(2)
    expect(card.text()).toContain('fine') // another approver's comment

    await w.get('[data-testid="approve"]').trigger('click')
    await flushPromises()
    const post = f.calls.find((c) => c.method === 'POST' && c.url === '/api/v1/approvals/a1/decision')!
    expect(post.body).toEqual({ decision: 'Approved', comment: '' })
    expect(w.find('[data-testid="approval"]').exists()).toBe(false)
    expect(w.get('[data-testid="no-items"]').text()).toContain('Nothing is waiting')
  })

  it('needs a comment to reject, and shows overdue, changed-page and the requester tab', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/approvals': () => json([approval({ dueAt: '2026-11-01T00:00:00Z', overdue: true, pageChanged: true })]),
      'POST /api/v1/approvals/a1/decision': () => json(approval({ status: 'Rejected', canDecide: false })),
    })
    const w = await open('/approvals', f)
    expect(w.get('[data-testid="approval-due"]').text()).toContain('overdue')
    expect(w.get('[data-testid="page-changed"]').text()).toContain('page changed')

    await w.get('[data-testid="start-reject"]').trigger('click')
    expect((w.get('[data-testid="confirm-reject"]').element as HTMLButtonElement).disabled).toBe(true)
    const comment = w.get('[data-testid="reject-comment"]')
    ;(comment.element as HTMLTextAreaElement).value = 'Needs a risk section'
    await comment.trigger('input')
    expect((w.get('[data-testid="confirm-reject"]').element as HTMLButtonElement).disabled).toBe(false)
    await w.get('[data-testid="confirm-reject"]').trigger('click')
    await flushPromises()
    expect(f.calls.find((c) => c.method === 'POST')!.body).toEqual({ decision: 'Rejected', comment: 'Needs a risk section' })

    await w.get('[data-testid="tab-requested"]').trigger('click')
    await flushPromises()
    expect(f.calls.some((c) => c.url === '/api/v1/approvals?scope=requested')).toBe(true)
  })

  it('on a page, editors can ask for approval (not while one is pending) and requesters can cancel', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/content/c1/approvals': () => json([approval({ canDecide: false, canCancel: true, requestedBy: 'u1', requestedByName: 'Sara' })]),
      'POST /api/v1/approvals/a1/cancel': () => json(approval({ status: 'Cancelled', canDecide: false, canCancel: false })),
    })
    // The panel is mounted on its own: the full page view needs a lot of unrelated endpoints.
    const { default: PageApprovals } = await import('@/modules/approvals/PageApprovals.vue')
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    wrapper = mount(PageApprovals, { props: { contentId: 'c1', canRequest: true }, attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    await flushPromises()
    expect(wrapper.findAll('[data-testid="approval"]')).toHaveLength(1)
    expect((wrapper.get('[data-testid="request-approval"]').element as HTMLButtonElement).disabled).toBe(true) // one is pending

    await wrapper.get('[data-testid="cancel-approval"]').trigger('click')
    await flushPromises()
    expect(wrapper.get('[data-testid="approval-status"]').text()).toBe('Cancelled')
    expect((wrapper.get('[data-testid="request-approval"]').element as HTMLButtonElement).disabled).toBe(false)

    await wrapper.get('[data-testid="request-approval"]').trigger('click')
    await flushPromises()
    expect((document.body.querySelector('[data-testid="send-approval"]') as HTMLButtonElement).disabled).toBe(true) // no approver chosen yet
  })

  it('hides the panel from readers when there is nothing to show', async () => {
    const f = fakeFetch({ ...base, 'GET /api/v1/content/c1/approvals': () => json([]) })
    const { default: PageApprovals } = await import('@/modules/approvals/PageApprovals.vue')
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    wrapper = mount(PageApprovals, { props: { contentId: 'c1', canRequest: false }, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    await flushPromises()
    expect(wrapper.find('[data-testid="page-approvals"]').exists()).toBe(false)
  })
})
