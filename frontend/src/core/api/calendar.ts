import { api, apiRequest } from './http'
import { prefixed } from '@/core/runtime/base'

export type Frequency = 'None' | 'Daily' | 'Weekly' | 'Monthly' | 'Yearly'
export type RecurrenceCalendar = 'Gregorian' | 'Persian'
export type EditScope = 'EntireSeries' | 'ThisOccurrence' | 'ThisAndFollowing'
export type CalendarViewMode = 'month' | 'day' | 'week' | 'workweek' | 'agenda' | 'year' | 'timeline'

export interface Recurrence {
  frequency: Frequency
  interval: number
  weekDays: number[]
  calendar: RecurrenceCalendar
  until: string | null
  count: number | null
  monthWeek?: number
}

export interface CalendarAttendee {
  userId: string
  displayName: string | null
  response: string
  optional: boolean
  invitedAt: string
  respondedAt: string | null
}

export interface CalendarBoard {
  id: string
  scope: string
  spaceId: string | null
  ownerUserId: string | null
  name: string
  description: string | null
  color: string
  icon: string | null
  isDefault: boolean
  archivedAt: string | null
  revision: number
}

export interface CalendarReminder {
  id: string
  minutesBefore: number
  inApp: boolean
  email: boolean
}

export interface CalendarEvent {
  id: string
  calendarId: string
  spaceKey: string | null
  spaceName: string | null
  title: string
  description: string | null
  location: string | null
  allDay: boolean
  startDate: string
  startTime: string | null
  endDate: string
  endTime: string | null
  timeZone: string
  recurrence: Recurrence | null
  excludedDates: string[]
  type: string
  status: string
  priority: string
  color: string | null
  onlineUrl: string | null
  isPrivate: boolean
  createdByName: string | null
  createdAt: string
  revision: number
  canEdit: boolean
  reminders: CalendarReminder[]
  attendees: CalendarAttendee[]
  linkedPageId: string | null
  linkedWhiteboardId: string | null
  myResponse: string | null
}

export interface Occurrence {
  eventId: string | null
  calendarId: string | null
  spaceKey: string | null
  spaceName: string | null
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
  occurrenceDate: string
  canEdit: boolean
  sourceKind: string
  externalUrl: string | null
  freeBusyOnly: boolean
  type: string | null
  color: string | null
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
  recurrence: { frequency: Frequency; interval: number; weekDays: number[]; calendar: RecurrenceCalendar; until: string | null; count: number | null; monthWeek?: number } | null
  type?: string
  status?: string
  priority?: string
  color?: string | null
  onlineUrl?: string | null
  isPrivate?: boolean
  editScope?: EditScope
  occurrenceDate?: string | null
  reminders?: { minutesBefore: number; inApp: boolean; email: boolean }[]
  attendees?: { userId: string; optional?: boolean }[]
  linkedPageId?: string | null
  linkedWhiteboardId?: string | null
}

export interface CalendarGrant {
  id: string
  subjectType: string
  subjectId: string
  grant: string
  createdAt: string
}

export interface CalendarPrefs {
  browserAlerts: boolean
  ttsEnabled: boolean
  ttsLocale: string
  ttsRate: number
  hidePrivateTitlesInTts: boolean
  quietStartMinutes: number | null
  quietEndMinutes: number | null
  timeZone: string
}

export interface CalendarImportResult {
  imported: number
  skipped: number
  warnings: string[]
}

const enc = encodeURIComponent

export const calendarApi = {
  boards: (spaceKey: string | null, includeArchived = false) =>
    api<CalendarBoard[]>('GET', `${spaceKey ? `/spaces/${enc(spaceKey)}` : ''}/calendars?includeArchived=${includeArchived}`),
  createBoard: (body: { scope: string; spaceKey?: string; name: string; description?: string; color?: string }) =>
    api<CalendarBoard>('POST', '/calendars', body),
  updateBoard: (id: string, body: { name?: string; description?: string; color?: string; icon?: string }) =>
    api<CalendarBoard>('PUT', `/calendars/${enc(id)}`, body),
  archiveBoard: (id: string) => api<CalendarBoard>('POST', `/calendars/${enc(id)}/archive`, {}),
  restoreBoard: (id: string) => api<CalendarBoard>('POST', `/calendars/${enc(id)}/restore`, {}),
  purgeBoard: (id: string, confirmName: string) => api<void>('POST', `/calendars/${enc(id)}/purge`, { confirmName }),
  importIcs: (calendarId: string, ics: string) => api<CalendarImportResult>('POST', `/calendars/${enc(calendarId)}/import`, { ics }),
  occurrences: (spaceKey: string | null, from: string, to: string, calendarIds?: string[], q?: string, type?: string, sourceKind?: string) => {
    const ids = calendarIds?.length ? `&calendarIds=${calendarIds.map(enc).join(',')}` : ''
    const query = q?.trim() ? `&q=${enc(q.trim())}` : ''
    const typeQ = type?.trim() ? `&type=${enc(type.trim())}` : ''
    const sourceQ = sourceKind?.trim() ? `&sourceKind=${enc(sourceKind.trim())}` : ''
    return api<Occurrence[]>('GET', `${spaceKey ? `/spaces/${enc(spaceKey)}` : ''}/calendar/occurrences?from=${from}&to=${to}${ids}${query}${typeQ}${sourceQ}`)
  },
  event: (id: string) => api<CalendarEvent>('GET', `/calendar/events/${enc(id)}`),
  create: (spaceKey: string, body: CalendarEventInput) => api<CalendarEvent>('POST', `/spaces/${enc(spaceKey)}/calendar/events`, body),
  createOnCalendar: (calendarId: string, body: CalendarEventInput) => api<CalendarEvent>('POST', `/calendars/${enc(calendarId)}/events`, body),
  update: (event: CalendarEvent, body: CalendarEventInput) =>
    apiRequest<CalendarEvent>('PUT', `/calendar/events/${enc(event.id)}`, body, { ifMatch: `"r${event.revision}"` }).then((r) => r.data),
  move: (id: string, targetCalendarId: string) => api<CalendarEvent>('POST', `/calendar/events/${enc(id)}/move`, { targetCalendarId }),
  rsvp: (id: string, response: string) => api<CalendarEvent>('POST', `/calendar/events/${enc(id)}/rsvp`, { response }),
  cancelOccurrence: (id: string, date: string) => api<CalendarEvent>('POST', `/calendar/events/${enc(id)}/exclusions`, { date }),
  remove: (id: string) => api<void>('DELETE', `/calendar/events/${enc(id)}`),
  exportUrl: (spaceKey: string) => prefixed(`/api/v1/spaces/${enc(spaceKey)}/calendar/export`),
  grants: (calendarId: string) => api<CalendarGrant[]>('GET', `/calendars/${enc(calendarId)}/grants`),
  putGrant: (calendarId: string, body: { subjectType: string; subjectId: string; grant: string }) =>
    api<CalendarGrant>('PUT', `/calendars/${enc(calendarId)}/grants`, body),
  deleteGrant: (calendarId: string, grantId: string) => api<void>('DELETE', `/calendars/${enc(calendarId)}/grants/${enc(grantId)}`),
  prefs: () => api<CalendarPrefs>('GET', '/calendar/preferences'),
  updatePrefs: (body: Partial<CalendarPrefs>) => api<CalendarPrefs>('PUT', '/calendar/preferences', body),
}
