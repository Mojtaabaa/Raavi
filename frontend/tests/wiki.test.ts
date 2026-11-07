import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createMemoryHistory } from 'vue-router'
import App from '@/app/App.vue'
import { apiRequest, configureHttp } from '@/core/api/http'
import { createRaaviI18n } from '@/core/i18n'
import { createRaaviRouter, resetRouterBootstrap } from '@/router'
import type { Page } from '@/core/api/wiki'
import DocumentBody from '@/modules/wiki/components/DocumentBody.vue'
import { docToText, textToDoc } from '@/modules/wiki/docText'
import RaaviEditor from '@/modules/editor/components/RaaviEditor.vue'
import { manifests, SCHEMA_VERSION } from '@/modules/editor/registry'
import type { Editor } from '@tiptap/core'
import { fakeFetch, json, problem, type Handler } from './support/fakeApi'

const tenantCtx = {
  kind: 'tenant',
  tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } },
}
const me = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }
const space = {
  id: 's1', key: 'ENG', name: 'Engineering', description: null, type: 'Team', status: 'Active', homePageId: 'home', updatedAt: '2026-11-07T08:00:00Z',
  createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', revision: 1,
  rights: { canView: true, canCreate: true, canAdminister: false, canManageTemplates: false },
}
const rights = { canEdit: false, canCreateChild: false, canMove: false, canArchive: false, canDelete: false, canRestrict: false, canPublish: false, canApprove: false, canComment: false, canModerateComments: false }
const page: Page = {
  id: 'p1', spaceId: 's1', spaceKey: 'ENG', spaceName: 'Engineering', parentId: 'a', contentType: 'page', title: 'Runbook', slug: 'runbook', status: 'Current',
  body: textToDoc('<script>alert(1)</script>\nSecond line'), version: 3, labels: ['ops'], breadcrumbs: [{ id: 'a', title: 'Operations', contentType: 'page' }],
  isRestricted: true, createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', updatedBy: 'u1', updatedByName: 'Sara', updatedAt: '2026-11-07T08:00:00Z',
  revision: 3, rights, publishedAt: '2026-11-07T08:00:00Z', hasDraft: false, draftUpdatedAt: null, scheduledPublishAt: null, pendingApproval: false, requirePublishApproval: false,
}

function withETag(body: unknown, etag: string): Response {
  return new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json', ETag: etag } })
}

const mounted: VueWrapper[] = []

async function mountAt(path: string, routes: Record<string, Handler>) {
  const f = fakeFetch({
    'GET /api/v1/host/context': () => json(tenantCtx),
    'GET /api/v1/auth/me': () => json(me),
    'GET /api/v1/auth/me/workspaces': () => json([]),
    'GET /api/v1/auth/antiforgery': () => json({ token: 'xsrf' }),
    'GET /api/v1/spaces/ENG': () => withETag(space, '"r1"'),
    'GET /api/v1/spaces/ENG/tree': () => json([]),
    ...routes,
  })
  vi.stubGlobal('fetch', f.fn)
  configureHttp(f.fn)
  const router = createRaaviRouter(createMemoryHistory())
  await router.push(path)
  const wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
  mounted.push(wrapper)
  await flushPromises()
  await flushPromises()
  return { wrapper, calls: f.calls, router }
}

describe('document text conversion', () => {
  it('maps lines to paragraphs and back', () => {
    const doc = textToDoc('first\r\n\nthird')
    expect(doc.schema).toBe('raavi.doc')
    expect(doc.doc.content!).toHaveLength(3)
    expect(doc.doc.content![1]).toEqual({ type: 'paragraph' })
    expect(docToText(doc)).toBe('first\n\nthird')
    expect(textToDoc('\n\n').doc.content).toEqual([])
  })

  it('renders paragraphs as text with their own direction (no HTML)', async () => {
    const wrapper = mount(DocumentBody, { global: { plugins: [createRaaviI18n('en'), PrimeVue, ToastService] }, props: { doc: { ...textToDoc('<img src=x onerror=alert(1)>'), doc: { type: 'doc', content: [{ type: 'paragraph', attrs: { dir: 'rtl' }, content: [{ type: 'text', text: '<b>سلام</b>' }] }] } } } })
    await flushPromises()
    expect(wrapper.find('b').exists()).toBe(false)
    expect(wrapper.text()).toContain('<b>سلام</b>')
    expect(wrapper.get('p').attributes('dir')).toBe('rtl')
    wrapper.unmount()
  })
})

describe('conditional requests', () => {
  it('sends If-Match and returns the new ETag', async () => {
    const f = fakeFetch({
      'GET /api/v1/auth/antiforgery': () => json({ token: 't' }),
      'PUT /api/v1/content/p1': () => withETag({ ok: true }, '"r4"'),
    })
    configureHttp(f.fn)
    const r = await apiRequest('PUT', '/content/p1', { title: 'x' }, { ifMatch: '"r3"' })
    expect(r.etag).toBe('"r4"')
    expect(f.calls.find((c) => c.method === 'PUT')!.headers['If-Match']).toBe('"r3"')
  })
})

describe('wiki views', () => {
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
    resetRouterBootstrap()
  })

  afterEach(() => {
    while (mounted.length) mounted.pop()!.unmount()
    vi.unstubAllGlobals()
  })

  it('shows a page with breadcrumbs, labels and restriction marker, without edit actions it may not use', async () => {
    const { wrapper } = await mountAt('/spaces/ENG/pages/p1/runbook', { 'GET /api/v1/content/p1': () => withETag(page, '"r3"') })
    expect(wrapper.get('h1').text()).toContain('Runbook')
    expect(wrapper.text()).toContain('Operations')
    expect(wrapper.text()).toContain('Restricted')
    expect(wrapper.text()).toContain('ops')
    const body = wrapper.get('[data-testid="document-body"]')
    expect(body.text()).toContain('<script>alert(1)</script>')
    expect(body.find('script').exists()).toBe(false)
    expect(wrapper.find('[data-testid="edit-page"]').exists()).toBe(false)
    vi.unstubAllGlobals()
  })

  it('shows not-found for pages the user cannot see', async () => {
    const { wrapper } = await mountAt('/spaces/ENG/pages/zzz', { 'GET /api/v1/content/zzz': () => problem(404, 'content.not_found') })
    expect(wrapper.text()).toContain('Page not found')
    vi.unstubAllGlobals()
  })

  it('autosaves the shared draft with If-Match and reports a concurrent change instead of overwriting it', async () => {
    const editable = { ...page, rights: { ...rights, canEdit: true, canPublish: true } }
    const draft = { contentId: 'p1', exists: true, title: 'Runbook', body: page.body, baseVersion: 3, publishedVersion: 3, isStale: false, revision: 2, updatedByName: 'Sara', updatedAt: '2026-11-07T08:00:00Z', contributors: ['Sara'], scheduledPublishAt: null, scheduledByName: null, pendingRequest: null }
    const { wrapper, calls } = await mountAt('/spaces/ENG/pages/p1/edit', {
      'GET /api/v1/content/p1': () => withETag(editable, '"r3"'),
      'GET /api/v1/content/p1/draft': () => withETag(draft, '"r2"'),
      'PUT /api/v1/content/p1/draft': () => problem(412, 'precondition.failed'),
      'GET /api/v1/editor/schema': () => json({ schema: 'raavi.doc', schemaVersion: SCHEMA_VERSION, maxBytes: 5242880, extensions: manifests.map((m) => ({ id: m.id, name: m.name, version: m.version, kind: m.kind, insertable: true })) }),
    })
    await new Promise((r) => setTimeout(r, 400)) // the editor's initial normalisation is not a user change
    const editor = wrapper.findComponent(RaaviEditor).vm.editor as Editor
    expect(editor.isEditable).toBe(true)
    editor.commands.setContent({ type: 'doc', content: [{ type: 'paragraph', content: [{ type: 'text', text: 'changed text' }] }] }, { emitUpdate: true })
    await new Promise((r) => setTimeout(r, 1700)) // autosave debounce
    await flushPromises()
    const put = calls.find((c) => c.method === 'PUT')!
    expect(put.url).toBe('/api/v1/content/p1/draft')
    expect(put.headers['If-Match']).toBe('"r2"')
    expect((put.body as { body: { doc: { content: unknown[] } } }).body.doc.content).toEqual([{ type: 'paragraph', attrs: { textAlign: null, dir: null }, content: [{ type: 'text', text: 'changed text' }] }])
    expect(wrapper.find('[data-testid="edit-conflict"]').exists()).toBe(true)
    expect(wrapper.get('[data-testid="publish"]').attributes('disabled')).toBeDefined()
    expect(calls.some((c) => c.method === 'PUT' && c.url === '/api/v1/content/p1')).toBe(false) // never overwrites the published page
    vi.unstubAllGlobals()
  })

  it('shows history with a word-level comparison and pending draft notice', async () => {
    const withDraft = { ...page, hasDraft: true, draftUpdatedAt: '2026-11-07T09:00:00Z', rights: { ...rights, canEdit: true, canPublish: true } }
    const versions = [
      { version: 3, title: 'Runbook', changeSummary: 'fix', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', type: 'Restore', restoredFrom: 1, contributors: ['Sara', 'Ali'] },
      { version: 2, title: 'Runbook', changeSummary: null, createdByName: 'Sara', createdAt: '2026-11-06T08:00:00Z', type: 'Published', restoredFrom: null, contributors: ['Sara'] },
    ]
    const diff = { from: { label: 'version', version: 2, title: 'Runbook' }, to: { label: 'version', version: 3, title: 'Runbook' }, titleChanged: false,
      blocks: [{ op: 'Change', type: 'paragraph', before: 'Keep backups weekly', after: 'Keep backups daily', segments: [{ op: 'Equal', text: 'Keep backups ' }, { op: 'Delete', text: 'weekly' }, { op: 'Insert', text: 'daily' }] }] }
    const { wrapper, calls } = await mountAt('/spaces/ENG/pages/p1/history', {
      'GET /api/v1/content/p1': () => withETag(withDraft, '"r3"'),
      'GET /api/v1/content/p1/versions': () => json(versions),
      'GET /api/v1/content/p1/diff': () => json(diff),
    })
    expect(calls.some((c) => c.url === '/api/v1/content/p1/diff?from=2&to=3')).toBe(true)
    expect(wrapper.get('del').text()).toBe('weekly')
    expect(wrapper.get('ins').text()).toBe('daily')
    expect(wrapper.text()).toContain('Restored from v1')
    expect(wrapper.text()).toContain('Sara، Ali')
    vi.unstubAllGlobals()
  })

  it('offers space creation only when the API allows it', async () => {
    const list = [{ id: 's1', key: 'ENG', name: 'Engineering', description: null, type: 'Team', status: 'Active', homePageId: null, updatedAt: '2026-11-07T08:00:00Z' }]
    const denied = await mountAt('/spaces', { 'GET /api/v1/spaces-capabilities': () => json({ canCreateSpaces: false }), 'GET /api/v1/spaces': () => json(list) })
    expect(denied.wrapper.text()).toContain('Engineering')
    expect(denied.wrapper.find('[data-testid="create-space"]').exists()).toBe(false)
    while (mounted.length) mounted.pop()!.unmount()
    vi.unstubAllGlobals()

    setActivePinia(createPinia())
    resetRouterBootstrap()
    const allowed = await mountAt('/spaces', { 'GET /api/v1/spaces-capabilities': () => json({ canCreateSpaces: true }), 'GET /api/v1/spaces': () => json(list) })
    expect(allowed.wrapper.find('[data-testid="create-space"]').exists()).toBe(true)
    vi.unstubAllGlobals()
  })

  it('opens quick find with Ctrl+K and lists permission-filtered results', async () => {
    const { wrapper, calls } = await mountAt('/spaces', {
      'GET /api/v1/spaces-capabilities': () => json({ canCreateSpaces: false }),
      'GET /api/v1/spaces': () => json([]),
      'GET /api/v1/quick-find': () => json({ spaces: [], content: [{ id: 'p1', contentType: 'page', title: 'Runbook', slug: 'runbook', spaceId: 's1', spaceKey: 'ENG', spaceName: 'Engineering', updatedAt: '2026-11-07T08:00:00Z' }] }),
    })
    expect(wrapper.find('[data-testid="quick-find"]').exists()).toBe(true)
    window.dispatchEvent(new KeyboardEvent('keydown', { key: 'k', ctrlKey: true }))
    await flushPromises()
    await new Promise((r) => setTimeout(r, 50))
    const input = document.querySelector<HTMLInputElement>('#quick-find')!
    expect(input).not.toBeNull()
    input.value = 'run'
    input.dispatchEvent(new Event('input'))
    await new Promise((r) => setTimeout(r, 350))
    await flushPromises()
    expect(calls.some((c) => c.url.startsWith('/api/v1/quick-find?q=run'))).toBe(true)
    expect(document.body.textContent).toContain('Runbook')
    vi.unstubAllGlobals()
  })
})
