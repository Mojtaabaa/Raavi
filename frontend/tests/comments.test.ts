import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import { getSchema } from '@tiptap/core'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createMemoryHistory } from 'vue-router'
import App from '@/app/App.vue'
import { configureHttp } from '@/core/api/http'
import { createRaaviI18n } from '@/core/i18n'
import { createRaaviRouter, resetRouterBootstrap } from '@/router'
import { textToDoc } from '@/modules/wiki/docText'
import { headlessExtensions } from '@/modules/editor/setup'
import { manifests, SCHEMA_VERSION } from '@/modules/editor/registry'
import { flatten, locate, quoteRange } from '@/modules/editor/extensions/commentAnchors'
import { fakeFetch, json } from './support/fakeApi'

const pushEvent = (globalThis as unknown as { pushEvent: (name: string) => void }).pushEvent

describe('inline comment anchors (text-quote selectors)', () => {
  const schema = getSchema(headlessExtensions())
  const doc = schema.nodeFromJSON({
    type: 'doc',
    content: [
      { type: 'paragraph', content: [{ type: 'text', text: 'Restart the queue. Then restart the queue again.' }] },
      { type: 'paragraph', content: [{ type: 'text', text: 'سرویس صف را ' }, { type: 'mention', attrs: { id: 'u1', label: 'Ali' } }, { type: 'text', text: ' بررسی کند' }] },
    ],
  })
  const flat = flatten(doc)

  it('flattens blocks with separators and keeps positions for every character', () => {
    expect(flat.text.split('\n')).toHaveLength(2)
    expect(flat.text).toContain('￼') // the mention
    expect(flat.positions).toHaveLength(flat.text.length)
    expect(doc.textBetween(flat.positions[0]!, flat.positions[6]! + 1)).toBe('Restart')
  })

  it('round-trips a selection and picks the occurrence that matches the context', () => {
    const second = flat.text.lastIndexOf('restart the queue')
    const from = flat.positions[second]!
    const to = flat.positions[second + 'restart the queue'.length - 1]! + 1
    const quote = quoteRange(flat, from, to)!
    expect(quote.exact).toBe('restart the queue')
    expect(quote.prefix.endsWith('Then ')).toBe(true)
    expect(quote.suffix.startsWith(' again')).toBe(true)
    expect(locate(flat, quote)).toEqual({ from, to })

    // Same words, different context: the first occurrence wins.
    const first = locate(flat, { exact: 'the queue', prefix: 'Restart ', suffix: '. Then' })!
    expect(doc.textBetween(first.from, first.to)).toBe('the queue')
    expect(first.from).toBe(flat.positions[flat.text.indexOf('the queue')])
  })

  it('reports text that is no longer on the page as orphaned and trims whitespace', () => {
    expect(locate(flat, { exact: 'deleted sentence', prefix: '', suffix: '' })).toBeNull()
    const persian = flat.text.indexOf('سرویس')
    const q = quoteRange(flat, flat.positions[persian]! - 1, flat.positions[persian + 4]! + 1)!
    expect(q.exact).toBe('سرویس')
  })
})

const tenantCtx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
const me = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }
const space = { id: 's1', key: 'ENG', name: 'Engineering', description: null, type: 'Team', status: 'Active', homePageId: 'home', updatedAt: '2026-11-07T08:00:00Z', createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', revision: 1, requirePublishApproval: false, rights: { canView: true, canCreate: true, canAdminister: false, canManageTemplates: false } }
const rights = { canEdit: true, canCreateChild: false, canMove: false, canArchive: false, canDelete: false, canRestrict: false, canPublish: true, canApprove: false, canComment: true, canModerateComments: false }
const page = {
  id: 'p1', spaceId: 's1', spaceKey: 'ENG', spaceName: 'Engineering', parentId: null, contentType: 'page', title: 'Runbook', slug: 'runbook', status: 'Current',
  body: textToDoc('Restart the queue before deploying'), version: 3, labels: [], breadcrumbs: [],
  isRestricted: false, createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', updatedBy: 'u1', updatedByName: 'Sara', updatedAt: '2026-11-07T08:00:00Z', revision: 3, rights,
  publishedAt: null, hasDraft: false, draftUpdatedAt: null, scheduledPublishAt: null, pendingApproval: false, requirePublishApproval: false,
}
const comment = (id: string, threadId: string, text: string, extra: Record<string, unknown> = {}) => ({
  id, threadId, parentId: id === threadId ? null : threadId, body: textToDoc(text), isDeleted: false, authorId: 'u2', authorName: 'Ali',
  createdAt: '2026-11-07T08:00:00Z', editedAt: null, pageVersion: 3, reactions: [], revision: 1, rights: { canEdit: false, canDelete: false }, ...extra,
})
const threads = [
  { id: 't1', kind: 'Inline', anchor: { exact: 'the queue', prefix: 'Restart ', suffix: ' before' }, status: 'Open', resolvedByName: null, resolvedAt: null, canResolve: true, canReply: true, comments: [comment('t1', 't1', 'Which queue?')] },
  { id: 't2', kind: 'Inline', anchor: { exact: 'a sentence that was removed', prefix: '', suffix: '' }, status: 'Open', resolvedByName: null, resolvedAt: null, canResolve: true, canReply: true, comments: [comment('t2', 't2', 'Outdated?')] },
]
const notification = (id: string, readAt: string | null) => ({
  id, kind: 'mention', spaceId: 's1', spaceKey: 'ENG', contentId: 'p1', commentId: 't1', actorId: 'u2', actorName: 'Ali', title: 'Runbook', excerpt: 'Which queue?', createdAt: '2026-11-07T08:00:00Z', readAt,
})

function routes(extra: Record<string, Parameters<typeof fakeFetch>[0][string]> = {}) {
  return fakeFetch({
    'GET /api/v1/host/context': () => json(tenantCtx),
    'GET /api/v1/auth/me': () => json(me),
    'GET /api/v1/auth/me/workspaces': () => json([]),
    'GET /api/v1/auth/antiforgery': () => json({ token: 'xsrf' }),
    'GET /api/v1/spaces/ENG': () => new Response(JSON.stringify(space), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"r1"' } }),
    'GET /api/v1/spaces/ENG/tree': () => json([]),
    'GET /api/v1/content/p1': () => new Response(JSON.stringify(page), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"r3"' } }),
    'GET /api/v1/content/p1/comments': () => json({ threads, canComment: true, watching: false, reactions: ['👍', '🎉'] }),
    'GET /api/v1/notifications/unread-count': () => json({ count: 0 }),
    'GET /api/v1/editor/schema': () => json({ schema: 'raavi.doc', schemaVersion: SCHEMA_VERSION, maxBytes: 1, extensions: manifests.map((m) => ({ id: m.id, name: m.name, version: m.version, kind: m.kind, insertable: true })) }),
    'GET /api/v1/editor/link-rewrites': () => json([{ prefix: 'vianna', baseUrl: 'https://svn.viannacloud.ir' }]),
    ...extra,
  })
}

describe('page discussion', () => {
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

  async function mountAt(path: string, f: ReturnType<typeof fakeFetch>) {
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push(path)
    wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    for (let i = 0; i < 4; i++) await flushPromises()
    await new Promise((r) => setTimeout(r, 50))
    await flushPromises()
    return { wrapper, router }
  }

  it('highlights inline anchors, flags orphaned threads, posts comments and toggles watching', async () => {
    const created = { id: 't3', kind: 'Page', anchor: null, status: 'Open', resolvedByName: null, resolvedAt: null, canResolve: true, canReply: true, comments: [comment('t3', 't3', 'Looks good', { authorName: 'Sara' })] }
    const f = routes({
      'POST /api/v1/content/p1/comments': () => json(created, 201),
      'PUT /api/v1/content/p1/watch': () => json({ watching: true }),
      'POST /api/v1/comments/t1/reactions': () => json([{ emoji: '🎉', count: 1, mine: true }]),
    })
    const { wrapper: w } = await mountAt('/spaces/ENG/pages/p1', f)

    expect(w.get('#comments-heading').text()).toContain('Comments (2)')
    const marks = document.querySelectorAll('[data-testid="document-body"] .raavi-comment-anchor')
    expect(marks).toHaveLength(1)
    expect(marks[0]!.textContent).toBe('the queue')
    expect(marks[0]!.getAttribute('data-thread-id')).toBe('t1')
    const orphan = w.get('[data-thread="t2"]')
    expect(orphan.text()).toContain('Text changed')
    expect(w.get('[data-thread="t1"]').text()).not.toContain('Text changed')

    // Clicking the highlight focuses its thread.
    ;(marks[0] as HTMLElement).dispatchEvent(new MouseEvent('click', { bubbles: true }))
    await flushPromises()
    expect(w.get('[data-thread="t1"]').classes()).toContain('active')

    // Reactions from the allow-list.
    const t1 = w.get('[data-thread="t1"]')
    await t1.get('[aria-label="Add reaction"]').trigger('click')
    await new Promise((r) => setTimeout(r, 50))
    await flushPromises()
    // (jsdom selectors do not match non-BMP characters in attribute values)
    ;[...document.querySelectorAll<HTMLButtonElement>('.p-popover button[aria-label]')].find((b) => b.getAttribute('aria-label') === '🎉')!.click()
    await flushPromises()
    expect(f.calls.find((c) => c.url === '/api/v1/comments/t1/reactions')!.body).toEqual({ emoji: '🎉', on: true })
    expect(w.get('[data-thread="t1"]').text()).toContain('🎉 1')

    // Page comment.
    const composer = w.get('.comments-panel > .new [data-testid="comment-editor"] .ProseMirror')
    composer.element.innerHTML = '<p>Looks good</p>'
    await new Promise((r) => setTimeout(r, 50))
    await flushPromises()
    await w.get('.comments-panel > .new [data-testid="comment-submit"]').trigger('submit')
    await flushPromises()
    const post = f.calls.find((c) => c.method === 'POST' && c.url === '/api/v1/content/p1/comments')!
    expect(post.body).toMatchObject({ kind: 'Page', anchor: null })
    expect(JSON.stringify(post.body)).toContain('Looks good')
    expect(w.get('#comments-heading').text()).toContain('Comments (3)')

    // Commenting made the user a watcher; the button reflects it. Unwatched → watch again.
    expect(w.get('[data-testid="watch"]').text()).toContain('Watching')
  })

  it('starts an inline comment from a text selection', async () => {
    const f = routes({ 'POST /api/v1/content/p1/comments': () => json({ ...threads[0], id: 't9' }, 201) })
    const { wrapper: w } = await mountAt('/spaces/ENG/pages/p1', f)

    const body = document.querySelector('[data-testid="document-body"] .ProseMirror p')!
    const text = body.lastChild!.nodeType === Node.TEXT_NODE ? body.lastChild! : body.firstChild!
    const content = text.textContent!
    const start = content.indexOf('before')
    const range = document.createRange()
    range.setStart(text, start)
    range.setEnd(text, start + 'before deploying'.length)
    const sel = window.getSelection()!
    sel.removeAllRanges()
    sel.addRange(range)
    body.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }))
    await flushPromises()

    await w.get('[data-testid="comment-selection"]').trigger('click')
    await flushPromises()
    const inline = w.get('[data-testid="inline-composer"]')
    expect(inline.get('blockquote').text()).toBe('before deploying')
    inline.get('.ProseMirror').element.innerHTML = '<p>Before which deploy?</p>'
    await new Promise((r) => setTimeout(r, 50))
    await flushPromises()
    await inline.get('form').trigger('submit')
    await flushPromises()
    const post = f.calls.find((c) => c.method === 'POST' && c.url === '/api/v1/content/p1/comments')!
    expect(post.body).toMatchObject({ kind: 'Inline', anchor: { exact: 'before deploying', suffix: '' } })
    expect((post.body as { anchor: { prefix: string } }).anchor.prefix.endsWith('the queue ')).toBe(true)
  })

  it('shows the unread badge, refreshes on push and opens the comment from the inbox', async () => {
    let count = 1
    const f = routes({
      'GET /api/v1/notifications/unread-count': () => json({ count }),
      'GET /api/v1/notifications': () => json({ items: [notification('n1', null), { ...notification('n2', '2026-11-07T09:00:00Z'), kind: 'page.published' }], unreadCount: count }),
      'POST /api/v1/notifications/n1/read': () => {
        count = 0
        return new Response(null, { status: 204 })
      },
      'POST /api/v1/notifications/read-all': () => new Response(null, { status: 204 }),
    })
    const { wrapper: w, router } = await mountAt('/spaces/ENG', f)
    expect(w.get('[data-testid="notifications-count"]').text()).toBe('1')
    expect(w.get('[data-testid="notifications-bell"]').attributes('aria-label')).toBe('Notifications, 1 unread')

    // A push from the server ("your inbox changed") refreshes the badge.
    count = 4
    pushEvent('notifications')
    await flushPromises()
    expect(w.get('[data-testid="notifications-count"]').text()).toBe('4')

    await w.get('[data-testid="notifications-bell"]').trigger('click')
    await flushPromises()
    const items = document.querySelectorAll<HTMLButtonElement>('[data-testid="notification"]')
    expect(items).toHaveLength(2)
    expect(items[0]!.textContent).toContain('Ali mentioned you on “Runbook”')
    expect(items[0]!.classList.contains('unread')).toBe(true)
    expect(items[1]!.textContent).toContain('Ali published “Runbook”')

    items[0]!.click()
    for (let i = 0; i < 4; i++) await flushPromises()
    expect(f.calls.some((c) => c.method === 'POST' && c.url === '/api/v1/notifications/n1/read')).toBe(true)
    expect(router.currentRoute.value.name).toBe('page')
    expect(router.currentRoute.value.hash).toBe('#comment-t1')
  })
})
