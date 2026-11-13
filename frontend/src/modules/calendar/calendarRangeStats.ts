import type { Occurrence } from '@/core/api/calendar'
import { addDays } from './calendarMath'

/** Map each ISO date in [from, to] to occurrences that span that day (multi-day expands per day). */
export function buildOccurrencesByDay(occurrences: Occurrence[], from: string, to: string): Map<string, Occurrence[]> {
  const map = new Map<string, Occurrence[]>()
  for (const o of occurrences) {
    const start = o.startDate < from ? from : o.startDate
    for (let d = start; d <= o.endDate && d <= to; d = addDays(d, 1)) {
      const list = map.get(d) ?? []
      list.push(o)
      map.set(d, list)
    }
  }
  return map
}

export type CalendarRangeStats = {
  total: number
  todayCount: number
  allDay: number
  timed: number
  recurring: number
  raavi: number
  jira: number
  busiestIso: string
  busiestN: number
  bars: { iso: string; n: number; pct: number }[]
}

/** KPI strip derived only from occurrences already returned by the API (filters/ACL applied server-side). */
export function computeCalendarRangeStats(
  occurrences: Occurrence[],
  from: string,
  to: string,
  todayIso: string,
): CalendarRangeStats {
  const byDay = buildOccurrencesByDay(occurrences, from, to)
  const todayCount = (byDay.get(todayIso) ?? []).length
  let allDay = 0
  let recurring = 0
  let raavi = 0
  let jira = 0
  for (const o of occurrences) {
    if (o.allDay) allDay++
    if (o.isRecurring) recurring++
    if (o.sourceKind === 'Jira') jira++
    else if (o.sourceKind === 'Raavi') raavi++
  }
  let busiestIso = ''
  let busiestN = 0
  for (const [iso, arr] of byDay) {
    if (arr.length > busiestN) {
      busiestN = arr.length
      busiestIso = iso
    }
  }
  const bars: { iso: string; n: number; pct: number }[] = []
  const maxN = busiestN || 1
  for (let d = from; d <= to; d = addDays(d, 1)) {
    const n = (byDay.get(d) ?? []).length
    bars.push({ iso: d, n, pct: Math.round((100 * n) / maxN) })
  }
  return {
    total: occurrences.length,
    todayCount,
    allDay,
    timed: occurrences.length - allDay,
    recurring,
    raavi,
    jira,
    busiestIso,
    busiestN,
    bars,
  }
}
