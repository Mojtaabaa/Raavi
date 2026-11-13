/**
 * Calendar arithmetic on plain ISO dates (yyyy-MM-dd), independent of the browser's time zone: every date is handled as UTC noon.
 * Persian (Jalali) months are derived from Intl, so there is no second calendar implementation to keep correct.
 */
export type CalendarSystem = 'persian' | 'gregory'

export interface DayParts {
  y: number
  m: number
  d: number
}

const MS_PER_DAY = 86_400_000

const at = (iso: string): Date => new Date(`${iso}T12:00:00Z`)

export function toIso(date: Date): string {
  return date.toISOString().slice(0, 10)
}

export function addDays(iso: string, n: number): string {
  return toIso(new Date(at(iso).getTime() + n * MS_PER_DAY))
}

export function daysBetween(fromIso: string, toIso_: string): number {
  return Math.round((at(toIso_).getTime() - at(fromIso).getTime()) / MS_PER_DAY)
}

/** 0 = Sunday … 6 = Saturday. */
export function weekdayOf(iso: string): number {
  return at(iso).getUTCDay()
}

const partFormatters = new Map<CalendarSystem, Intl.DateTimeFormat>()

export function partsOf(iso: string, cal: CalendarSystem): DayParts {
  let f = partFormatters.get(cal)
  if (!f) {
    f = new Intl.DateTimeFormat(`en-u-ca-${cal}-nu-latn`, { timeZone: 'UTC', year: 'numeric', month: 'numeric', day: 'numeric' })
    partFormatters.set(cal, f)
  }
  const parts = f.formatToParts(at(iso))
  const get = (type: string) => Number(parts.find((p) => p.type === type)?.value)
  return { y: get('year'), m: get('month'), d: get('day') }
}

export function firstOfMonth(iso: string, cal: CalendarSystem): string {
  return addDays(iso, -(partsOf(iso, cal).d - 1))
}

export function daysInMonth(iso: string, cal: CalendarSystem): number {
  const first = firstOfMonth(iso, cal)
  const m = partsOf(first, cal).m
  let n = 28
  while (partsOf(addDays(first, n), cal).m === m) n++
  return n
}

/** First day of the month `delta` months away from the month containing `iso`. */
export function shiftMonth(iso: string, cal: CalendarSystem, delta: number): string {
  let first = firstOfMonth(iso, cal)
  for (let i = 0; i < Math.abs(delta); i++) {
    first = delta > 0 ? addDays(first, daysInMonth(first, cal)) : firstOfMonth(addDays(first, -1), cal)
  }
  return first
}

export interface GridDay {
  iso: string
  inMonth: boolean
  day: number
}

/** Whole weeks covering the month, starting on `weekStart` (0 = Sunday … 6 = Saturday). */
export function monthGrid(iso: string, cal: CalendarSystem, weekStart: number): GridDay[] {
  const first = firstOfMonth(iso, cal)
  const month = partsOf(first, cal).m
  const lead = (weekdayOf(first) - weekStart + 7) % 7
  const rows = Math.ceil((lead + daysInMonth(first, cal)) / 7)
  const start = addDays(first, -lead)
  return Array.from({ length: rows * 7 }, (_, i) => {
    const d = addDays(start, i)
    const p = partsOf(d, cal)
    return { iso: d, inMonth: p.m === month, day: p.d }
  })
}

export function monthLabel(iso: string, cal: CalendarSystem, intlTag: string): string {
  return new Intl.DateTimeFormat(`${intlTag}-u-ca-${cal}`, { timeZone: 'UTC', month: 'long', year: 'numeric' }).format(at(iso))
}

export function weekdayLabel(iso: string, intlTag: string, style: 'short' | 'long' = 'short'): string {
  return new Intl.DateTimeFormat(intlTag, { timeZone: 'UTC', weekday: style }).format(at(iso))
}

export function dateLabel(iso: string, cal: CalendarSystem, intlTag: string): string {
  return new Intl.DateTimeFormat(`${intlTag}-u-ca-${cal}`, { timeZone: 'UTC', dateStyle: 'medium' }).format(at(iso))
}

/** Inclusive week [start, end] containing `iso`, starting on `weekStart` (0=Sun…6=Sat). */
export function weekRange(iso: string, weekStart: number): { from: string; to: string } {
  const lead = (weekdayOf(iso) - weekStart + 7) % 7
  const from = addDays(iso, -lead)
  return { from, to: addDays(from, 6) }
}

/** Monday–Friday work week containing `iso` (Gregorian weekdays). */
export function workWeekRange(iso: string): { from: string; to: string } {
  const day = weekdayOf(iso) // 0 Sun
  const toMonday = day === 0 ? -6 : 1 - day
  const from = addDays(iso, toMonday)
  return { from, to: addDays(from, 4) }
}

export function shiftYear(iso: string, cal: CalendarSystem, delta: number): string {
  const p = partsOf(iso, cal)
  return isoFromParts(p.y + delta, 1, 1, cal)
}

export function yearLabel(iso: string, cal: CalendarSystem, intlTag: string): string {
  return new Intl.DateTimeFormat(`${intlTag}-u-ca-${cal}`, { timeZone: 'UTC', year: 'numeric' }).format(at(iso))
}

/** First day of each month in the calendar year containing `iso`. */
export function yearMonths(iso: string, cal: CalendarSystem): string[] {
  const y = partsOf(iso, cal).y
  return Array.from({ length: 12 }, (_, i) => isoFromParts(y, i + 1, 1, cal))
}

export function shiftByView(iso: string, cal: CalendarSystem, view: 'month' | 'day' | 'week' | 'workweek' | 'agenda' | 'year' | 'timeline', delta: number): string {
  if (view === 'year') return shiftYear(iso, cal, delta)
  if (view === 'month') return shiftMonth(iso, cal, delta)
  if (view === 'day' || view === 'agenda' || view === 'timeline') return addDays(iso, delta)
  if (view === 'workweek') return addDays(iso, delta * 7)
  return addDays(iso, delta * 7)
}

/** Minutes from midnight for `HH:mm` / `HH:mm:ss`; null when missing. */
export function minutesOf(time: string | null | undefined): number | null {
  if (!time) return null
  const [h, m] = time.split(':').map(Number)
  if (!Number.isFinite(h) || !Number.isFinite(m)) return null
  return h! * 60 + m!
}

export const TIMELINE_HOURS = Array.from({ length: 24 }, (_, h) => h)

/** The ISO date of year/month/day in the given calendar (day is clamped to the month). Found by bisection over Intl, like the rest of this file. */
export function isoFromParts(y: number, m: number, d: number, cal: CalendarSystem): string {
  if (cal === 'gregory') return `${String(y).padStart(4, '0')}-${String(m).padStart(2, '0')}-${String(d).padStart(2, '0')}`
  const key = (p: DayParts) => p.y * 10_000 + p.m * 100 + p.d
  const target = y * 10_000 + m * 100 + 1
  let lo = `${y + 621}-01-01`
  let hi = `${y + 622}-12-31`
  while (daysBetween(lo, hi) > 0) {
    const mid = addDays(lo, Math.floor(daysBetween(lo, hi) / 2))
    if (key(partsOf(mid, cal)) < target) lo = addDays(mid, 1)
    else hi = mid
  }
  const first = lo
  return addDays(first, Math.min(d, daysInMonth(first, cal)) - 1)
}
