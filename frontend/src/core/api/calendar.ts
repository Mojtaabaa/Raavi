import { api, apiRequest } from './http'
import { prefixed } from '@/core/runtime/base'

export type Frequency = 'None' | 'Daily' | 'Weekly' | 'Monthly' | 'Yearly'
export type RecurrenceCalendar = 'Gregorian' | 'Persian'

export interface Recurrence {
  frequency: Frequency
  interval: number
  /** 0 = Sunday … 6 = Saturday. */
  weekDays: number[]
  calendar: RecurrenceCalendar
  /** yyyy-MM-dd; mutually exclusive with `count`. */
  until: string | null
  count: number | null
}

export interface CalendarEvent {
  id: string
  spaceKey: string
  spaceName: string
  title: string
  description: string | null
  location: string | null
  allDay: boolean
  startDate: string
  startTime: string | null
  /** Last day of the event (inclusive). */
  endDate: string
  endTime: string | null
  timeZone: string
  recurrence: Recurrence | null
  excludedDates: string[]
  createdByName: string | null
  createdAt: string
  revision: number
  canEdit: boolean
}

export interface Occurrence {
  eventId: string
  spaceKey: string
  spaceName: string
  title: string
  location: string | null
  allDay: boolean
  startDate: string
  startTime: string | null
  endDate: string
  endTime: string | null
  timeZone: string
  startUtc: string | null
  endUtc: string | null
  isRecurring: boolean
  /** The series start date this occurrence came from (used to cancel it). */
  occurrenceDate: string
  canEdit: boolean
}

export interface CalendarEventInput {
  title: string
  description: string | null
  location: string | null
  allDay: boolean
  startDate: string
  startTime: string | null
  endDate: string
  endTime: string | null
  timeZone: string
  recurrence: { frequency: Frequency; interval: number; weekDays: number[]; calendar: RecurrenceCalendar; until: string | null; count: number | null } | null
}

const enc = encodeURIComponent

export const calendarApi = {
  occurrences: (spaceKey: string | null, from: string, to: string) =>
    api<Occurrence[]>('GET', `${spaceKey ? `/spaces/${enc(spaceKey)}` : ''}/calendar/occurrences?from=${from}&to=${to}`),
  event: (id: string) => api<CalendarEvent>('GET', `/calendar/events/${enc(id)}`),
  create: (spaceKey: string, body: CalendarEventInput) => api<CalendarEvent>('POST', `/spaces/${enc(spaceKey)}/calendar/events`, body),
  update: (event: CalendarEvent, body: CalendarEventInput) =>
    apiRequest<CalendarEvent>('PUT', `/calendar/events/${enc(event.id)}`, body, { ifMatch: `"r${event.revision}"` }).then((r) => r.data),
  cancelOccurrence: (id: string, date: string) => api<CalendarEvent>('POST', `/calendar/events/${enc(id)}/exclusions`, { date }),
  remove: (id: string) => api<void>('DELETE', `/calendar/events/${enc(id)}`),
  exportUrl: (spaceKey: string) => prefixed(`/api/v1/spaces/${enc(spaceKey)}/calendar/export`),
}
