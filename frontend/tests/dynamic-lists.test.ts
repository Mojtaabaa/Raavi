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
import { manifests, SCHEMA_VERSION } from '@/modules/editor/registry'
import { fakeFetch, json, problem } from './support/fakeApi'

const tenantCtx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
const me = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }
const space = { id: 's1', key: 'ENG', name: 'Engineering', description: null, type: 'Team', status: 'Active', homePageId: 'home', updatedAt: '2026-11-07T08:00:00Z', createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', revision: 1, requirePublishApproval: false, rights: { canView: true, canCreate: true, canAdminister: false, canManageTemplates: false } }
const rights = { canEdit: false, canCreateChild: false, canMove: false, canArchive: false, canDelete: false, canRestrict: false, canPublish: false, canApprove: false, canComment: false, canModerateComments: false }
const body = {
  schema: 'raavi.doc', schemaVersion: SCHEMA_VERSION,
  doc: { type: 'doc', content: [
    { type: 'children', attrs: { depth: 1 } },
    { type: 'contentByLabel', attrs: { labels: 'howto, runbook', maxItems: 3, currentSpaceOnly: true } },
  ] },
}
const page = {
  id: 'p1', spaceId: 's1', spaceKey: 'ENG', spaceName: 'Engineering', parentId: null, contentType: 'page', title: 'Index', slug: 'index', status: 'Current', body, version: 2, labels: [], breadcrumbs: [],
  isRestricted: false, createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', updatedBy: 'u1', updatedByName: 'Sara', updatedAt: '2026-11-07T08:00:00Z', revision: 2, rights,
  publishedAt: null, hasDraft: false, draftUpdatedAt: null, scheduledPublishAt: null, pendingApproval: false, requirePublishApproval: false,
}
const item = (id: string, title: string, spaceKey = 'ENG') => ({ id, contentType: 'page', title, slug: title.toLowerCase(), spaceId: 's', spaceKey, spaceName: spaceKey, updatedAt: '2026-11-07T08:00:00Z' })

describe('dynamic lists in pages (children, content by label)', () => {
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

  async function show(f: ReturnType<typeof fakeFetch>) {
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/spaces/ENG/pages/p1')
    wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    for (let i = 0; i < 5; i++) await flushPromises()
    await new Promise((r) => setTimeout(r, 100))
    await flushPromises()
    return wrapper
  }

  const base = {
    'GET /api/v1/host/context': () => json(tenantCtx),
    'GET /api/v1/auth/me': () => json(me),
    'GET /api/v1/auth/me/workspaces': () => json([]),
    'GET /api/v1/auth/antiforgery': () => json({ token: 'xsrf' }),
    'GET /api/v1/notifications/unread-count': () => json({ count: 0 }),
    'GET /api/v1/spaces/ENG': () => new Response(JSON.stringify(space), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"r1"' } }),
    'GET /api/v1/content/p1': () => new Response(JSON.stringify(page), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"r2"' } }),
    'GET /api/v1/content/p1/comments': () => json({ threads: [], canComment: false, watching: false, reactions: [] }),
    'GET /api/v1/editor/schema': () => json({ schema: 'raavi.doc', schemaVersion: SCHEMA_VERSION, maxBytes: 1, extensions: manifests.map((m) => ({ id: m.id, name: m.name, version: m.version, kind: m.kind, insertable: true })) }),
    'GET /api/v1/editor/link-rewrites': () => json([{ prefix: 'vianna', baseUrl: 'https://svn.viannacloud.ir' }]),
  }

  it('lists the child pages and the pages with the labels, deduplicated, filtered to the space and capped', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG/tree': () => json([{ id: 'c1', parentId: 'p1', contentType: 'page', title: 'Child one', slug: 'child-one', position: 0, hasChildren: false }, { id: 'c2', parentId: 'p1', contentType: 'page', title: 'Child two', slug: 'child-two', position: 1, hasChildren: false }]),
      'GET /api/v1/labels/howto/content': () => json([item('a', 'Beta'), item('b', 'Alpha'), item('x', 'Elsewhere', 'OPS')]),
      'GET /api/v1/labels/runbook/content': () => json([item('b', 'Alpha'), item('c', 'Gamma'), item('d', 'Delta')]),
    })
    const w = await show(f)

    const children = w.get('[data-testid="children-list"]')
    expect(children.findAll('a').map((a) => a.text())).toEqual(['Child one', 'Child two'])
    expect(children.get('a').attributes('href')).toContain('/spaces/ENG/pages/c1')
    expect(f.calls.some((c) => c.url.startsWith('/api/v1/spaces/ENG/tree') && c.url.includes('parentId=p1'))).toBe(true)

    const byLabel = w.get('[data-testid="by-label-list"]')
    expect(byLabel.text()).toContain('howto, runbook')
    // Sorted by title, duplicate ids collapsed, other spaces excluded (this space only), capped at maxItems = 3.
    expect(byLabel.findAll('a').map((a) => a.text())).toEqual(['Alpha', 'Beta', 'Delta'])
  })

  it('shows empty and error states instead of breaking the page', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG/tree': () => json([]),
      'GET /api/v1/labels/howto/content': () => problem(500, 'internal'),
      'GET /api/v1/labels/runbook/content': () => json([]),
    })
    const w = await show(f)
    expect(w.get('[data-testid="children-list"]').text()).toContain('This page has no child pages.')
    expect(w.get('[data-testid="by-label-list"]').text()).toContain('The list could not be loaded.')
    expect(w.find('[data-testid="document-body"]').exists()).toBe(true)
  })
})
