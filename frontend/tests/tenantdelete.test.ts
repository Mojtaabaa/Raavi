import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import SystemTenantsView from '@/modules/tenants/views/SystemTenantsView.vue'
import { createRaaviI18n } from '@/core/i18n'
import { configureHttp } from '@/core/api/http'
import { fakeFetch, json } from './support/fakeApi'

const settle = async () => {
  for (let i = 0; i < 4; i++) {
    await new Promise((r) => setTimeout(r, 20))
    await flushPromises()
  }
}

const base = { displayNameFa: 'ن', slug: 'x', statusReason: null, createdAt: '2026-01-01T00:00:00Z', url: null }
const tenants = [
  { ...base, id: 'a', code: 'ACME', displayNameEn: 'Acme', status: 'Active' },
  { ...base, id: 'b', code: 'OLDCO', displayNameEn: 'Old Co', status: 'PendingDeletion', deletionScheduledFor: '2026-12-01T00:00:00Z' },
  { ...base, id: 'c', code: 'GONE', displayNameEn: 'Gone', status: 'Deleted', deletedAt: '2026-10-01T00:00:00Z' },
]

describe('workspace deletion (system tenants page)', () => {
  beforeEach(() => {
    localStorage.setItem('raavi.locale', 'en')
    setActivePinia(createPinia())
  })
  afterEach(() => vi.unstubAllGlobals())

  async function open(extra: Parameters<typeof fakeFetch>[0] = {}) {
    const f = fakeFetch({ 'GET /api/v1/auth/antiforgery': () => json({ token: 'x' }), 'GET /api/v1/system/tenants': () => json({ items: tenants, total: 3 }), ...extra })
    configureHttp(f.fn)
    vi.stubGlobal('fetch', f.fn)
    const w = mount(SystemTenantsView, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, ToastService] } })
    await flushPromises()
    return { w, f }
  }

  it('offers cancel / purge for a pending workspace, nothing for a deleted one, and needs the typed code to request deletion', async () => {
    const { w, f } = await open({ 'POST /api/v1/system/tenants/a/delete-request': () => json({ ...tenants[0], status: 'PendingDeletion' }) })
    expect(document.body.querySelector('[data-testid="tenant-due-OLDCO"]')!.textContent).toContain('Deleted for good on')
    expect(document.body.querySelector('[data-testid="tenant-cancel-delete-OLDCO"]')).not.toBeNull()
    expect(document.body.querySelector('[data-testid="tenant-purge-OLDCO"]')).not.toBeNull()
    expect(document.body.querySelector('[data-testid="tenant-delete-GONE"]')).toBeNull()
    expect(document.body.querySelector('[data-testid="tenant-logo-GONE"]')).toBeNull()

    document.body.querySelector<HTMLButtonElement>('[data-testid="tenant-delete-ACME"]')!.click()
    await flushPromises()
    const confirm = document.body.querySelector<HTMLButtonElement>('[data-testid="delete-confirm"]')!
    expect(confirm.disabled).toBe(true)
    const reason = document.body.querySelector<HTMLInputElement>('[data-testid="delete-reason"]')!
    reason.value = 'closing down'
    reason.dispatchEvent(new Event('input'))
    const code = document.body.querySelector<HTMLInputElement>('[data-testid="delete-code"]')!
    code.value = 'acm'
    code.dispatchEvent(new Event('input'))
    await flushPromises()
    expect(confirm.disabled).toBe(true) // a wrong code keeps the button off
    code.value = 'acme'
    code.dispatchEvent(new Event('input'))
    await flushPromises()
    expect(confirm.disabled).toBe(false)
    confirm.click()
    await settle()
    const call = f.calls.find((c) => c.url.endsWith('/delete-request'))!
    expect(call.body).toEqual({ reason: 'closing down', confirmCode: 'acme' })
    w.unmount()
  })

  it('purge now asks for the code only and cancelling a deletion calls the cancel endpoint', async () => {
    const { w, f } = await open({
      'POST /api/v1/system/tenants/b/purge': () => json({ ...tenants[1], status: 'Deleted' }),
      'POST /api/v1/system/tenants/b/cancel-deletion': () => json({ ...tenants[1], status: 'Active' }),
    })
    document.body.querySelector<HTMLButtonElement>('[data-testid="tenant-purge-OLDCO"]')!.click()
    await flushPromises()
    expect(document.body.querySelector('[data-testid="delete-reason"]')).toBeNull()
    const code = document.body.querySelector<HTMLInputElement>('[data-testid="delete-code"]')!
    code.value = 'OLDCO'
    code.dispatchEvent(new Event('input'))
    await flushPromises()
    document.body.querySelector<HTMLButtonElement>('[data-testid="delete-confirm"]')!.click()
    await settle()
    expect(f.calls.find((c) => c.url.endsWith('/purge'))!.body).toEqual({ confirmCode: 'OLDCO' })

    document.body.querySelector<HTMLButtonElement>('[data-testid="tenant-cancel-delete-OLDCO"]')!.click()
    await settle()
    expect(f.calls.some((c) => c.url.endsWith('/cancel-deletion'))).toBe(true)
    w.unmount()
  })
})
