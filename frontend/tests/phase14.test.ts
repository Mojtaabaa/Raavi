import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest'
import { flushPromises, mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import { createMemoryHistory } from 'vue-router'
import App from '@/app/App.vue'
import ErrorHelp from '@/app/ErrorHelp.vue'
import { createRaaviRouter, resetRouterBootstrap } from '@/router'
import { createRaaviI18n } from '@/core/i18n'
import { ApiError, configureHttp } from '@/core/api/http'
import { errorSteps } from '@/core/api/errors'
import { applyThemeToDocument, themeVariables, THEMES } from '@/core/theme/themes'
import { useThemeStore } from '@/stores/theme'
import { elementFromGesture, GRID, snapTo } from '@/modules/whiteboards/boardModel'
import { parseCsv, planImport } from '@/modules/databases/csv'
import type { DbColumn } from '@/core/api/databases'
import { fakeFetch, json, problem } from './support/fakeApi'

describe('colour themes', () => {
  beforeEach(() => {
    localStorage.clear()
    setActivePinia(createPinia())
    document.documentElement.removeAttribute('style')
    document.documentElement.className = ''
  })

  it('derives tokens, primary and surface scales and the icon colour from a few numbers; the built-in themes override nothing', () => {
    expect(themeVariables(THEMES.find((t) => t.id === 'light')!)).toEqual({})
    expect(themeVariables(THEMES.find((t) => t.id === 'dark')!)).toEqual({})
    const choc = themeVariables(THEMES.find((t) => t.id === 'chocolate')!)
    expect(choc['--raavi-bg']).toMatch(/^#[0-9a-f]{6}$/)
    expect(choc['--raavi-icon']).toMatch(/^#[0-9a-f]{6}$/) // icons follow the accent
    expect(choc['--p-primary-500']).toMatch(/^#[0-9a-f]{6}$/)
    expect(choc['--p-surface-900']).toMatch(/^#[0-9a-f]{6}$/)
    const contrast = themeVariables(THEMES.find((t) => t.id === 'contrast')!)
    expect([contrast['--raavi-bg'], contrast['--raavi-text']]).toEqual(['#ffffff', '#000000'])
    for (const t of THEMES) for (const v of Object.values(themeVariables(t))) expect(v).toMatch(/^#[0-9a-f]{6}$/) // only colours ever reach the document
  })

  it('applies and removes a theme on the document (dark class, data-theme, no leftovers)', () => {
    const root = document.documentElement
    expect(applyThemeToDocument('chocolate', false).dark).toBe(true)
    expect(root.classList.contains('raavi-dark')).toBe(true)
    expect(root.dataset.theme).toBe('chocolate')
    expect(root.style.getPropertyValue('--raavi-icon')).not.toBe('')

    expect(applyThemeToDocument('ocean', true).dark).toBe(false) // a light theme stays light on a dark device
    expect(root.classList.contains('raavi-dark')).toBe(false)

    expect(applyThemeToDocument('light', true).dark).toBe(false)
    expect(root.style.getPropertyValue('--raavi-icon')).toBe('') // leftovers of the previous theme are gone
    expect(applyThemeToDocument('system', true).dark).toBe(true) // "like my device" follows it
  })

  it('remembers the choice, saves it to the profile only when signed in, and lets the profile win after sign-in', async () => {
    const f = fakeFetch({ 'PUT /api/v1/auth/me/theme': () => new Response(null, { status: 204 }), 'GET /api/v1/auth/antiforgery': () => json({ token: 'x' }) })
    configureHttp(f.fn)
    const store = useThemeStore()
    await store.choose('forest', false)
    expect(localStorage.getItem('raavi.theme')).toBe('forest')
    expect(f.calls.some((c) => c.method === 'PUT')).toBe(false)

    await store.choose('rose', true)
    expect(f.calls.find((c) => c.method === 'PUT')?.body).toEqual({ theme: 'rose' })
    await store.choose('system', true)
    expect(f.calls.filter((c) => c.method === 'PUT').at(-1)?.body).toEqual({ theme: null }) // back to "like my device"

    store.adopt('midnight')
    expect(store.theme).toBe('midnight')
    expect(document.documentElement.dataset.theme).toBe('midnight')
    store.adopt('<script>') // anything unknown is ignored
    expect(store.theme).toBe('midnight')
  })
})

describe('error guidance', () => {
  const i18n = createRaaviI18n('en')
  const t = (k: string) => i18n.global.t(k) as string
  const te = (k: string) => i18n.global.te(k)

  it('has steps for errors people can fix, and none for the rest', () => {
    const steps = errorSteps(new ApiError(400, 'content.body_invalid', 'x'), t, te)
    expect(steps.length).toBeGreaterThanOrEqual(3)
    expect(steps.join(' ')).toContain('Ctrl+Z')
    expect(errorSteps(new ApiError(500, 'something.unknown', 'x'), t, te)).toEqual([])
    expect(errorSteps(new Error('plain'), t, te)).toEqual([])
  })

  it('every language has the same steps (a Persian user is told the same things)', () => {
    const fa = createRaaviI18n('fa')
    for (const code of ['content.body_invalid', 'content.schema_unsupported', 'password.length', 'password.too_simple', 'password.contains_email', 'member.account_shared', 'logo.type_invalid', 'logo.too_large']) {
      const e = errorSteps(new ApiError(400, code, 'x'), (k) => i18n.global.t(k) as string, (k) => i18n.global.te(k))
      const f = errorSteps(new ApiError(400, code, 'x'), (k) => fa.global.t(k) as string, (k) => fa.global.te(k))
      expect(e.length, code).toBeGreaterThan(0)
      expect(f.length, code).toBe(e.length)
    }
  })

  it('shows the message, the steps and the guide link in one box', () => {
    const w = mount(ErrorHelp, { props: { error: new ApiError(400, 'content.body_invalid', 'x') }, global: { plugins: [createRaaviI18n('en'), PrimeVue] } })
    expect(w.get('[data-testid="error-help"]').text()).toContain('The page content is not valid.')
    expect(w.findAll('li').length).toBeGreaterThanOrEqual(3)
    expect(w.get('[data-testid="help-link"]').attributes('href')).toBe('/help/fa/')
    w.unmount()
  })
})

describe('whiteboard helpers', () => {
  it('draws diamonds, triangles and lines like the other shapes', () => {
    const d = elementFromGesture('diamond', 10, 10, 110, 90)!
    expect([d.type, d.w, d.h]).toEqual(['Diamond', 100, 80])
    const click = elementFromGesture('triangle', 50, 50, 50, 50)!
    expect([click.type, click.w, click.h]).toEqual(['Triangle', 130, 110]) // a click gives the default size
    const line = elementFromGesture('line', 0, 0, 40, 30)!
    expect([line.type, line.points]).toEqual(['Line', [0, 0, 40, 30]])
    expect(elementFromGesture('line', 5, 5, 5, 5)).toBeNull() // a click is no line
  })

  it('snaps to the grid only when asked to', () => {
    expect(snapTo(33, false)).toBe(33)
    expect(snapTo(33, true)).toBe(40)
    expect(snapTo(29, true)).toBe(20)
    expect(snapTo(-31, true)).toBe(-40)
    expect(GRID).toBe(20)
  })
})

describe('database CSV import', () => {
  const columns: DbColumn[] = [
    { id: 'c1', name: 'Name', type: 'Text', options: [] },
    { id: 'c2', name: 'Points', type: 'Number', options: [] },
    { id: 'c3', name: 'Status', type: 'Select', options: [{ id: 'o1', label: 'Todo' }, { id: 'o2', label: 'Done' }] },
    { id: 'c4', name: 'Ok', type: 'Checkbox', options: [] },
    { id: 'c5', name: 'Due', type: 'Date', options: [] },
  ]

  it('reads quotes, doubled quotes, line breaks inside fields, a BOM and any of , ; tab as separator', () => {
    expect(parseCsv('﻿a,b\r\n"x, y","say ""hi"""\r\n"two\nlines",z\r\n')).toEqual([['a', 'b'], ['x, y', 'say "hi"'], ['two\nlines', 'z']])
    expect(parseCsv('a;b\n1;2')).toEqual([['a', 'b'], ['1', '2']])
    expect(parseCsv('a\tb\n1\t2\n\n')).toEqual([['a', 'b'], ['1', '2']])
  })

  it('maps columns by header name, converts values, and reports what it could not use', () => {
    const plan = planImport(parseCsv('name,POINTS,status,ok,due,Unknown\nApple,"1,200",done,yes,2026-11-09,x\nPear,abc,nope,no,09/11/2026,y\n'), columns)
    expect(plan.ignored).toEqual(['Unknown'])
    expect(plan.rows[0]).toEqual({ c1: 'Apple', c2: 1200, c3: 'o2', c4: true, c5: '2026-11-09' })
    expect(plan.rows[1]).toEqual({ c1: 'Pear' }) // bad cells are left empty ...
    expect(plan.problems).toEqual([{ row: 2, column: 'Points' }, { row: 2, column: 'Status' }, { row: 2, column: 'Due' }]) // ... and counted
  })
})

describe('sign-in page of system administrators', () => {
  beforeEach(() => {
    localStorage.clear()
    localStorage.setItem('raavi.locale', 'en')
    setActivePinia(createPinia())
    resetRouterBootstrap()
    window.history.replaceState(null, '', '/')
  })
  afterEach(() => vi.unstubAllGlobals())

  async function open(mfaRequired: boolean) {
    const f = fakeFetch({
      'GET /api/v1/host/context': () => json({ kind: 'system', tenant: null }),
      'GET /api/v1/auth/me': () => problem(401, 'auth.required'),
      'GET /api/v1/auth/antiforgery': () => json({ token: 'x' }),
      'GET /api/v1/auth/system/options': () => json({ mfaRequired }),
    })
    configureHttp(f.fn)
    vi.stubGlobal('fetch', f.fn)
    const router = createRaaviRouter(createMemoryHistory())
    await router.push('/system/login')
    const w = mount(App, { attachTo: document.body, global: { plugins: [createRaaviI18n('en'), PrimeVue, router, ToastService, ConfirmationService] } })
    for (let i = 0; i < 6; i++) {
      await new Promise((r) => setTimeout(r, 20))
      await flushPromises()
    }
    return w
  }

  it('asks for the authenticator code, and does not when two-step verification is switched off', async () => {
    const on = await open(true)
    expect(document.body.querySelector('[data-testid="otp"]')).not.toBeNull()
    on.unmount()

    resetRouterBootstrap()
    const off = await open(false)
    expect(document.body.querySelector('[data-testid="otp"]')).toBeNull()
    expect(document.body.querySelector('[data-testid="help-link"]')).not.toBeNull() // the guide is linked from the sign-in page
    off.unmount()
  })
})
