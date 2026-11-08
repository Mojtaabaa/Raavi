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
