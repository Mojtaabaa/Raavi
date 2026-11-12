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
import { addDays, daysInMonth, firstOfMonth, monthGrid, partsOf, shiftMonth, shiftYear, toIso, weekdayOf, yearMonths } from '@/modules/calendar/calendarMath'
import { fakeFetch, json } from './support/fakeApi'

const tenantCtx = { kind: 'tenant', tenant: { slug: 'acme', displayName: { fa: 'اکمی', en: 'Acme' }, defaultLocale: 'en', timeZone: 'Asia/Tehran', calendar: 'gregorian', branding: { primaryColor: null, secondaryColor: null, welcomeMessageFa: null, welcomeMessageEn: null } } }
const me = { id: 'u1', displayName: 'Sara', email: 's@x.io', preferredLocale: 'en', hostKind: 'tenant', isTenantAdministrator: false, isSystemAdministrator: false, provider: 'local' }
const space = (canCreate: boolean) => ({ id: 's1', key: 'ENG', name: 'Engineering', description: null, type: 'Team', status: 'Active', homePageId: 'home', updatedAt: '2026-11-07T08:00:00Z', createdBy: 'u1', createdByName: 'Sara', createdAt: '2026-11-07T08:00:00Z', revision: 1, requirePublishApproval: false, rights: { canView: true, canCreate, canAdminister: false, canManageTemplates: false } })
const withEtag = (body: unknown) => new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json', ETag: '"r1"' } })

const occurrence = (over: Record<string, unknown> = {}) => ({
  eventId: 'e1', calendarId: 'c1', spaceKey: 'ENG', spaceName: 'Engineering', title: 'Standup', location: null, allDay: false, startDate: toIso(new Date()), startTime: '09:30', endDate: toIso(new Date()), endTime: '09:45',
  timeZone: 'Asia/Tehran', startUtc: null, endUtc: null, isRecurring: true, occurrenceDate: toIso(new Date()), canEdit: true, sourceKind: 'Raavi', externalUrl: null, freeBusyOnly: false, type: 'Event', color: '#3b82f6', ...over,
})
const fullEvent = (over: Record<string, unknown> = {}) => ({
  id: 'e1', calendarId: 'c1', spaceKey: 'ENG', spaceName: 'Engineering', title: 'Standup', description: null, location: null, allDay: false, startDate: toIso(new Date()), startTime: '09:30', endDate: toIso(new Date()), endTime: '09:45',
  timeZone: 'Asia/Tehran', recurrence: { frequency: 'Weekly', interval: 1, weekDays: [weekdayOf(toIso(new Date()))], calendar: 'Gregorian', until: null, count: null }, excludedDates: [],
  type: 'Event', status: 'Scheduled', priority: 'Normal', color: null, onlineUrl: null, isPrivate: false, reminders: [], attendees: [], linkedPageId: null, linkedWhiteboardId: null, myResponse: null, createdByName: 'Ali', createdAt: '2026-11-07T08:00:00Z', revision: 3, canEdit: true, ...over,
})
const board = { id: 'c1', scope: 'Space', spaceId: 's1', ownerUserId: null, name: 'Engineering', description: null, color: '#3b82f6', icon: null, isDefault: true, archivedAt: null, revision: 1 }
const prefs = { browserAlerts: true, ttsEnabled: false, ttsLocale: 'en', ttsRate: 1, hidePrivateTitlesInTts: true, quietStartMinutes: null, quietEndMinutes: null, timeZone: 'UTC' }

describe('calendar arithmetic', () => {
  it('derives Persian months from Intl, so Jalali dates match the known conversions', () => {
    expect(partsOf('2026-03-21', 'persian')).toEqual({ y: 1405, m: 1, d: 1 }) // Nowruz 1405
    expect(partsOf('2026-04-20', 'persian')).toEqual({ y: 1405, m: 1, d: 31 })
    expect(partsOf('2026-04-21', 'persian')).toEqual({ y: 1405, m: 2, d: 1 })
    expect(partsOf('2025-03-20', 'persian')).toEqual({ y: 1403, m: 12, d: 30 }) // leap year ends with the 30th of Esfand
    expect(firstOfMonth('2026-05-05', 'persian')).toBe('2026-04-21')
    expect(daysInMonth('2026-04-21', 'persian')).toBe(31)
    expect(daysInMonth('2026-10-23', 'persian')).toBe(30) // Aban has 30 days
    expect(daysInMonth('2026-02-10', 'gregory')).toBe(28)
    expect(daysInMonth('2028-02-10', 'gregory')).toBe(29)
  })

  it('moves between months in either calendar', () => {
    expect(shiftMonth('2026-04-25', 'persian', 1)).toBe('2026-05-22') // 1 Khordad
    expect(shiftMonth('2026-04-25', 'persian', -1)).toBe('2026-03-21')
    expect(shiftMonth('2026-01-31', 'gregory', 1)).toBe('2026-02-01')
    expect(shiftMonth('2026-01-15', 'gregory', -2)).toBe('2025-11-01')
    expect(shiftMonth('2026-01-15', 'gregory', 0)).toBe('2026-01-01')
    expect(addDays('2026-02-28', 1)).toBe('2026-03-01')
  })

  it('builds whole-week grids starting on the requested weekday', () => {
    const feb = monthGrid('2026-02-10', 'gregory', 0) // 1 Feb 2026 is a Sunday: exactly four weeks
    expect(feb).toHaveLength(28)
    expect(feb[0]).toEqual({ iso: '2026-02-01', inMonth: true, day: 1 })

    const far = monthGrid('2026-03-25', 'persian', 6) // 1 Farvardin 1405 is a Saturday: 31 days → five weeks
    expect(far).toHaveLength(35)
    expect(far[0]).toEqual({ iso: '2026-03-21', inMonth: true, day: 1 })
    expect(far.filter((d) => d.inMonth)).toHaveLength(31)
    expect(far[34]!.inMonth).toBe(false)
    expect(far.every((d, i) => i === 0 || d.iso === addDays(far[i - 1]!.iso, 1))).toBe(true)
  })

  it('lists twelve months for a year and shifts by calendar year', () => {
    const months = yearMonths('2026-07-15', 'gregory')
    expect(months).toHaveLength(12)
    expect(months[0]).toBe('2026-01-01')
    expect(months[11]).toBe('2026-12-01')
    expect(shiftYear('2026-03-21', 'persian', 1)).toBe('2027-03-21') // 1 Farvardin 1406
  })
})

describe('calendar view', () => {
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
    'GET /api/v1/calendars': () => json([board]),
    'GET /api/v1/spaces/ENG/calendars': () => json([board]),
    'GET /api/v1/calendar/preferences': () => json(prefs),
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

  it('shows the month in the tenant calendar, switches to Jalali, and requests a window of whole weeks', async () => {
    const f = fakeFetch({ ...base, 'GET /api/v1/calendar/occurrences': () => json([occurrence({ spaceKey: 'OPS', spaceName: 'Operations' })]) })
    const w = await open('/calendar', f)

    const first = f.calls.find((c) => c.url.startsWith('/api/v1/calendar/occurrences'))!
    const [from, to] = [/from=([\d-]+)/.exec(first.url)![1]!, /to=([\d-]+)/.exec(first.url)![1]!]
    expect(weekdayOf(from)).toBe(0) // English, Gregorian: weeks start on Sunday
    expect((new Date(to).getTime() - new Date(from).getTime()) / 86_400_000 + 1).toBeLessThanOrEqual(42)
    expect(w.findAll('[role="gridcell"]').length % 7).toBe(0)
    expect(w.get('[data-testid="occurrence"]').text()).toContain('Standup')
    expect(w.get('[data-testid="occurrence"]').text()).toContain('Operations') // the cross-space feed names the space
    expect(w.find('[data-testid="new-event"]').exists()).toBe(false) // events are created inside a space
    const gregorianTitle = w.get('[data-testid="month-title"]').text()

    const jalali = w.findAll('[data-testid="calendar-system"] [role="radio"], [data-testid="calendar-system"] .p-togglebutton').find((b) => b.text() === 'Jalali')!
    await jalali.trigger('click')
    for (let i = 0; i < 3; i++) await flushPromises()
    expect(w.get('[data-testid="month-title"]').text()).not.toBe(gregorianTitle)
    expect(f.calls.filter((c) => c.url.startsWith('/api/v1/calendar/occurrences')).length).toBeGreaterThan(1)

    const before = w.get('[data-testid="month-title"]').text()
    await w.get('[data-testid="next-month"]').trigger('click')
    await flushPromises()
    expect(w.get('[data-testid="month-title"]').text()).not.toBe(before)
  })

  it('opens the create dialog when double-clicking a day cell', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => withEtag(space(true)),
      'GET /api/v1/spaces/ENG/calendar/occurrences': () => json([]),
    })
    const w = await open('/spaces/ENG/calendar', f)
    const day = w.get('[data-testid="calendar-day"]')
    const iso = day.attributes('data-date')!
    expect(iso).toMatch(/^\d{4}-\d{2}-\d{2}$/)
    await day.trigger('dblclick')
    await flushPromises()
    expect(document.body.querySelector('[data-testid="event-title"]')).not.toBeNull()
    expect((document.body.querySelector('[data-testid="event-start"]') as HTMLInputElement).value).toBe(iso)
  })

  it('creates a weekly event that always repeats on its own weekday, in the space of the calendar', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => withEtag(space(true)),
      'GET /api/v1/spaces/ENG/calendar/occurrences': () => json([]),
      'POST /api/v1/spaces/ENG/calendar/events': () => json(fullEvent(), 201),
      'POST /api/v1/calendars/c1/events': () => json(fullEvent(), 201),
    })
    const w = await open('/spaces/ENG/calendar', f)
    expect(w.get('[data-testid="no-events"]').text()).toContain('No events')
    expect(w.get('[data-testid="export-calendar"]').attributes('href')).toBe('/api/v1/spaces/ENG/calendar/export')

    await w.get('[data-testid="new-event"]').trigger('click')
    await flushPromises()
    const set = (sel: string, value: string) => {
      const el = document.body.querySelector<HTMLInputElement>(sel)!
      el.value = value
      el.dispatchEvent(new Event('input'))
    }
    set('[data-testid="event-title"]', 'Planning')
    set('[data-testid="event-start"]', '2026-07-06') // a Monday
    set('[data-testid="event-end"]', '2026-07-06')
    await flushPromises()
    expect(document.body.querySelector('[data-testid="start-label"]')!.textContent!.length).toBeGreaterThan(0)

    // Choose "Weekly" through the component API, as the Select overlay is a portal.
    const select = (w.findAllComponents({ name: 'Select' }).find((c) => c.attributes('data-testid') === 'event-repeat') ?? w.findComponent('[data-testid="event-repeat"]'))
    ;(select as VueWrapper).vm.$emit('update:modelValue', 'Weekly')
    await flushPromises()
    expect(document.body.querySelector('#ev-day-1')).not.toBeNull() // weekday checkboxes appear

    document.body.querySelector<HTMLButtonElement>('[data-testid="save-event"]')!.click()
    for (let i = 0; i < 3; i++) await flushPromises()
    const post = f.calls.find((c) => c.method === 'POST' && (c.url === '/api/v1/spaces/ENG/calendar/events' || c.url === '/api/v1/calendars/c1/events'))!
    expect(post.body).toMatchObject({ title: 'Planning', allDay: false, startDate: '2026-07-06', startTime: '09:00', endDate: '2026-07-06', endTime: '10:00', timeZone: 'Asia/Tehran' })
    const rec = (post.body as { recurrence: { frequency: string; weekDays: number[]; until: null; count: null } }).recurrence
    expect(rec.frequency).toBe('Weekly')
    expect(rec.weekDays).toContain(1) // Monday is implied by the start date
    expect(rec.until).toBeNull()
  })

  it('opens an occurrence, cancels just that occurrence, and is read-only without Edit rights', async () => {
    const day = toIso(new Date())
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => withEtag(space(false)),
      'GET /api/v1/spaces/ENG/calendar/occurrences': () => json([occurrence()]),
      'GET /api/v1/calendar/events/e1': () => json(fullEvent()),
      'POST /api/v1/calendar/events/e1/exclusions': () => json(fullEvent({ excludedDates: [day] })),
    })
    const w = await open('/spaces/ENG/calendar', f)
    await w.get('[data-testid="occurrence"]').trigger('click')
    for (let i = 0; i < 3; i++) await flushPromises()
    expect((document.body.querySelector('[data-testid="event-title"]') as HTMLInputElement).value).toBe('Standup')
    document.body.querySelector<HTMLButtonElement>('[data-testid="cancel-occurrence"]')!.click()
    for (let i = 0; i < 3; i++) await flushPromises()
    const call = f.calls.find((c) => c.method === 'POST' && c.url === '/api/v1/calendar/events/e1/exclusions')!
    expect(call.body).toEqual({ date: day })

    // Read-only members see the details but no save/cancel/delete buttons.
    wrapper?.unmount()
    resetRouterBootstrap()
    const g = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => withEtag(space(false)),
      'GET /api/v1/spaces/ENG/calendar/occurrences': () => json([occurrence({ canEdit: false })]),
      'GET /api/v1/calendar/events/e1': () => json(fullEvent({ canEdit: false })),
    })
    const w2 = await open('/spaces/ENG/calendar', g)
    expect(w2.find('[data-testid="new-event"]').exists()).toBe(false)
    await w2.get('[data-testid="occurrence"]').trigger('click')
    for (let i = 0; i < 3; i++) await flushPromises()
    expect(document.body.querySelector('[data-testid="save-event"]')).toBeNull()
    expect(document.body.querySelector('[data-testid="delete-event"]')).toBeNull()
    expect(document.body.querySelector('[data-testid="cancel-occurrence"]')).toBeNull()
    expect((document.body.querySelector('[data-testid="event-title"]') as HTMLInputElement).disabled).toBe(true)
  })

  it('switches to year view, searches occurrences, and opens share/import actions', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => withEtag(space(true)),
      'GET /api/v1/spaces/ENG/calendar/occurrences': () => json([occurrence()]),
      'GET /api/v1/calendars/c1/grants': () => json([{ id: 'g1', subjectType: 'User', subjectId: 'u2', grant: 'View', createdAt: '2026-11-07T08:00:00Z' }]),
      'GET /api/v1/directory': () => json([]),
    })
    const w = await open('/spaces/ENG/calendar', f)

    const yearBtn = w.findAll('[data-testid="calendar-view"] [role="radio"], [data-testid="calendar-view"] .p-togglebutton').find((b) => b.text() === 'Year')!
    await yearBtn.trigger('click')
    for (let i = 0; i < 3; i++) await flushPromises()
    expect(w.find('[data-testid="year-view"]').exists()).toBe(true)
    expect(w.findAll('[data-testid="year-month"]').length).toBe(12)

    await w.get('[data-testid="calendar-search"]').setValue('Standup')
    for (let i = 0; i < 5; i++) await flushPromises()
    await new Promise((r) => setTimeout(r, 350))
    for (let i = 0; i < 3; i++) await flushPromises()
    expect(f.calls.some((c) => c.url.includes('/calendar/occurrences') && c.url.includes('q=Standup'))).toBe(true)

    await w.get('[data-testid="share-calendar"]').trigger('click')
    for (let i = 0; i < 3; i++) await flushPromises()
    expect(document.body.querySelector('[data-testid="share-dialog"]')).not.toBeNull()
    expect(document.body.querySelector('[data-testid="grant-list"]')!.textContent).toContain('View')

    await w.get('[data-testid="import-calendar"]').trigger('click')
    for (let i = 0; i < 3; i++) await flushPromises()
    expect(document.body.querySelector('[data-testid="import-dialog"]')).not.toBeNull()
  })

  it('opens timeline view, filters by type, and creates a personal calendar', async () => {
    const f = fakeFetch({
      ...base,
      'GET /api/v1/spaces/ENG': () => withEtag(space(true)),
      'GET /api/v1/spaces/ENG/calendar/occurrences': () => json([occurrence()]),
      'POST /api/v1/calendars': () => json({ ...board, id: 'c2', name: 'Desk', scope: 'Personal' }, 201),
    })
    const w = await open('/spaces/ENG/calendar', f)

    const timelineBtn = w.findAll('[data-testid="calendar-view"] [role="radio"], [data-testid="calendar-view"] .p-togglebutton').find((b) => b.text() === 'Timeline')!
    await timelineBtn.trigger('click')
    for (let i = 0; i < 3; i++) await flushPromises()
    expect(w.find('[data-testid="timeline-view"]').exists()).toBe(true)

    const typeSelect = w.findComponent('[data-testid="filter-type"]')
    ;(typeSelect as VueWrapper).vm.$emit('update:modelValue', 'Meeting')
    for (let i = 0; i < 5; i++) await flushPromises()
    await new Promise((r) => setTimeout(r, 350))
    for (let i = 0; i < 3; i++) await flushPromises()
    expect(f.calls.some((c) => c.url.includes('/calendar/occurrences') && c.url.includes('type=Meeting'))).toBe(true)

    await w.get('[data-testid="new-calendar"]').trigger('click')
    for (let i = 0; i < 2; i++) await flushPromises()
    expect(document.body.querySelector('[data-testid="board-dialog"]')).not.toBeNull()
    const nameInput = w.findAllComponents({ name: 'InputText' }).find((c) => c.attributes('data-testid') === 'board-name')
      ?? w.findComponent('[data-testid="board-name"]')
    ;(nameInput as VueWrapper).vm.$emit('update:modelValue', 'Desk')
    await flushPromises()
    document.body.querySelector<HTMLButtonElement>('[data-testid="save-board"]')!.click()
    for (let i = 0; i < 4; i++) await flushPromises()
    const post = f.calls.find((c) => c.method === 'POST' && c.url === '/api/v1/calendars')
    expect(post?.body).toMatchObject({ name: 'Desk', scope: 'Space', spaceKey: 'ENG' })
  })
})
