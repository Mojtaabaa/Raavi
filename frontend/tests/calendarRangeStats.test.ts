import { describe, expect, it } from 'vitest'
import type { Occurrence } from '@/core/api/calendar'
import { buildOccurrencesByDay, computeCalendarRangeStats } from '@/modules/calendar/calendarRangeStats'

function occ(over: Partial<Occurrence> & Pick<Occurrence, 'startDate' | 'endDate'>): Occurrence {
  const { startDate, endDate, ...rest } = over
  return {
    eventId: 'e1',
    calendarId: 'c1',
    spaceKey: 'ENG',
    spaceName: 'Engineering',
    title: 'T',
    location: null,
    allDay: false,
    startDate,
    startTime: '09:00',
    endDate,
    endTime: '10:00',
    timeZone: 'Asia/Tehran',
    startUtc: null,
    endUtc: null,
    isRecurring: false,
    occurrenceDate: startDate,
    canEdit: true,
    sourceKind: 'Raavi',
    externalUrl: null,
    freeBusyOnly: false,
    type: 'Event',
    color: null,
    ...rest,
  }
}

describe('buildOccurrencesByDay', () => {
  it('expands multi-day events across each in-range day', () => {
    const list = [occ({ startDate: '2026-07-01', endDate: '2026-07-03', title: 'Retreat' })]
    const map = buildOccurrencesByDay(list, '2026-07-01', '2026-07-07')
    expect(map.get('2026-07-01')).toHaveLength(1)
    expect(map.get('2026-07-02')).toHaveLength(1)
    expect(map.get('2026-07-03')).toHaveLength(1)
    expect(map.get('2026-07-04')).toBeUndefined()
  })

  it('clips events to the visible range', () => {
    const list = [occ({ startDate: '2026-06-28', endDate: '2026-07-05' })]
    const map = buildOccurrencesByDay(list, '2026-07-01', '2026-07-07')
    expect(map.get('2026-06-30')).toBeUndefined()
    expect(map.get('2026-07-01')).toHaveLength(1)
    expect(map.get('2026-07-05')).toHaveLength(1)
  })
})

describe('computeCalendarRangeStats', () => {
  it('counts sources, all-day, recurring, and busiest day from API-shaped data', () => {
    const list: Occurrence[] = [
      occ({ startDate: '2026-07-06', endDate: '2026-07-06', allDay: true, isRecurring: true, sourceKind: 'Raavi' }),
      occ({ startDate: '2026-07-06', endDate: '2026-07-06', sourceKind: 'Jira', eventId: null, title: 'J-1' }),
      occ({ startDate: '2026-07-06', endDate: '2026-07-06', sourceKind: 'Jira', eventId: null, title: 'J-2' }),
      occ({ startDate: '2026-07-07', endDate: '2026-07-07', sourceKind: 'Raavi', eventId: 'e2' }),
    ]
    const s = computeCalendarRangeStats(list, '2026-07-06', '2026-07-10', '2026-07-06')
    expect(s.total).toBe(4)
    expect(s.allDay).toBe(1)
    expect(s.timed).toBe(3)
    expect(s.recurring).toBe(1)
    expect(s.raavi).toBe(2)
    expect(s.jira).toBe(2)
    expect(s.todayCount).toBe(3)
    expect(s.busiestIso).toBe('2026-07-06')
    expect(s.busiestN).toBe(3)
    expect(s.bars.find((b) => b.iso === '2026-07-06')?.n).toBe(3)
  })

  it('reflects filtered lists only (no hidden occurrences)', () => {
    const visible = [occ({ startDate: '2026-07-01', endDate: '2026-07-01', sourceKind: 'Raavi' })]
    const s = computeCalendarRangeStats(visible, '2026-07-01', '2026-07-07', '2026-07-01')
    expect(s.total).toBe(1)
    expect(s.jira).toBe(0)
  })

  it('reports zero today count when today is outside the visible range', () => {
    const list = [occ({ startDate: '2026-06-01', endDate: '2026-06-01' })]
    const s = computeCalendarRangeStats(list, '2026-06-01', '2026-06-07', '2026-07-01')
    expect(s.total).toBe(1)
    expect(s.todayCount).toBe(0)
  })
})
