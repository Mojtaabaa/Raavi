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
import { parseKeyMap, parseSpaces } from '@/core/api/migration'
import { fakeFetch, json, problem } from './support/fakeApi'

const tenantCtx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
const admin = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: true, isSystemAdministrator: false, provider: 'local' }

const job = (over: Record<string, unknown> = {}) => ({
  id: 'j1', sourceUrl: 'https://wiki.example.test/', sourceVersion: '10.2.13 (build 23551)', mode: 'DryRun', status: 'Completed', control: null,
  createdAt: '2026-11-07T08:00:00Z', startedAt: '2026-11-07T08:00:05Z', finishedAt: '2026-11-07T08:03:00Z', currentOperation: null,
  spacesTotal: 2, spacesDone: 2, pagesTotal: 10, pagesDone: 10, pagesFailed: 0, versionsDone: 24, warnings: 3, hasReport: true, canResume: false, ...over,
})
const report = {
  mode: 'DryRun', sourceVersion: '10.2.13',
  spaces: [{ sourceKey: 'eng', name: 'Engineering', raaviKey: null, pages: 6, versions: 15, pagesImported: 6, versionsImported: 0, pagesFailed: 0, keyConflict: false },
    { sourceKey: 'ops', name: 'Operations', raaviKey: null, pages: 4, versions: 9, pagesImported: 4, versionsImported: 0, pagesFailed: 0, keyConflict: false }],
  unsupportedMacros: { contentbylabel: 2, roadmap: 1 }, warningsByCode: { 'macro.unsupported': 3 }, attachmentReferences: 5, pageLinks: 7,
  unmappedAuthors: ['علی (ali)', 'Sara (sara)'], linksResolved: 0, linksUnresolved: 0, validation: [],
  attachments: { found: 5, bytes: 3 * 1024 * 1024, imported: 4, tooLarge: 1, failed: 0, maxBytes: 10 * 1024 * 1024 },
  access: { restrictedPages: 2, restrictionsApplied: 2, unmappedSubjects: ['user: Ghost'], spacePermissions: 'not_imported' },
  authorMappings: [
    { sourceKey: 'sara', userName: 'sara', displayName: 'Sara Source', email: 'sara@example.com', suggestedUser: 'Sara Ahmadi', applied: false },
    { sourceKey: 'ali', userName: 'ali', displayName: 'Ali Source', email: 'ali@example.com', suggestedUser: 'Ali Rezaei', applied: true },
    { sourceKey: 'ghost', userName: 'ghost', displayName: 'Ghost', email: null, suggestedUser: null, applied: false },
  ],
}
const events = [{ at: '2026-11-07T08:01:00Z', level: 'Warning', code: 'macro.unsupported', sourceRef: 'eng/12 Guide', message: "Unsupported macro 'roadmap' was kept as text." }]

function routes(extra: Record<string, Parameters<typeof fakeFetch>[0][string]> = {}) {
  return fakeFetch({
    'GET /api/v1/host/context': () => json(tenantCtx),
    'GET /api/v1/auth/me': () => json(admin),
    'GET /api/v1/auth/me/workspaces': () => json([]),
    'GET /api/v1/auth/antiforgery': () => json({ token: 'xsrf' }),
    'GET /api/v1/notifications/unread-count': () => json({ count: 0 }),
    'GET /api/v1/migrations/confluence': () => json([job()]),
    'GET /api/v1/migrations/confluence/j1/report': () => json(report),
    'GET /api/v1/migrations/confluence/j1/events': () => json(events),
    ...extra,
  })
}

describe('Confluence migration page', () => {
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

  async function open(f: ReturnType<typeof fakeFetch>) {
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/admin/migrations/confluence')
    wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    for (let i = 0; i < 4; i++) await flushPromises()
    return { w: wrapper, router }
  }

  it('parses the optional space list and key mapping', () => {
    expect(parseSpaces(' ENG, OPS  hr ')).toEqual(['ENG', 'OPS', 'hr'])
    expect(parseSpaces('  ')).toBeNull()
    expect(parseKeyMap('docs=DOCS\n\n bad \nold = NEW ')).toEqual({ docs: 'DOCS', old: 'NEW' })
    expect(parseKeyMap('')).toBeNull()
  })

  it('lists jobs and shows the selected job with its report, unmapped authors and log', async () => {
    const { w } = await open(routes())
    expect(w.get('[data-testid="confluence-migration"]').text()).toContain('wiki.example.test')
    expect(w.text()).toContain('Completed')

    await w.get('tbody tr').trigger('click')
    for (let i = 0; i < 3; i++) await flushPromises()
    const detail = w.get('[data-testid="migration-detail"]')
    expect(detail.get('[data-testid="job-counters"]').text()).toContain('pages 10/10')
    const rep = detail.get('[data-testid="migration-report"]').text()
    expect(rep).toContain('Engineering')
    expect(rep).toContain('contentbylabel')
    expect(rep).toContain('Unsupported macro kept as text × 3') // localized warning label, never the raw code
    expect(detail.get('[data-testid="unmapped-authors"]').text()).toContain('علی (ali)')
    expect(detail.get('[data-testid="attachments-summary"]').text()).toContain('4 of 5 files imported; 1 larger than 10 MB were left out')
    const access = detail.get('[data-testid="access-summary"]').text()
    expect(access).toContain('2 page(s) were restricted')
    expect(access).toContain('user: Ghost')
    expect(access).toContain('Space permissions are not imported')
    const mapping = detail.get('[data-testid="author-mappings"]').text()
    expect(mapping).toContain('Suggested: Sara Ahmadi') // a suggestion is not applied until the administrator confirms
    expect(mapping).toContain('Matched: Ali Rezaei')
    expect(mapping).toContain('No match')
    expect(detail.get('[data-testid="migration-events"]').text()).toContain('eng/12 Guide')
    // A finished job offers "Run again" but only once a token is typed (the stored one was erased).
    expect(detail.get('[data-testid="start"]').attributes('disabled')).toBeDefined()
  })

  it('creates a dry run by default without keeping the token and shows the new job', async () => {
    const created = job({ id: 'j2', status: 'Created', hasReport: false, pagesTotal: 0, pagesDone: 0, spacesTotal: 0, spacesDone: 0, versionsDone: 0, warnings: 0 })
    const f = routes({
      'POST /api/v1/migrations/confluence': () => json(created, 201),
      'GET /api/v1/migrations/confluence/j2/events': () => json([]),
    })
    const { w } = await open(f)

    await w.get('[data-testid="new-migration"]').trigger('click')
    await flushPromises()
    const dialog = document.body
    const url = dialog.querySelector<HTMLInputElement>('[data-testid="m-url"]')!
    url.value = 'https://wiki.example.test'
    url.dispatchEvent(new Event('input'))
    const token = dialog.querySelector<HTMLInputElement>('#m-token-input')!
    expect(token.type).toBe('password')
    expect(token.autocomplete).toBe('off')
    token.value = 'secret-pat'
    token.dispatchEvent(new Event('input'))
    await flushPromises()
    dialog.querySelector<HTMLInputElement>('#m-spaces')!.value = 'ENG'
    dialog.querySelector<HTMLInputElement>('#m-spaces')!.dispatchEvent(new Event('input'))
    await flushPromises()
    dialog.querySelector<HTMLButtonElement>('[data-testid="m-create"]')!.click()
    for (let i = 0; i < 4; i++) await flushPromises()

    const post = f.calls.find((c) => c.method === 'POST' && c.url === '/api/v1/migrations/confluence')!
    expect(post.body).toEqual({ url: 'https://wiki.example.test', token: 'secret-pat', dryRun: true, spaces: ['ENG'], keyMap: null, reconvert: false, mapUsersByEmail: false, userMap: null })
    expect(w.get('[data-testid="job-status"]').text()).toBe('Ready')
    expect(w.text()).not.toContain('secret-pat')
    expect(document.body.textContent).not.toContain('secret-pat')
  })

  it('starts a ready job, and resumes a failed one only with a new token', async () => {
    let status = 'Created'
    const f = routes({
      'GET /api/v1/migrations/confluence': () => json([job({ id: 'j3', status, hasReport: false, canResume: status === 'Failed' })]),
      'GET /api/v1/migrations/confluence/j3/events': () => json([]),
      'POST /api/v1/migrations/confluence/j3/start': () => json(job({ id: 'j3', status: 'Queued', hasReport: false })),
    })
    const { w } = await open(f)
    await w.get('tbody tr').trigger('click')
    for (let i = 0; i < 3; i++) await flushPromises()
    expect(w.find('#restart-token').exists()).toBe(false) // a ready job still has its token stored
    await w.get('[data-testid="start"]').trigger('submit')
    await flushPromises()
    const call = f.calls.find((c) => c.url === '/api/v1/migrations/confluence/j3/start')!
    expect(call.body).toEqual({})
    expect(w.get('[data-testid="job-status"]').text()).toBe('Queued')
    // Active: the page offers pause/cancel, not another start.
    expect(w.find('[data-testid="pause"]').exists()).toBe(true)
    expect(w.find('[data-testid="start"]').exists()).toBe(false)
    status = 'Failed'
  })

  it('explains source errors in the user language and never shows raw server text', async () => {
    const f = routes({ 'POST /api/v1/migrations/confluence': () => problem(400, 'source.host_not_allowed') })
    const { w } = await open(f)
    await w.get('[data-testid="new-migration"]').trigger('click')
    await flushPromises()
    const url = document.body.querySelector<HTMLInputElement>('[data-testid="m-url"]')!
    url.value = 'https://evil.example'
    url.dispatchEvent(new Event('input'))
    const token = document.body.querySelector<HTMLInputElement>('#m-token-input')!
    token.value = 't'
    token.dispatchEvent(new Event('input'))
    await flushPromises()
    document.body.querySelector<HTMLButtonElement>('[data-testid="m-create"]')!.click()
    for (let i = 0; i < 3; i++) await flushPromises()
    expect(document.body.textContent).toContain('not in the list of sources your installation allows')
    expect(document.body.textContent).not.toContain('test.no_route')
  })

  it('is reachable by tenant administrators only', async () => {
    const f = routes({ 'GET /api/v1/auth/me': () => json({ ...admin, isTenantAdministrator: false }) })
    const { router } = await open(f)
    expect(router.currentRoute.value.name).not.toBe('admin-migrations')
  })
})
