/** Visible event title in grid cells: wrap in CSS, hard cap for very long strings. */
export const CALENDAR_EVENT_LABEL_MAX = 56

export function calendarEventLabel(title: string | null | undefined, max = CALENDAR_EVENT_LABEL_MAX): string {
  const raw = (title ?? '').trim()
  if (!raw) return ''
  if (max < 2) return raw.slice(0, 1)
  if (raw.length <= max) return raw
  return `${raw.slice(0, max - 1)}…`
}
