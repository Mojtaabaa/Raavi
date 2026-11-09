import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount, type VueWrapper } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createMemoryHistory } from 'vue-router'
import App from '@/app/App.vue'
import UserAvatar from '@/app/UserAvatar.vue'
import { configureHttp } from '@/core/api/http'
import { createRaaviI18n } from '@/core/i18n'
import { createRaaviRouter, resetRouterBootstrap } from '@/router'
import { useAvatarStore } from '@/stores/avatars'
import { useHostStore } from '@/stores/host'
import { fakeFetch, json, problem } from './support/fakeApi'

// The crop dialog needs a canvas (not in jsdom): a stand-in that "crops" at once and hands back a small picture.
vi.mock('@/app/PictureCropDialog.vue', async () => {
  const { defineComponent, h, watch } = await import('vue')
  return {
    default: defineComponent({
      props: { file: { type: Object, default: null } },
      emits: ['confirm', 'cancel'],
      setup(props, { emit }) {
        watch(() => props.file, (f) => f && emit('confirm', new Blob(['small-picture'], { type: 'image/webp' })), { immediate: true })
        return () => h('span', { 'data-testid': 'crop-stub' })
      },
    }),
  }
})

const A = '11111111-1111-4111-8111-111111111111'
const B = '22222222-2222-4222-8222-222222222222'
const C = '33333333-3333-4333-8333-333333333333'

const tenantCtx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'UTC', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
const me = { id: A, displayName: 'Sara Ahmadi', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }

const settle = async () => {
  for (let i = 0; i < 6; i++) {
    await new Promise((r) => setTimeout(r, 40))
    await flushPromises()
  }
}

describe('profile pictures', () => {
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

  function mountAvatar(props: Record<string, unknown>, ctx: 'tenant' | 'system', f: ReturnType<typeof fakeFetch>) {
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const host = useHostStore()
    host.context = ctx === 'tenant' ? ({ kind: 'tenant', tenant: tenantCtx.tenant } as never) : ({ kind: 'system' } as never)
    return mount(UserAvatar, { props, global: { plugins: [createRaaviI18n('en'), PrimeVue] } })
  }

  it('asks once for all the pictures of a render pass and shows initials for users without one', async () => {
    const f = fakeFetch({ [`GET /api/v1/users/avatars`]: () => json({ [A]: 3 }) })
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    useHostStore().context = { kind: 'tenant', tenant: tenantCtx.tenant } as never
    const store = useAvatarStore()
    store.want(A)
    store.want(B)
    store.want(C)
    store.want(A) // asked twice: still one id
    await settle()
    expect(f.calls).toHaveLength(1)
    expect(f.calls[0]!.url).toBe(`/api/v1/users/avatars?ids=${A},${B},${C}`)
    expect(store.versions).toEqual({ [A]: 3, [B]: null, [C]: null })
    store.want(A) // known now: no new request
    await settle()
    expect(f.calls).toHaveLength(1)
  })

  it('shows the picture with its version in the address, or initials', async () => {
    const f = fakeFetch({ 'GET /api/v1/users/avatars': () => json({ [A]: 3 }) })
    wrapper = mountAvatar({ userId: A, name: 'Sara Ahmadi', size: 40 }, 'tenant', f)
    await settle()
    const img = wrapper.get('img')
    expect(img.attributes('src')).toBe(`/api/v1/users/${A}/avatar?v=3`)
    expect(img.attributes('alt')).toBe('')

    const other = mount(UserAvatar, { props: { userId: B, name: 'سارا احمدی' }, global: { plugins: [createRaaviI18n('en'), PrimeVue] } })
    await settle()
    expect(other.find('img').exists()).toBe(false)
    expect(other.get('.initials').attributes('data-initials')).toBe('سا')
    other.unmount()

    const noId = mount(UserAvatar, { props: { name: null }, global: { plugins: [createRaaviI18n('en'), PrimeVue] } })
    expect(noId.get('.initials').attributes('data-initials')).toBe('?')
    noId.unmount()
  })

  it('falls back to the initials when the picture cannot be loaded and never asks outside a workspace', async () => {
    const f = fakeFetch({ 'GET /api/v1/users/avatars': () => json({ [A]: 1 }) })
    wrapper = mountAvatar({ userId: A, name: 'Sara Ahmadi' }, 'tenant', f)
    await settle()
    await wrapper.get('img').trigger('error')
    expect(wrapper.find('img').exists()).toBe(false)
    expect(wrapper.get('.initials').attributes('data-initials')).toBe('SA')

    const g = fakeFetch({})
    const system = mountAvatar({ userId: A, name: 'Root Admin' }, 'system', g)
    await settle()
    expect(g.calls).toHaveLength(0) // the system area has no workspace pictures
    expect(system.find('img').exists()).toBe(false)
    system.unmount()
  })

  async function openProfile(extra: Parameters<typeof fakeFetch>[0]) {
    const f = fakeFetch({
      'GET /api/v1/host/context': () => json(tenantCtx),
      'GET /api/v1/auth/me': () => json(me),
      'GET /api/v1/auth/me/workspaces': () => json([]),
      'GET /api/v1/auth/antiforgery': () => json({ token: 'xsrf' }),
      'GET /api/v1/notifications/unread-count': () => json({ count: 0 }),
      'GET /api/v1/auth/me/identities': () => json([]),
      'GET /api/v1/auth/providers': () => json([]),
      'GET /api/v1/notifications/preferences': () => problem(404, 'x'),
      ...extra,
    })
    vi.stubGlobal('fetch', f.fn)
    configureHttp(f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/me')
    wrapper = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    await settle()
    return f
  }

  it('uploads a shrunken picture, shows it at once, and removes it again', async () => {
    let version: number | null = null
    const f = await openProfile({
      'GET /api/v1/users/avatars': () => json(version ? { [A]: version } : {}),
      'PUT /api/v1/auth/me/avatar': () => {
        version = 2
        return json({ version: 2 })
      },
      'DELETE /api/v1/auth/me/avatar': () => {
        version = null
        return new Response(null, { status: 204 })
      },
    })
    const root = document.body
    expect(root.querySelector('[data-testid="picture-remove"]')).toBeNull() // no picture yet

    const input = root.querySelector<HTMLInputElement>('[data-testid="picture-input"]')!
    const file = new File(['raw-photo'], 'me.png', { type: 'image/png' })
    Object.defineProperty(input, 'files', { value: [file], configurable: true })
    input.dispatchEvent(new Event('change'))
    await settle()

    const put = f.calls.find((c) => c.method === 'PUT')!
    expect(put.url).toBe('/api/v1/auth/me/avatar')
    expect(put.body).toBeInstanceOf(FormData)
    expect(((put.body as FormData).get('file') as File).size).toBe('small-picture'.length) // the resized picture, not the original
    expect(root.querySelector('[data-testid="picture"] img')?.getAttribute('src')).toBe(`/api/v1/users/${A}/avatar?v=2`)
    expect(root.querySelector('[data-testid="user-name"]')!.querySelector('img')?.getAttribute('src')).toBe(`/api/v1/users/${A}/avatar?v=2`) // also in the header

    root.querySelector<HTMLButtonElement>('[data-testid="picture-remove"]')!.click()
    await settle()
    expect(f.calls.some((c) => c.method === 'DELETE' && c.url === '/api/v1/auth/me/avatar')).toBe(true)
    expect(root.querySelector('[data-testid="picture"] img')).toBeNull()
  })

  it('refuses files that are not PNG, JPEG or WebP without sending anything', async () => {
    const f = await openProfile({ 'GET /api/v1/users/avatars': () => json({}) })
    const input = document.body.querySelector<HTMLInputElement>('[data-testid="picture-input"]')!
    Object.defineProperty(input, 'files', { value: [new File(['<svg/>'], 'x.svg', { type: 'image/svg+xml' })], configurable: true })
    input.dispatchEvent(new Event('change'))
    await settle()
    expect(f.calls.some((c) => c.method === 'PUT')).toBe(false)
    expect(document.body.textContent).toContain('Choose a PNG, JPEG or WebP picture')
  })
})
