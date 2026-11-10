import { afterEach, beforeEach, describe, expect, it } from 'vitest'
import { mount } from '@vue/test-utils'
import { createPinia, setActivePinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ConfirmationService from 'primevue/confirmationservice'
import CalendarDateInput from '@/app/CalendarDateInput.vue'
import CommentThread from '@/modules/wiki/comments/CommentThread.vue'
import { createRaaviI18n } from '@/core/i18n'
import { display, isoToZonedInput, setDisplay, zonedInputToIso } from '@/core/runtime/display'
import { formatDate } from '@/core/i18n/locales'
import { isoFromParts, partsOf } from '@/modules/calendar/calendarMath'

describe('workspace display settings (time zone and calendar)', () => {
  beforeEach(() => {
    localStorage.setItem('raavi.locale', 'en')
    setActivePinia(createPinia())
  })
  afterEach(() => setDisplay(null, null))

  it('shows every instant in the workspace time zone and calendar', () => {
    setDisplay('Asia/Tehran', 'gregorian')
    expect(formatDate('2026-11-10T22:00:00Z', 'en', { timeStyle: 'short', dateStyle: undefined, hourCycle: 'h23' })).toBe('01:30') // +03:30
    setDisplay('UTC', 'gregorian')
    expect(formatDate('2026-11-10T22:00:00Z', 'en', { timeStyle: 'short', dateStyle: undefined, hourCycle: 'h23' })).toBe('22:00')
    setDisplay('UTC', 'persian')
    expect(formatDate('2026-03-21T12:00:00Z', 'en')).toMatch(/1405|Farvardin/)
    setDisplay('Not/AZone', 'nonsense')
    expect(display.timeZone).toBeNull() // an invalid zone never breaks display
  })

  it('date-time fields are the wall clock of that zone and convert both ways', () => {
    setDisplay('Asia/Tehran', 'gregorian')
    expect(zonedInputToIso('2026-07-01T09:00')).toBe('2026-07-01T05:30:00.000Z') // +03:30, no daylight saving
    expect(isoToZonedInput(zonedInputToIso('2026-11-10T14:15'))).toBe('2026-11-10T14:15')
    setDisplay('America/New_York', 'gregorian')
    expect(zonedInputToIso('2026-01-15T09:00')).toBe('2026-01-15T14:00:00.000Z')
    expect(zonedInputToIso('2026-07-15T09:00')).toBe('2026-07-15T13:00:00.000Z') // daylight saving
  })

  it('Persian dates convert to ISO and back', () => {
    expect(isoFromParts(1405, 1, 1, 'persian')).toBe('2026-03-21')
    expect(partsOf('2026-03-21', 'persian')).toEqual({ y: 1405, m: 1, d: 1 })
    expect(isoFromParts(1404, 12, 29, 'persian')).toBe('2026-03-20')
    expect(isoFromParts(1403, 12, 31, 'persian')).toBe(isoFromParts(1403, 12, 30, 'persian')) // 1403 has 30 days in Esfand: 31 is clamped
  })

  it('the date field follows the calendar: native input for Gregorian, year/month/day pickers for Persian, ISO either way', async () => {
    setDisplay('UTC', 'gregorian')
    const g = mount(CalendarDateInput, { props: { modelValue: '2026-03-21', 'data-testid': 'd' }, global: { plugins: [createRaaviI18n('en'), PrimeVue] } })
    expect(g.find('input[type="date"]').exists()).toBe(true)
    await g.find('input[type="date"]').setValue('2026-04-02')
    expect(g.emitted('update:modelValue')![0]).toEqual(['2026-04-02'])

    setDisplay('UTC', 'persian')
    const p = mount(CalendarDateInput, { props: { modelValue: '2026-03-21' }, global: { plugins: [createRaaviI18n('fa'), PrimeVue] } })
    expect(p.find('input[type="date"]').exists()).toBe(false)
    expect((p.get('[data-testid="cdi-year"]').element as HTMLSelectElement).value).toBe('1405')
    expect((p.get('[data-testid="cdi-month"]').element as HTMLSelectElement).value).toBe('1')
    await p.get('[data-testid="cdi-month"]').setValue('2') // Ordibehesht 1
    expect(p.emitted('update:modelValue')![0]).toEqual(['2026-04-21'])
    const t = mount(CalendarDateInput, { props: { modelValue: '2026-03-21T09:30', time: true }, global: { plugins: [createRaaviI18n('fa'), PrimeVue] } })
    await t.get('input[type="time"]').setValue('18:45')
    expect(t.emitted('update:modelValue')![0]).toEqual(['2026-03-21T18:45'])
  })
})

describe('comment quotes', () => {
  it('a long quote is cut with "show more", a short one is shown whole, and the quote is a button that jumps to the passage', async () => {
    const long = 'word '.repeat(120).trim()
    const thread = (exact: string) => ({ id: 't1', kind: 'Inline', status: 'Open', anchor: { exact, prefix: '', suffix: '' }, comments: [], resolvedByName: null })
    const mountIt = (exact: string) => mount(CommentThread, { props: { thread: thread(exact) as never, reactions: [], active: false, orphaned: false }, global: { plugins: [createRaaviI18n('en'), PrimeVue, ConfirmationService] } })
    const w = mountIt(long)
    const quote = w.get('.quote-text')
    expect(quote.text().length).toBeLessThan(long.length)
    expect(quote.text().endsWith('…')).toBe(true)
    await quote.trigger('click')
    expect(w.emitted('focusAnchor')![0]).toEqual(['t1'])
    await w.get('[data-testid="quote-toggle"]').trigger('click')
    expect(w.get('.quote-text').text()).toBe(long)
    const short = mountIt('just a few words')
    expect(short.find('[data-testid="quote-toggle"]').exists()).toBe(false)
    expect(short.get('.quote-text').text()).toBe('just a few words')
  })
})
