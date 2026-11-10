import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import SystemUsersView from '@/modules/tenants/views/SystemUsersView.vue'
import SpacesView from '@/modules/wiki/views/SpacesView.vue'
import WorkerStatus from '@/app/WorkerStatus.vue'
import { createRaaviI18n } from '@/core/i18n'
import { configureHttp } from '@/core/api/http'
import { useAuthStore } from '@/stores/auth'
import { useHostStore } from '@/stores/host'
import { createRaaviRouter } from '@/router'
import { createMemoryHistory } from 'vue-router'
import { fakeFetch, json, type Handler } from './support/fakeApi'

const settle = async () => {
  for (let i = 0; i < 4; i++) {
    await new Promise((r) => setTimeout(r, 20))
    await flushPromises()
  }
}
const plugins = () => [createRaaviI18n('en'), PrimeVue, ToastService]
const user = (id: string, email: string, workspaces: number, admin = false) => ({ id, displayName: email.split('@')[0], email, status: 'Active', workspaces, isSystemAdministrator: admin, createdAt: '2026-01-01T00:00:00Z', lastLoginAt: null })

describe('operations (ADR-051)', () => {
  beforeEach(() => {
    localStorage.setItem('raavi.locale', 'en')
    setActivePinia(createPinia())
  })
  afterEach(() => vi.unstubAllGlobals())

  function install(routes: Record<string, Handler>) {
    const f = fakeFetch({ 'GET /api/v1/auth/antiforgery': () => json({ token: 'x' }), ...routes })
    configureHttp(f.fn)
    vi.stubGlobal('fetch', f.fn)
    return f
  }

  it('lists accounts with their workspace counts and deletes every account without a workspace in one go', async () => {
    const page = { items: [user('1', 'ali@x.io', 2), user('2', 'orphan@x.io', 0), user('3', 'root@x.io', 0, true)], total: 3, skip: 0, take: 200, withoutWorkspace: 1 }
    const f = install({
      'GET /api/v1/system/users': () => json(page),
      'POST /api/v1/system/users/delete': () => json({ deleted: 1, skipped: 0 }),
    })
    const w = mount(SystemUsersView, { attachTo: document.body, global: { plugins: plugins() } })
    await settle()
    expect(document.body.querySelector('[data-testid="ws-ali@x.io"]')!.textContent).toBe('2')
    expect(document.body.querySelector('[data-testid="ws-orphan@x.io"]')!.textContent).toBe('0')
    expect(document.body.textContent).toContain('System administrator') // never offered for deletion
    expect(document.body.querySelector<HTMLButtonElement>('[data-testid="delete-selected"]')!.disabled).toBe(true) // nothing selected yet
    const all = document.body.querySelector<HTMLButtonElement>('[data-testid="delete-all"]')!
    expect(all.textContent).toContain('(1)')
    all.click()
    await settle()
    document.body.querySelector<HTMLButtonElement>('[data-testid="users-confirm-ok"]')!.click()
    await settle()
    expect(f.calls.find((c) => c.url.endsWith('/users/delete'))!.body).toEqual({ allWithoutWorkspace: true })
    w.unmount()
  })

  it('asks the server for only the accounts without a workspace when the filter is on', async () => {
    const f = install({ 'GET /api/v1/system/users': () => json({ items: [], total: 0, skip: 0, take: 200, withoutWorkspace: 0 }) })
    const w = mount(SystemUsersView, { attachTo: document.body, global: { plugins: plugins() } })
    await settle()
    const box = document.body.querySelector<HTMLInputElement>('[data-testid="only-without"] input, input#only-without')!
    box.click()
    await settle()
    expect(f.calls.some((c) => c.url.includes('withoutWorkspace=true'))).toBe(true)
    w.unmount()
  })

  async function workerPage(me: Record<string, unknown>, kind: 'tenant' | 'system', state: string) {
    const f = install({ 'GET /api/v1/tenant/workers': () => json({ state, checkedAt: '2026-11-10T10:00:00Z', lastHeartbeat: state === 'Running' ? '2026-11-10T09:59:40Z' : null, servers: [], jobs: { Enqueued: 4 } }), 'GET /api/v1/system/workers': () => json({ state, checkedAt: '2026-11-10T10:00:00Z', lastHeartbeat: null, servers: [], jobs: {} }) })
    const auth = useAuthStore()
    const host = useHostStore()
    auth.me = me as never
    host.context = { kind, tenant: null } as never
    const w = mount(WorkerStatus, { attachTo: document.body, global: { plugins: plugins() } })
    await settle()
    return { w, f }
  }

  it('shows a red worker indicator with what to do when the service is not running (tenant administrator)', async () => {
    const { w, f } = await workerPage({ id: 'a', isTenantAdministrator: true, isSystemAdministrator: false }, 'tenant', 'Stopped')
    const chip = document.body.querySelector<HTMLButtonElement>('[data-testid="worker-status"]')!
    expect(chip.classList.contains('down')).toBe(true)
    expect(f.calls.some((c) => c.url.endsWith('/tenant/workers'))).toBe(true)
    chip.click()
    await settle()
    expect(document.body.querySelector('[data-testid="worker-state"]')!.textContent).toContain('not running')
    expect(document.body.querySelector('[data-testid="worker-help"]')!.textContent).toContain('Start-Service RaaviWorkers')
    w.unmount()
  })

  it('shows a green indicator when the worker reported recently and nothing to people who are not administrators', async () => {
    const ok = await workerPage({ id: 'a', isTenantAdministrator: true, isSystemAdministrator: false }, 'tenant', 'Running')
    expect(document.body.querySelector('[data-testid="worker-status"]')!.classList.contains('ok')).toBe(true)
    ok.w.unmount()

    const plain = await workerPage({ id: 'b', isTenantAdministrator: false, isSystemAdministrator: false }, 'tenant', 'Stopped')
    expect(document.body.querySelector('[data-testid="worker-status"]')).toBeNull()
    expect(plain.f.calls.some((c) => c.url.includes('/workers'))).toBe(false) // not even asked
    plain.w.unmount()
  })

  it('uploads the chosen archive with the new key when a space is imported', async () => {
    const f = install({
      'GET /api/v1/spaces': () => json([]),
      'GET /api/v1/spaces-capabilities': () => json({ canCreateSpaces: true }),
      'POST /api/v1/spaces/import': () => json({ key: 'NEWKEY', name: 'Handbook', pages: 3, attachments: 1, comments: 0, warnings: [], notImported: { databases: 1 } }),
    })
    useAuthStore().me = { id: 'a', isTenantAdministrator: true, isSystemAdministrator: false } as never
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/spaces')
    const w = mount(SpacesView, { attachTo: document.body, global: { plugins: [...plugins(), router] } })
    await settle()
    document.body.querySelector<HTMLButtonElement>('[data-testid="import-space"]')!.click()
    await settle()
    const input = document.body.querySelector<HTMLInputElement>('[data-testid="import-file"]')!
    const file = new File(['zip'], 'space.zip', { type: 'application/zip' })
    Object.defineProperty(input, 'files', { value: [file] })
    input.dispatchEvent(new Event('change'))
    const key = document.body.querySelector<HTMLInputElement>('[data-testid="import-key"]')!
    key.value = 'NEWKEY'
    key.dispatchEvent(new Event('input'))
    await settle()
    document.body.querySelector<HTMLButtonElement>('[data-testid="import-run"]')!.click()
    await settle()
    const sent = f.calls.find((c) => c.url.endsWith('/spaces/import'))!
    expect((sent.body as FormData).get('key')).toBe('NEWKEY')
    expect((sent.body as FormData).get('file')).toBeInstanceOf(File)
    const result = document.body.querySelector('[data-testid="import-result"]')!.textContent!
    expect(result).toContain('Imported 3 pages, 1 files and 0 comments')
    expect(result).toContain('databases')
    w.unmount()
  })
})
