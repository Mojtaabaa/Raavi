import { reactive } from 'vue'

/**
 * How dates and times are shown everywhere (ADR-053): one time zone and one calendar for the whole workspace, set by its administrator
 * (Administration → Display). Stored instants stay UTC; only the display and the entry of date/time fields use these values.
 */
export type DisplayCalendar = 'persian' | 'gregory'

export const display = reactive<{ timeZone: string | null; calendar: DisplayCalendar | null }>({ timeZone: null, calendar: null })

export function setDisplay(timeZone: string | null | undefined, calendar: string | null | undefined): void {
  display.timeZone = timeZone && isValidTimeZone(timeZone) ? timeZone : null
  display.calendar = calendar === 'persian' ? 'persian' : calendar === 'gregorian' || calendar === 'gregory' ? 'gregory' : null
}

export function isValidTimeZone(tz: string): boolean {
  try {
    new Intl.DateTimeFormat('en', { timeZone: tz })
    return true
  } catch {
    return false
  }
}

export function timeZoneList(): string[] {
  const intl = Intl as unknown as { supportedValuesOf?: (k: string) => string[] }
  const list = intl.supportedValuesOf?.('timeZone') ?? ['Asia/Tehran', 'UTC', 'Europe/London', 'Europe/Berlin', 'America/New_York', 'Asia/Dubai', 'Asia/Istanbul']
  return list.includes('UTC') ? list : ['UTC', ...list]
}

/** The zone used for display: the workspace's, else the browser's. */
export const activeTimeZone = (): string => display.timeZone ?? Intl.DateTimeFormat().resolvedOptions().timeZone

function partsIn(date: Date, tz: string) {
  const f = new Intl.DateTimeFormat('en-u-nu-latn', { timeZone: tz, hourCycle: 'h23', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' })
  const p = Object.fromEntries(f.formatToParts(date).map((x) => [x.type, x.value]))
  return { y: +p.year!, m: +p.month!, d: +p.day!, h: +p.hour!, mi: +p.minute!, s: +p.second! }
}

/** An instant (ISO, UTC) → "yyyy-MM-ddTHH:mm" as the wall clock of the display zone shows it (for date-time fields). */
export function isoToZonedInput(iso: string, tz: string = activeTimeZone()): string {
  const p = partsIn(new Date(iso), tz)
  const z = (n: number, w = 2) => String(n).padStart(w, '0')
  return `${z(p.y, 4)}-${z(p.m)}-${z(p.d)}T${z(p.h)}:${z(p.mi)}`
}

/** "yyyy-MM-ddTHH:mm" typed as wall-clock time of the display zone → the instant (ISO, UTC). */
export function zonedInputToIso(local: string, tz: string = activeTimeZone()): string {
  const [date, time = '00:00'] = local.split('T')
  const [y, m, d] = date!.split('-').map(Number)
  const [h, mi] = time.split(':').map(Number)
  const asUtc = Date.UTC(y!, m! - 1, d!, h!, mi!)
  let guess = asUtc
  for (let i = 0; i < 3; i++) {
    const p = partsIn(new Date(guess), tz)
    const shown = Date.UTC(p.y, p.m - 1, p.d, p.h, p.mi)
    guess += asUtc - shown // move by the difference between what we wanted and what the zone shows
  }
  return new Date(guess).toISOString()
}
