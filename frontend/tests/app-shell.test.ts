import { beforeEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import AppShell from '@/layouts/AppShell.vue'
import { createRaaviI18n } from '@/core/i18n'
import { fetchServiceStatus } from '@/core/api/health'
import { useLocaleStore } from '@/stores/locale'

describe('AppShell', () => {
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
  })

  it('switches language and document direction', async () => {
    const i18n = createRaaviI18n('fa')
    const wrapper = mount(AppShell, { global: { plugins: [i18n, PrimeVue] } })

    expect(wrapper.text()).toContain('راوی')

    const english = wrapper.findAll('[data-testid="locale-switch"] button').find((b) => b.text() === 'English')
    expect(english).toBeDefined()
    await english!.trigger('click')

    expect(document.documentElement.dir).toBe('ltr')
    expect(useLocaleStore().locale).toBe('en') // App.vue syncs vue-i18n from this store
    expect(localStorage.getItem('raavi.locale')).toBe('en')
  })
})

describe('fetchServiceStatus', () => {
  it('maps responses and network failures', async () => {
    expect(await fetchServiceStatus(async () => new Response('Healthy', { status: 200 }))).toBe('healthy')
    expect(await fetchServiceStatus(async () => new Response('', { status: 503 }))).toBe('unavailable')
    expect(await fetchServiceStatus(async () => { throw new TypeError('offline') })).toBe('unavailable')
  })
})
