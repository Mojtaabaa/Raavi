import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createMemoryHistory } from 'vue-router'
import App from '@/app/App.vue'
import { createRaaviRouter, resetRouterBootstrap } from '@/router'
import { createRaaviI18n } from '@/core/i18n'
import { configureHttp } from '@/core/api/http'
import { fakeFetch, json, problem } from './support/fakeApi'

const TOKEN = 'abcdefghijklmnopqrstuvwxyz012345'
const systemMe = { id: 'u1', displayName: 'Administrator', email: 'admin@vesta.local', preferredLocale: null, hostKind: 'system', isTenantAdministrator: false, isSystemAdministrator: true, provider: 'local' }

const mounted: { unmount: () => void }[] = []

async function mountAt(path: string, routes: Parameters<typeof fakeFetch>[0]) {
  const f = fakeFetch(routes)
  configureHttp(f.fn)
  vi.stubGlobal('fetch', f.fn)
  const router = createRaaviRouter(createMemoryHistory())
  await router.push(path)
  const wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
  mounted.push(wrapper)
  for (let i = 0; i < 4; i++) {
    await new Promise((r) => setTimeout(r, 20))
    await flushPromises()
  }
  return { wrapper, calls: f.calls, router }
}

const settle = async () => {
  for (let i = 0; i < 8; i++) {
    await new Promise((r) => setTimeout(r, 20))
    await flushPromises()
  }
}
const set = async (sel: string, value: string) => {
  const el = document.body.querySelector<HTMLInputElement>(sel)!
  el.value = value
  el.dispatchEvent(new Event('input'))
  await flushPromises()
}

describe('first sign-in of the bootstrap administrator', () => {
  beforeEach(() => {
    localStorage.clear()
    localStorage.setItem('raavi.locale', 'en')
    setActivePinia(createPinia())
    resetRouterBootstrap()
    window.history.replaceState(null, '', '/')
  })
  afterEach(() => {
    while (mounted.length) mounted.pop()!.unmount()
    vi.unstubAllGlobals()
  })

  const base = {
    'GET /api/v1/host/context': () => json({ kind: 'system', tenant: null }),
    'GET /api/v1/auth/me': () => problem(401, 'auth.required'),
    'GET /api/v1/auth/antiforgery': () => json({ token: 'xsrf' }),
  }

  it('turns the "first sign-in required" answer into the setup page without asking for the code first', async () => {
    const { calls, router } = await mountAt('/system/login', {
      ...base,
      'POST /api/v1/auth/system/login': () => problem(401, 'auth.first_login_required'),
      'POST /api/v1/auth/system/first-login': () => json({ acceptUrl: `https://raavi.example/system/setup#token=${TOKEN}` }),
      'POST /api/v1/auth/system/setup/inspect': () => json({ email: 'admin@vesta.local', expiresAt: '2026-11-09T12:00:00Z', passwordChangeRequired: true }),
    })
    await set('#email', 'admin@vesta.local')
    await set('#password', '123@vesta')
    document.body.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'))
    await settle()

    expect(calls.find((c) => c.url.endsWith('/system/first-login'))!.body).toEqual({ email: 'admin@vesta.local', password: '123@vesta' })
    expect(router.currentRoute.value.name).toBe('system-setup')
    expect(router.currentRoute.value.hash).toBe(`#token=${TOKEN}`) // the setup page reads the token from the address fragment
  })

  it('sends the temporary and the new password, refuses a mismatching repeat locally and ends signed in', async () => {
    window.history.replaceState(null, '', `/system/setup#token=${TOKEN}`)
    const { calls, router } = await mountAt('/system/setup', {
      ...base,
      'POST /api/v1/auth/system/setup/inspect': () => json({ email: 'admin@vesta.local', expiresAt: '2026-11-09T12:00:00Z', passwordChangeRequired: true }),
      'POST /api/v1/auth/system/setup/begin': () => json({ secret: 'JBSWY3DPEHPK3PXP', otpAuthUri: 'otpauth://totp/Raavi:admin@vesta.local?secret=JBSWY3DPEHPK3PXP&issuer=Raavi' }),
      'POST /api/v1/auth/system/setup/complete': () => json(systemMe),
      'GET /api/v1/system/tenants': () => json({ items: [], total: 0 }),
    })
    const start = [...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('Start setup'))!
    start.click()
    await settle()

    await set('#pw', '123@vesta')
    await set('#pw-new', 'Vesta-Admin-2026-Strong!')
    await set('#pw-confirm', 'Something-else-entirely')
    await set('#otp', '123456')
    document.body.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'))
    await settle()
    expect(calls.some((c) => c.url.endsWith('/setup/complete'))).toBe(false) // nothing is sent while the two new passwords differ
    expect(document.body.textContent).toContain('not the same')

    await set('#pw-confirm', 'Vesta-Admin-2026-Strong!')
    document.body.querySelector<HTMLFormElement>('form')!.dispatchEvent(new Event('submit'))
    await settle()
    expect(calls.find((c) => c.url.endsWith('/setup/complete'))!.body).toEqual({ token: TOKEN, displayName: '', password: '123@vesta', code: '123456', newPassword: 'Vesta-Admin-2026-Strong!' })
    expect(router.currentRoute.value.name).toBe('system-tenants')
  })

  it('keeps the ordinary invitation setup (no temporary password fields) for invited administrators', async () => {
    window.history.replaceState(null, '', `/system/setup#token=${TOKEN}`)
    await mountAt('/system/setup', {
      ...base,
      'POST /api/v1/auth/system/setup/inspect': () => json({ email: 'new@vesta.local', expiresAt: '2026-11-09T12:00:00Z' }),
      'POST /api/v1/auth/system/setup/begin': () => json({ secret: 'JBSWY3DPEHPK3PXP', otpAuthUri: 'otpauth://totp/Raavi:new@vesta.local?secret=JBSWY3DPEHPK3PXP&issuer=Raavi' }),
    })
    ;[...document.body.querySelectorAll('button')].find((b) => b.textContent?.includes('Start setup'))!.click()
    await settle()
    expect(document.body.querySelector('#pw-new')).toBeNull()
    expect(document.body.querySelector('#pw')).not.toBeNull()
  })
})
