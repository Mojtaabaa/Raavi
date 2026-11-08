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
import { fakeFetch, json, problem } from './support/fakeApi'

const tenantCtx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
const me = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }
const space = (canCreate: boolean) => ({ id: 's1', key: 'ENG', name: 'Engineering', description: null, type: 'Team', status: 'Active', homePageId: 'home', updatedAt: '2026-11-07T08:00:00Z', createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', revision: 1, requirePublishApproval: false, rights: { canView: true, canCreate, canAdminister: false, canManageTemplates: false } })
const task = (id: string, title: string, over: Record<string, unknown> = {}) => ({
  id, spaceKey: 'ENG', spaceName: 'Engineering', contentId: null, title, notes: null, status: 'Open', assigneeId: 'u1', assigneeName: 'Sara', dueDate: null, isOverdue: false,
  completedAt: null, createdAt: '2026-11-07T08:00:00Z', createdByName: 'Ali', revision: 1, rights: { canEdit: true, canComplete: true, canDelete: true }, ...over,
})

describe('tasks', () => {
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

  it('"My tasks" lists assigned tasks across spaces, flags overdue ones and completes a task', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/tasks/mine': () => json([task('t1', 'Ship it', { dueDate: '2020-01-01', isOverdue: true }), task('t2', 'Write docs', { dueDate: '2099-05-06', spaceKey: 'OPS', spaceName: 'Operations' })]),
      'POST /api/v1/tasks/t1/complete': () => json(task('t1', 'Ship it', { status: 'Done', completedAt: '2026-11-08T08:00:00Z' })),
    })
    const w = await open('/tasks', f)

    const rows = w.findAll('[data-testid="task"]')
    expect(rows).toHaveLength(2)
    expect(rows[0]!.text()).toContain('Ship it')
    expect(rows[0]!.text()).toContain('Overdue')
    expect(rows[1]!.text()).toContain('Operations') // other spaces are named and linked
    expect(w.find('[data-testid="new-task"]').exists()).toBe(false) // tasks are created inside a space
    expect(f.calls.find((c) => c.url.startsWith('/api/v1/tasks/mine'))!.url).toContain('status=Open')

    await rows[0]!.get('input[type="checkbox"]').setValue(true)
    await flushPromises()
    expect(f.calls.some((c) => c.method === 'POST' && c.url === '/api/v1/tasks/t1/complete')).toBe(true)
    expect(w.findAll('[data-testid="task"]')).toHaveLength(1) // the Open filter drops it
  })

  it('creates a task in a space only for members who may edit, and offers the CSV export', async () => {
    const created = task('t9', 'Plan the launch', { assigneeId: null, assigneeName: null, dueDate: '2030-03-04' })
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => new Response(JSON.stringify(space(true)), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"r1"' } }),
      'GET /api/v1/spaces/ENG/tasks': () => json([]),
      'POST /api/v1/spaces/ENG/tasks': () => json(created, 201),
    })
    const w = await open('/spaces/ENG/tasks', f)
    expect(w.get('[data-testid="no-tasks"]').text()).toContain('No tasks in this space.')
    expect(w.get('[data-testid="export-tasks"]').attributes('href')).toBe('/api/v1/spaces/ENG/tasks/export')

    await w.get('[data-testid="new-task"]').trigger('click')
    await flushPromises()
    const title = document.body.querySelector<HTMLInputElement>('[data-testid="task-title"]')!
    title.value = 'Plan the launch'
    title.dispatchEvent(new Event('input'))
    const due = document.body.querySelector<HTMLInputElement>('#task-due')!
    due.value = '2030-03-04'
    due.dispatchEvent(new Event('input'))
    await flushPromises()
    document.body.querySelector<HTMLButtonElement>('[data-testid="save-task"]')!.click()
    for (let i = 0; i < 3; i++) await flushPromises()
    const post = f.calls.find((c) => c.method === 'POST' && c.url === '/api/v1/spaces/ENG/tasks')!
    expect(post.body).toEqual({ title: 'Plan the launch', notes: null, assigneeId: null, dueDate: '2030-03-04', contentId: null })
  })

  it('hides creation for read-only members and explains assignee errors in the user language', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => new Response(JSON.stringify(space(false)), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"r1"' } }),
      'GET /api/v1/spaces/ENG/tasks': () => json([task('t1', 'Read only', { rights: { canEdit: false, canComplete: false, canDelete: false } })]),
    })
    const w = await open('/spaces/ENG/tasks', f)
    expect(w.find('[data-testid="new-task"]').exists()).toBe(false)
    const row = w.get('[data-testid="task"]')
    expect(row.get('input[type="checkbox"]').attributes('disabled')).toBeDefined()
    expect(row.findAll('button').length).toBe(0) // no edit/delete

    const f2 = fakeFetch({ ...base, 'GET /api/v1/tasks/mine': () => problem(500, 'internal') })
    wrapper?.unmount()
    resetRouterBootstrap()
    const w2 = await open('/tasks', f2)
    expect(w2.text()).not.toContain('internal')
  })
})
