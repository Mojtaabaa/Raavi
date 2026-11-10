import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { ref } from 'vue'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createMemoryHistory } from 'vue-router'
import App from '@/app/App.vue'
import { configureHttp } from '@/core/api/http'
import { createRaaviI18n } from '@/core/i18n'
import { createRaaviRouter, resetRouterBootstrap } from '@/router'
import { textToDoc } from '@/modules/wiki/docText'
import { manifests, SCHEMA_VERSION } from '@/modules/editor/registry'
import { fakeFetch, json } from './support/fakeApi'

// The live session needs WebSockets and IndexedDB; here it is replaced by a stub that records how it was started.
const started: { documentName: string; tickets: string[] }[] = []
vi.mock('@/modules/editor/collaboration', async (importOriginal) => {
  const real = await importOriginal<typeof import('@/modules/editor/collaboration')>()
  return {
    ...real,
    startLiveSession: (target: { documentName: string; ticket: () => Promise<string> }) => {
      const entry = { documentName: target.documentName, tickets: [] as string[] }
      started.push(entry)
      void target.ticket().then((t) => entry.tickets.push(t))
      void target.ticket().then((t) => entry.tickets.push(t))
      return { document: null, provider: null, status: ref('live'), peers: ref([{ clientId: 2, name: 'Ali', color: 'red' }]), extensions: [], destroy() {} }
    },
  }
})

const { colorFor, storageKey } = await import('@/modules/editor/collaboration')

const tenantCtx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
const me = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }
const space = { id: 's1', key: 'ENG', name: 'Engineering', description: null, type: 'Team', status: 'Active', homePageId: 'home', updatedAt: '2026-11-07T08:00:00Z', createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', revision: 1, requirePublishApproval: false, rights: { canView: true, canCreate: true, canAdminister: false, canManageTemplates: false } }
const rights = { canEdit: true, canCreateChild: false, canMove: false, canArchive: false, canDelete: false, canRestrict: false, canPublish: true, canApprove: false, canComment: false, canModerateComments: false }
const page = {
  id: 'p1', spaceId: 's1', spaceKey: 'ENG', spaceName: 'Engineering', parentId: null, contentType: 'page', title: 'Runbook', slug: 'runbook', status: 'Current', body: textToDoc('x'), version: 3, labels: [], breadcrumbs: [],
  isRestricted: false, createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', updatedBy: 'u1', updatedByName: 'Sara', updatedAt: '2026-11-07T08:00:00Z', revision: 3, rights,
  publishedAt: null, hasDraft: false, draftUpdatedAt: null, scheduledPublishAt: null, pendingApproval: false, requirePublishApproval: false,
}
const draft = { contentId: 'p1', exists: true, title: 'Runbook', body: page.body, baseVersion: 3, publishedVersion: 3, isStale: false, revision: 7, updatedByName: 'Sara', updatedAt: '2026-11-07T08:00:00Z', contributors: [], scheduledPublishAt: null, scheduledByName: null, pendingRequest: null }
const withETag = (body: unknown, etag: string) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json', ETag: etag } })

describe('live collaboration helpers', () => {
  it('derives stable colours and per-document cache keys', () => {
    expect(colorFor('user-1')).toBe(colorFor('user-1'))
    expect(colorFor('user-1')).not.toBe(colorFor('user-2'))
    expect(storageKey('t1:page:p1:draft')).toBe('raavi-collab:t1:page:p1:draft')
  })
})

describe('page editor in live mode', () => {
  let wrapper: VueWrapper | null = null
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
    resetRouterBootstrap()
    started.length = 0
  })
  afterEach(() => {
    wrapper?.unmount()
    vi.unstubAllGlobals()
  })

  it('joins the live document with fresh tickets, saves only the title, and publishes the latest draft', async () => {
    let ticketNo = 0
    const f = fakeFetch({
      'GET /api/v1/host/context': () => json(tenantCtx),
      'GET /api/v1/auth/me': () => json(me),
      'GET /api/v1/auth/me/workspaces': () => json([]),
      'GET /api/v1/auth/antiforgery': () => json({ token: 'xsrf' }),
      'GET /api/v1/spaces/ENG': () => withETag(space, '"r1"'),
      'GET /api/v1/spaces/ENG/tree': () => json([]),
      'GET /api/v1/content/p1': () => withETag(page, '"r3"'),
      'GET /api/v1/content/p1/draft': () => withETag(draft, '"r7"'),
      'GET /api/v1/collaboration/config': () => json({ enabled: true }),
      'POST /api/v1/collaboration/tickets': () => json({ ticket: `ticket-${++ticketNo}`, documentName: 't1:page:p1:draft', url: 'wss://collab.test', permission: 'edit', expiresAt: '2026-11-07T08:01:00Z' }),
      'PUT /api/v1/content/p1/draft/title': () => json({ ...draft, title: 'Runbook 2' }),
      'POST /api/v1/content/p1/publish': () => withETag({ outcome: 'Published', page: { ...page, version: 4 } }, '"r4"'),
      'GET /api/v1/editor/schema': () => json({ schema: 'raavi.doc', schemaVersion: SCHEMA_VERSION, maxBytes: 1, extensions: manifests.map((m) => ({ id: m.id, name: m.name, version: m.version, kind: m.kind, insertable: true })) }),
      'GET /api/v1/editor/link-rewrites': () => json([{ prefix: 'vianna', baseUrl: 'https://svn.viannacloud.ir' }]),
    })
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/spaces/ENG/pages/p1/edit')
    wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    await flushPromises()
    await flushPromises()

    expect(started).toHaveLength(1)
    expect(started[0]!.documentName).toBe('t1:page:p1:draft')
    await flushPromises()
    expect(started[0]!.tickets).toEqual(['ticket-1', 'ticket-2']) // first connect uses the issued ticket, reconnects fetch new ones
    expect(wrapper.get('[data-testid="live-status"]').text()).toContain('A') // presence of another editor

    await new Promise((r) => setTimeout(r, 400))
    await wrapper.get('#page-title').setValue('Runbook 2')
    await new Promise((r) => setTimeout(r, 1500))
    await flushPromises()
    expect(f.calls.some((c) => c.method === 'PUT' && c.url === '/api/v1/content/p1/draft/title')).toBe(true)
    expect(f.calls.some((c) => c.method === 'PUT' && c.url === '/api/v1/content/p1/draft')).toBe(false) // body belongs to the live document

    await wrapper.get('[data-testid="publish"]').trigger('click')
    await flushPromises()
    document.querySelector<HTMLButtonElement>('[data-testid="confirm-publish"]')!.click()
    await flushPromises()
    const publish = f.calls.find((c) => c.method === 'POST' && c.url === '/api/v1/content/p1/publish')!
    expect(publish.headers['If-Match']).toBe('*')
  })
})
