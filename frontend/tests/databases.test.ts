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
const space = (canCreate: boolean) => ({ id: 's1', key: 'ENG', name: 'Engineering', description: null, type: 'Team', status: 'Active', homePageId: 'home', updatedAt: '2026-11-07T08:00:00Z', createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', revision: 1, requirePublishApproval: false, rights: { canView: true, canCreate, canAdminister: false, canManageTemplates: false } })
const withEtag = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"r1"' } })

const columns = [
  { id: 'cname', name: 'Name', type: 'Text', options: [] },
  { id: 'cpts', name: 'Points', type: 'Number', options: [] },
  { id: 'cdone', name: 'Done', type: 'Checkbox', options: [] },
  { id: 'cstat', name: 'Status', type: 'Select', options: [{ id: 'o1', label: 'Todo' }, { id: 'o2', label: 'Shipped' }] },
]
const database = (canEdit: boolean, over: Record<string, unknown> = {}) => ({
  id: 'd1', spaceKey: 'ENG', spaceName: 'Engineering', name: 'Backlog', description: null, columns, rowCount: 2, createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', updatedAt: '2026-11-07T08:00:00Z', revision: 4, canEdit, ...over,
})
const row = (id: string, values: Record<string, unknown>, revision = 1) => ({ id, databaseId: 'd1', values, createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', updatedAt: '2026-11-07T08:00:00Z', revision })

describe('databases', () => {
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
  }

  async function open(path: string, f: ReturnType<typeof fakeFetch>) {
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push(path)
    wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    for (let i = 0; i < 4; i++) await flushPromises()
    return wrapper
  }

  it('lists the databases of a space and creates one only for members who may create', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => withEtag(space(true)),
      'GET /api/v1/spaces/ENG/databases': () => json([database(true)]),
      'POST /api/v1/spaces/ENG/databases': () => json(database(true, { id: 'd2', name: 'People' }), 201),
      'GET /api/v1/databases/d2': () => json(database(true, { id: 'd2', name: 'People' })),
      'GET /api/v1/databases/d2/rows': () => json({ total: 0, rows: [] }),
    })
    const w = await open('/spaces/ENG/databases', f)
    expect(w.get('[data-testid="database"]').text()).toContain('Backlog')
    expect(w.get('[data-testid="database"]').text()).toContain('4 columns · 2 rows')

    await w.get('[data-testid="new-database"]').trigger('click')
    await flushPromises()
    const name = document.body.querySelector<HTMLInputElement>('[data-testid="database-name"]')!
    name.value = 'People'
    name.dispatchEvent(new Event('input'))
    await flushPromises()
    document.body.querySelector<HTMLButtonElement>('[data-testid="save-database"]')!.click()
    for (let i = 0; i < 40 && !f.calls.some((c) => c.method === 'POST' && c.url === '/api/v1/spaces/ENG/databases'); i++) {
      await new Promise((r) => setTimeout(r, 25))
      await flushPromises()
    }
    const post = f.calls.find((c) => c.method === 'POST' && c.url === '/api/v1/spaces/ENG/databases')!
    expect(post.body).toEqual({ name: 'People', description: null, columns: [{ name: 'Name', type: 'Text' }] })
    for (let i = 0; i < 40 && !f.calls.some((c) => c.url === '/api/v1/databases/d2'); i++) {
      await new Promise((r) => setTimeout(r, 25))
      await flushPromises()
    }
    expect(f.calls.some((c) => c.url === '/api/v1/databases/d2')).toBe(true) // navigated to the new table

    wrapper?.unmount()
    resetRouterBootstrap()
    const g = fakeFetch({ ...base, 'GET /api/v1/spaces/ENG': () => withEtag(space(false)), 'GET /api/v1/spaces/ENG/databases': () => json([]) })
    const w2 = await open('/spaces/ENG/databases', g)
    expect(w2.find('[data-testid="new-database"]').exists()).toBe(false)
    expect(w2.get('[data-testid="no-databases"]').text()).toContain('No databases')
  })

  it('edits cells in place with the row ETag, sorts by header and filters on the server', async () => {
    const rows = [row('r1', { cname: 'Alpha', cpts: 3, cstat: 'o1' }, 2), row('r2', { cname: 'Beta' })]
    const f = fakeFetch({
      ...base,
      'GET /api/v1/databases/d1': () => json(database(true)),
      'GET /api/v1/databases/d1/rows': () => json({ total: 2, rows }),
      'PUT /api/v1/database-rows/r1': () => json(row('r1', { cname: 'Alpha', cpts: 8, cstat: 'o1' }, 3)),
    })
    const w = await open('/databases/d1', f)
    const trs = w.findAll('[data-testid="row"]')
    expect(trs).toHaveLength(2)
    expect((trs[0]!.findAll('input')[0]!.element as HTMLInputElement).value).toBe('Alpha')
    expect((trs[0]!.find('select').element as HTMLSelectElement).value).toBe('o1')

    const points = trs[0]!.get('input[type="number"]')
    ;(points.element as HTMLInputElement).value = '8'
    await points.trigger('change')
    await flushPromises()
    const put = f.calls.find((c) => c.method === 'PUT' && c.url === '/api/v1/database-rows/r1')!
    expect(put.body).toEqual({ values: { cname: 'Alpha', cpts: 8, cstat: 'o1' } })
    expect(put.headers['if-match'] ?? put.headers['If-Match']).toBe('"r2"')

    // Clearing a cell removes it from the row instead of storing an empty string.
    const name = trs[1]!.findAll('input')[0]!
    ;(name.element as HTMLInputElement).value = ''
    await name.trigger('change')
    await flushPromises()
    const puts = f.calls.filter((c) => c.method === 'PUT' && c.url === '/api/v1/database-rows/r2')
    expect(puts[0]!.body).toEqual({ values: {} })

    await w.findAll('[data-testid="sort"]')[1]!.trigger('click')
    await flushPromises()
    expect(f.calls.filter((c) => c.url.startsWith('/api/v1/databases/d1/rows')).at(-1)!.url).toContain('sort=cpts')
    await w.findAll('[data-testid="sort"]')[1]!.trigger('click')
    await flushPromises()
    expect(f.calls.filter((c) => c.url.startsWith('/api/v1/databases/d1/rows')).at(-1)!.url).toContain('dir=desc')
    expect(w.findAll('th')[1]!.attributes('aria-sort')).toBe('descending')

    vi.useFakeTimers()
    const search = w.get('[data-testid="database-search"]')
    ;(search.element as HTMLInputElement).value = 'alp'
    await search.trigger('input')
    await vi.advanceTimersByTimeAsync(300)
    vi.useRealTimers()
    await flushPromises()
    expect(f.calls.filter((c) => c.url.startsWith('/api/v1/databases/d1/rows')).at(-1)!.url).toContain('q=alp')
  })

  it('saves schema changes keeping ids and locking existing types, and is read-only without Edit', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/databases/d1': () => json(database(true)),
      'GET /api/v1/databases/d1/rows': () => json({ total: 0, rows: [] }),
      'PUT /api/v1/databases/d1': () => json(database(true, { revision: 5 })),
    })
    const w = await open('/databases/d1', f)
    expect(w.get('[data-testid="no-rows"]').text()).toContain('No rows yet.')
    await w.get('[data-testid="edit-schema"]').trigger('click')
    await flushPromises()

    const types = [...document.body.querySelectorAll<HTMLSelectElement>('[data-testid="column-type"]')]
    expect(types.every((t) => t.disabled)).toBe(true) // existing columns cannot change type
    const options = document.body.querySelector<HTMLTextAreaElement>('[data-testid="column-options"]')!
    expect(options.value).toBe('Todo\nShipped')
    options.value = 'Todo\nBlocked' // "Shipped" removed, "Blocked" added; "Todo" keeps its id
    options.dispatchEvent(new Event('input'))
    document.body.querySelector<HTMLButtonElement>('[data-testid="add-column"]')!.click()
    await flushPromises()
    const names = document.body.querySelectorAll<HTMLInputElement>('[data-testid="column-name"]')
    const last = names[names.length - 1]!
    last.value = 'Due'
    last.dispatchEvent(new Event('input'))
    const lastType = [...document.body.querySelectorAll<HTMLSelectElement>('[data-testid="column-type"]')].at(-1)!
    expect(lastType.disabled).toBe(false)
    lastType.value = 'Date'
    lastType.dispatchEvent(new Event('change'))
    await flushPromises()
    document.body.querySelector<HTMLButtonElement>('[data-testid="save-schema"]')!.click()
    for (let i = 0; i < 3; i++) await flushPromises()

    const put = f.calls.find((c) => c.method === 'PUT' && c.url === '/api/v1/databases/d1')!
    expect(put.headers['if-match'] ?? put.headers['If-Match']).toBe('"r4"')
    const body = put.body as { columns: { id?: string; name: string; type: string; options?: { id?: string; label: string }[] }[] }
    expect(body.columns.map((c) => c.id)).toEqual(['cname', 'cpts', 'cdone', 'cstat', undefined])
    expect(body.columns[3]!.options).toEqual([{ id: 'o1', label: 'Todo' }, { id: undefined, label: 'Blocked' }])
    expect(body.columns[4]).toMatchObject({ name: 'Due', type: 'Date' })

    wrapper?.unmount()
    resetRouterBootstrap()
    const g = fakeFetch({
      ...base,
      'GET /api/v1/databases/d1': () => json(database(false)),
      'GET /api/v1/databases/d1/rows': () => json({ total: 1, rows: [row('r1', { cname: 'Alpha', cdone: true, cstat: 'o2' })] }),
    })
    const w2 = await open('/databases/d1', g)
    expect(w2.find('[data-testid="add-row"]').exists()).toBe(false)
    expect(w2.find('[data-testid="edit-schema"]').exists()).toBe(false)
    expect(w2.find('[data-testid="row"] input').exists()).toBe(false) // plain text, nothing editable
    expect(w2.get('[data-testid="row"]').text()).toContain('Shipped') // select shows its label
    expect(w2.get('[data-testid="export-database"]').attributes('href')).toBe('/api/v1/databases/d1/export')
  })
})
