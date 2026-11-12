<script setup lang="ts">
import CalendarDateInput from '@/app/CalendarDateInput.vue'
import PrincipalPicker from '@/modules/wiki/components/PrincipalPicker.vue'
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Menu from 'primevue/menu'
import Message from 'primevue/message'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import type { MenuItem } from 'primevue/menuitem'
import {
  calendarApi,
  type CalendarBoard,
  type CalendarEvent,
  type CalendarEventInput,
  type CalendarGrant,
  type CalendarPrefs,
  type CalendarViewMode,
  type EditScope,
  type Frequency,
  type Occurrence,
  type RecurrenceCalendar,
} from '@/core/api/calendar'
import { connection } from '@/core/api/connection'
import { wikiApi, type Space } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { LOCALES } from '@/core/i18n/locales'
import { useAuthStore } from '@/stores/auth'
import { useHostStore } from '@/stores/host'
import { useLocaleStore } from '@/stores/locale'
import {
  addDays,
  dateLabel,
  daysBetween,
  minutesOf,
  monthGrid,
  monthLabel,
  partsOf,
  shiftByView,
  TIMELINE_HOURS,
  toIso,
  weekdayLabel,
  weekdayOf,
  weekRange,
  workWeekRange,
  yearLabel,
  yearMonths,
  type CalendarSystem,
} from './calendarMath'

const { t, te } = useI18n()
const route = useRoute()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()
const host = useHostStore()
const auth = useAuthStore()

const spaceKey = computed(() => (typeof route.params.key === 'string' ? route.params.key : null))
const intlTag = computed(() => LOCALES[locale.locale].intlTag)
const space = ref<Space | null>(null)
const calendar = ref<CalendarSystem>(host.tenant?.calendar === 'gregorian' ? 'gregory' : host.tenant?.calendar === 'persian' ? 'persian' : LOCALES[locale.locale].defaultCalendar)
const calendars = computed(() => [
  { value: 'persian', label: t('calendar.jalali') },
  { value: 'gregory', label: t('calendar.gregorian') },
])
const viewMode = ref<CalendarViewMode>('month')
const viewOptions = computed(() => [
  { value: 'month', label: t('calendar.viewMonth') },
  { value: 'day', label: t('calendar.viewDay') },
  { value: 'week', label: t('calendar.viewWeek') },
  { value: 'workweek', label: t('calendar.viewWorkWeek') },
  { value: 'agenda', label: t('calendar.viewAgenda') },
  { value: 'year', label: t('calendar.viewYear') },
  { value: 'timeline', label: t('calendar.viewTimeline') },
])
const anchor = ref(toIso(new Date()))
const occurrences = ref<Occurrence[]>([])
const boards = ref<CalendarBoard[]>([])
const archivedBoards = ref<CalendarBoard[]>([])
const selectedIds = ref<string[]>([])
const loading = ref(true)
const prefs = ref<CalendarPrefs | null>(null)
const prefsOpen = ref(false)
const editScope = ref<EditScope>('EntireSeries')
const scopePrompt = ref(false)
const searchQ = ref('')
const filterType = ref('')
const filterSource = ref('')
const offline = computed(() => connection.down.value)
const shareBoard = ref<CalendarBoard | null>(null)
const grants = ref<CalendarGrant[]>([])
const grantLevel = ref('View')
const importBoard = ref<CalendarBoard | null>(null)
const importText = ref('')
const importing = ref(false)
const showArchived = ref(false)
const boardDialog = ref<'new' | CalendarBoard | null>(null)
const boardForm = ref({ name: '', color: '#3b82f6', description: '', scope: 'Personal' })
const favoriteIds = ref<string[]>([])
const contextOcc = ref<Occurrence | null>(null)
const contextMenu = ref<InstanceType<typeof Menu> | null>(null)
const dragging = ref<Occurrence | null>(null)
const nowMinutes = ref(new Date().getHours() * 60 + new Date().getMinutes())

const weekStart = computed(() => (calendar.value === 'persian' || locale.locale === 'fa' ? 6 : 0))
const grid = computed(() => monthGrid(anchor.value, calendar.value, weekStart.value))
const yearMonthStarts = computed(() => yearMonths(anchor.value, calendar.value))
const range = computed(() => {
  if (viewMode.value === 'year') {
    const months = yearMonthStarts.value
    const first = monthGrid(months[0]!, calendar.value, weekStart.value)
    const last = monthGrid(months[11]!, calendar.value, weekStart.value)
    return { from: first[0]!.iso, to: last[last.length - 1]!.iso }
  }
  if (viewMode.value === 'month') {
    const g = grid.value
    return { from: g[0]!.iso, to: g[g.length - 1]!.iso }
  }
  if (viewMode.value === 'day' || viewMode.value === 'timeline' || viewMode.value === 'agenda') {
    return { from: anchor.value, to: viewMode.value === 'agenda' ? addDays(anchor.value, 13) : anchor.value }
  }
  if (viewMode.value === 'workweek') return workWeekRange(anchor.value)
  return weekRange(anchor.value, weekStart.value)
})
const title = computed(() => {
  if (viewMode.value === 'year') return yearLabel(anchor.value, calendar.value, intlTag.value)
  if (viewMode.value === 'month') return monthLabel(anchor.value, calendar.value, intlTag.value)
  if (viewMode.value === 'day' || viewMode.value === 'timeline') return dateLabel(anchor.value, calendar.value, intlTag.value)
  return `${dateLabel(range.value.from, calendar.value, intlTag.value)} – ${dateLabel(range.value.to, calendar.value, intlTag.value)}`
})
const weekdays = computed(() => grid.value.slice(0, 7).map((d) => ({ iso: d.iso, label: weekdayLabel(d.iso, intlTag.value), long: weekdayLabel(d.iso, intlTag.value, 'long') })))
const today = toIso(new Date())
const rtl = computed(() => LOCALES[locale.locale].direction === 'rtl')
const num = (n: number) => new Intl.NumberFormat(intlTag.value).format(n)
const canCreate = computed(() => !offline.value && !!spaceKey.value && !!space.value?.rights.canCreate)

const storageKey = computed(() => `raavi.calendar.selected.${spaceKey.value ?? 'all'}`)
const favKey = computed(() => `raavi.calendar.favorites.${spaceKey.value ?? 'all'}`)
const cacheKey = computed(() => `raavi.calendar.cache.${spaceKey.value ?? 'all'}.${range.value.from}.${range.value.to}`)
const grantChoices = computed(() =>
  (['FreeBusy', 'View', 'Create', 'EditOwn', 'EditAll', 'Manage'] as const).map((v) => ({ value: v, label: t(`calendar.grant${v}`) })),
)
const typeFilterChoices = computed(() => [
  { value: '', label: t('calendar.filterAllTypes') },
  ...['Event', 'Meeting', 'Reminder', 'Milestone', 'Holiday', 'FocusTime'].map((v) => ({ value: v, label: v })),
])
const sourceFilterChoices = computed(() => [
  { value: '', label: t('calendar.filterAllSources') },
  { value: 'Raavi', label: t('calendar.sourceRaavi') },
  { value: 'Jira', label: t('calendar.sourceJira') },
])
const scopeCreateChoices = computed(() => {
  const list = [{ value: 'Personal', label: t('calendar.scopePersonal') }]
  if (spaceKey.value) list.push({ value: 'Space', label: t('calendar.scopeSpace') })
  if (auth.me?.isTenantAdministrator) list.push({ value: 'Organization', label: t('calendar.scopeOrg') })
  return list
})
const sortedBoards = computed(() => {
  const fav = new Set(favoriteIds.value)
  return [...boards.value].sort((a, b) => Number(fav.has(b.id)) - Number(fav.has(a.id)) || a.name.localeCompare(b.name))
})
const canManageBoards = computed(() => !offline.value && (!!spaceKey.value ? !!space.value?.rights.canCreate : true))
const recurrencePresets = computed(() => [
  { id: 'none', label: t('calendar.presetNone') },
  { id: 'standup', label: t('calendar.presetStandup') },
  { id: 'weekdays', label: t('calendar.presetWeekdays') },
  { id: 'biweekly', label: t('calendar.presetBiweekly') },
])

const byDay = computed(() => {
  const map = new Map<string, Occurrence[]>()
  for (const o of occurrences.value) {
    for (let d = o.startDate < range.value.from ? range.value.from : o.startDate; d <= o.endDate && d <= range.value.to; d = addDays(d, 1)) {
      const list = map.get(d) ?? []
      list.push(o)
      map.set(d, list)
    }
  }
  return map
})

const agendaList = computed(() =>
  [...occurrences.value].sort((a, b) => a.startDate.localeCompare(b.startDate) || (a.startTime ?? '').localeCompare(b.startTime ?? '')),
)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const timeLabel = (o: Occurrence) => (o.freeBusyOnly ? t('calendar.freeBusy') : o.allDay ? t('calendar.allDay') : (o.startTime ?? ''))
const occKey = (o: Occurrence) => `${o.sourceKind}-${o.eventId ?? o.externalUrl}-${o.occurrenceDate}`

function loadFavorites() {
  try {
    const raw = localStorage.getItem(favKey.value)
    favoriteIds.value = raw ? (JSON.parse(raw) as string[]) : []
  } catch {
    favoriteIds.value = []
  }
}

function toggleFavorite(id: string) {
  const set = new Set(favoriteIds.value)
  if (set.has(id)) set.delete(id)
  else set.add(id)
  favoriteIds.value = [...set]
  localStorage.setItem(favKey.value, JSON.stringify(favoriteIds.value))
}

async function loadBoards() {
  loadFavorites()
  const [active, archived] = await Promise.all([
    calendarApi.boards(spaceKey.value, false),
    showArchived.value ? calendarApi.boards(spaceKey.value, true) : Promise.resolve([] as CalendarBoard[]),
  ])
  boards.value = active
  archivedBoards.value = archived.filter((b) => b.archivedAt)
  const saved = localStorage.getItem(storageKey.value)
  let next: string[]
  if (saved) {
    try {
      const ids = JSON.parse(saved) as string[]
      next = ids.filter((id) => boards.value.some((b) => b.id === id))
    } catch {
      next = boards.value.map((b) => b.id)
    }
  } else {
    next = boards.value.map((b) => b.id)
  }
  const same = next.length === selectedIds.value.length && next.every((id) => selectedIds.value.includes(id))
  if (!same) selectedIds.value = next
}

function toggleBoard(id: string, on: boolean) {
  const set = new Set(selectedIds.value)
  if (on) set.add(id)
  else set.delete(id)
  selectedIds.value = [...set]
  localStorage.setItem(storageKey.value, JSON.stringify(selectedIds.value))
  void loadOccurrences()
}

function writeCache(list: Occurrence[]) {
  try {
    localStorage.setItem(cacheKey.value, JSON.stringify({ at: Date.now(), occurrences: list }))
  } catch {
    /* quota */
  }
}

function readCache(): Occurrence[] | null {
  try {
    const raw = localStorage.getItem(cacheKey.value)
    if (!raw) return null
    const parsed = JSON.parse(raw) as { occurrences?: Occurrence[] }
    return Array.isArray(parsed.occurrences) ? parsed.occurrences : null
  } catch {
    return null
  }
}

async function loadOccurrences() {
  try {
    const list = await calendarApi.occurrences(
      spaceKey.value,
      range.value.from,
      range.value.to,
      selectedIds.value.length ? selectedIds.value : undefined,
      searchQ.value || undefined,
      filterType.value || undefined,
      filterSource.value || undefined,
    )
    occurrences.value = list
    writeCache(list)
    scheduleBrowserAlerts()
  } catch (e) {
    const cached = readCache()
    if (cached) {
      occurrences.value = cached
      toast.add({ severity: 'warn', summary: t('calendar.offlineCached'), life: 4000 })
    } else {
      throw e
    }
  }
}

async function load() {
  loading.value = true
  try {
    if (spaceKey.value && space.value?.key !== spaceKey.value) space.value = (await wikiApi.space(spaceKey.value)).value
    await loadBoards()
    prefs.value = await calendarApi.prefs().catch(() => null)
    await loadOccurrences()
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}
let searchTimer: number | undefined
let nowTimer: number | undefined
onMounted(() => {
  void load()
  nowTimer = window.setInterval(() => {
    const d = new Date()
    nowMinutes.value = d.getHours() * 60 + d.getMinutes()
  }, 60_000)
})
watch([anchor, calendar, spaceKey, viewMode], load)
watch([searchQ, filterType, filterSource], () => {
  if (searchTimer) window.clearTimeout(searchTimer)
  searchTimer = window.setTimeout(() => void loadOccurrences().catch(fail), 300)
})
watch(showArchived, () => void loadBoards().catch(fail))

const go = (delta: number) => (anchor.value = shiftByView(anchor.value, calendar.value, viewMode.value, delta))
const goToday = () => (anchor.value = toIso(new Date()))

type Form = {
  title: string
  description: string
  location: string
  allDay: boolean
  startDate: string
  startTime: string
  endDate: string
  endTime: string
  timeZone: string
  frequency: Frequency
  interval: number
  weekDays: number[]
  recurCalendar: RecurrenceCalendar
  endMode: 'never' | 'until' | 'count'
  until: string
  count: number
  type: string
  status: string
  priority: string
  color: string
  onlineUrl: string
  isPrivate: boolean
  calendarId: string
  reminders: { minutesBefore: number; inApp: boolean; email: boolean }[]
  attendees: { userId: string; name: string; optional: boolean }[]
  linkedPageId: string
  linkedWhiteboardId: string
  monthWeek: number
}

const editing = ref<'new' | CalendarEvent | null>(null)
const readOnly = ref(false)
const occurrenceDate = ref<string | null>(null)
const saving = ref(false)
const form = ref<Form>(blank(toIso(new Date())))

function blank(date: string): Form {
  return {
    title: '', description: '', location: '', allDay: false, startDate: date, startTime: '09:00', endDate: date, endTime: '10:00', timeZone: host.tenant?.timeZone ?? 'UTC',
    frequency: 'None', interval: 1, weekDays: [weekdayOf(date)], recurCalendar: calendar.value === 'persian' ? 'Persian' : 'Gregorian', endMode: 'never', until: '', count: 10,
    type: 'Event', status: 'Scheduled', priority: 'Normal', color: '', onlineUrl: '', isPrivate: false,
    calendarId: boards.value.find((b) => b.isDefault)?.id ?? boards.value[0]?.id ?? '',
    reminders: [{ minutesBefore: 15, inApp: true, email: false }],
    attendees: [], linkedPageId: '', linkedWhiteboardId: '', monthWeek: 0,
  }
}

function fromEvent(e: CalendarEvent): Form {
  const r = e.recurrence
  return {
    title: e.title, description: e.description ?? '', location: e.location ?? '', allDay: e.allDay, startDate: e.startDate, startTime: e.startTime ?? '09:00', endDate: e.endDate,
    endTime: e.endTime ?? '10:00', timeZone: e.timeZone, frequency: r?.frequency ?? 'None', interval: r?.interval ?? 1, weekDays: r?.weekDays.length ? [...r.weekDays] : [weekdayOf(e.startDate)],
    recurCalendar: r?.calendar ?? (calendar.value === 'persian' ? 'Persian' : 'Gregorian'), endMode: r?.until ? 'until' : r?.count ? 'count' : 'never', until: r?.until ?? '', count: r?.count ?? 10,
    type: e.type, status: e.status, priority: e.priority, color: e.color ?? '', onlineUrl: e.onlineUrl ?? '', isPrivate: e.isPrivate, calendarId: e.calendarId,
    reminders: e.reminders.map((x) => ({ minutesBefore: x.minutesBefore, inApp: x.inApp, email: x.email })),
    attendees: (e.attendees ?? []).map((a) => ({ userId: a.userId, name: a.displayName ?? a.userId, optional: a.optional })),
    linkedPageId: e.linkedPageId ?? '', linkedWhiteboardId: e.linkedWhiteboardId ?? '', monthWeek: r?.monthWeek ?? 0,
  }
}

function newEvent(date: string = anchor.value) {
  if (offline.value) {
    toast.add({ severity: 'warn', summary: t('calendar.offlineBlock'), life: 4000 })
    return
  }
  form.value = blank(date)
  occurrenceDate.value = null
  readOnly.value = false
  editing.value = 'new'
}

/** Double-click an empty day (month / week / timeline) to open the create dialog for that date. */
function onDayDblClick(date: string) {
  if (!canCreate.value) return
  newEvent(date)
}

function jumpMonth(iso: string) {
  anchor.value = iso
  viewMode.value = 'month'
}

async function openOccurrence(o: Occurrence) {
  if (o.sourceKind !== 'Raavi' || !o.eventId) {
    if (o.externalUrl) window.open(o.externalUrl, '_blank', 'noopener')
    return
  }
  try {
    const ev = await calendarApi.event(o.eventId)
    form.value = fromEvent(ev)
    occurrenceDate.value = o.isRecurring ? o.occurrenceDate : null
    readOnly.value = !ev.canEdit || offline.value
    editing.value = ev
  } catch (e) {
    fail(e)
  }
}

function applyPreset(id: string) {
  if (id === 'none') {
    form.value.frequency = 'None'
    form.value.interval = 1
    return
  }
  const start = form.value.startDate || today
  if (id === 'standup') {
    form.value.frequency = 'Weekly'
    form.value.interval = 1
    form.value.weekDays = [1, 2, 3, 4, 5]
    form.value.endMode = 'never'
  } else if (id === 'weekdays') {
    form.value.frequency = 'Weekly'
    form.value.interval = 1
    form.value.weekDays = [1, 2, 3, 4, 5]
    form.value.endMode = 'never'
  } else if (id === 'biweekly') {
    form.value.frequency = 'Weekly'
    form.value.interval = 2
    form.value.weekDays = [weekdayOf(start)]
    form.value.endMode = 'never'
  }
}

async function openShare(b: CalendarBoard) {
  if (offline.value) {
    toast.add({ severity: 'warn', summary: t('calendar.offlineBlock'), life: 4000 })
    return
  }
  try {
    grants.value = await calendarApi.grants(b.id)
    shareBoard.value = b
  } catch (e) {
    fail(e)
  }
}

async function addGrant(pick: { subjectType: 'User' | 'Group' | 'AllMembers'; subjectId: string | null }) {
  if (!shareBoard.value || !pick.subjectId) return
  try {
    await calendarApi.putGrant(shareBoard.value.id, { subjectType: pick.subjectType, subjectId: pick.subjectId, grant: grantLevel.value })
    grants.value = await calendarApi.grants(shareBoard.value.id)
  } catch (e) {
    fail(e)
  }
}

async function removeGrant(g: CalendarGrant) {
  if (!shareBoard.value) return
  try {
    await calendarApi.deleteGrant(shareBoard.value.id, g.id)
    grants.value = await calendarApi.grants(shareBoard.value.id)
  } catch (e) {
    fail(e)
  }
}

function openImport(b: CalendarBoard) {
  if (offline.value) {
    toast.add({ severity: 'warn', summary: t('calendar.offlineBlock'), life: 4000 })
    return
  }
  importBoard.value = b
  importText.value = ''
}

async function runImport() {
  if (!importBoard.value || !importText.value.trim()) return
  importing.value = true
  try {
    const result = await calendarApi.importIcs(importBoard.value.id, importText.value)
    toast.add({
      severity: result.skipped ? 'warn' : 'success',
      summary: t('calendar.importResult', { imported: num(result.imported), skipped: num(result.skipped) }),
      life: 5000,
    })
    importBoard.value = null
    await load()
  } catch (e) {
    fail(e)
  } finally {
    importing.value = false
  }
}

function onIcsFile(ev: Event) {
  const input = ev.target as HTMLInputElement
  const file = input.files?.[0]
  if (!file) return
  if (file.size > 1_000_000) {
    toast.add({ severity: 'error', summary: t('calendar.importTooLarge'), life: 5000 })
    input.value = ''
    return
  }
  const reader = new FileReader()
  reader.onload = () => {
    importText.value = String(reader.result ?? '')
  }
  reader.readAsText(file)
  input.value = ''
}

async function archiveBoard(b: CalendarBoard) {
  if (offline.value) return
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('calendar.confirmArchive', { name: b.name }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await calendarApi.archiveBoard(b.id)
        await load()
      } catch (e) {
        fail(e)
      }
    },
  })
}

async function restoreBoard(b: CalendarBoard) {
  try {
    await calendarApi.restoreBoard(b.id)
    await load()
  } catch (e) {
    fail(e)
  }
}

function askPurge(b: CalendarBoard) {
  confirm.require({
    header: t('calendar.purgeTitle'),
    message: t('calendar.confirmPurge', { name: b.name }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      const typed = window.prompt(t('calendar.purgePrompt', { name: b.name }), '')
      if (typed == null) return
      try {
        await calendarApi.purgeBoard(b.id, typed)
        await load()
      } catch (e) {
        fail(e)
      }
    },
  })
}

const frequencies = computed(() => (['None', 'Daily', 'Weekly', 'Monthly', 'Yearly'] as Frequency[]).map((v) => ({ value: v, label: t(`calendar.freq.${v}`) })))
const recurCalendars = computed(() => [
  { value: 'Gregorian', label: t('calendar.gregorian') },
  { value: 'Persian', label: t('calendar.jalali') },
])
const endModes = computed(() => [
  { value: 'never', label: t('calendar.endNever') },
  { value: 'until', label: t('calendar.endUntil') },
  { value: 'count', label: t('calendar.endCount') },
])
const weekdayChoices = computed(() => weekdays.value.map((w) => ({ index: weekdayOf(w.iso), label: w.long })))
const startWeekday = computed(() => weekdayOf(form.value.startDate || today))
const boardChoices = computed(() => boards.value.map((b) => ({ value: b.id, label: b.name })))
const typeChoices = ['Event', 'Meeting', 'Reminder', 'Milestone', 'Holiday'].map((v) => ({ value: v, label: v }))
const statusChoices = ['Scheduled', 'Confirmed', 'Tentative', 'Cancelled', 'Completed'].map((v) => ({ value: v, label: v }))
const priorityChoices = ['Low', 'Normal', 'High'].map((v) => ({ value: v, label: v }))
const scopeChoices = computed(() => [
  { value: 'EntireSeries', label: t('calendar.scopeSeries') },
  { value: 'ThisOccurrence', label: t('calendar.scopeThis') },
  { value: 'ThisAndFollowing', label: t('calendar.scopeFollowing') },
])

function toggleDay(index: number, on: boolean) {
  const set = new Set(form.value.weekDays)
  if (on) set.add(index)
  else set.delete(index)
  form.value.weekDays = [...set].sort()
}

const isRecurring = computed(() => form.value.frequency !== 'None')
const preview = (iso: string) => (iso ? dateLabel(iso, calendar.value, intlTag.value) : '')

function body(scope?: EditScope): CalendarEventInput {
  const f = form.value
  const weeklyDays = [...new Set([...f.weekDays, startWeekday.value])].sort()
  const nthDays = f.monthWeek ? [startWeekday.value] : []
  const days = f.frequency === 'Weekly' ? weeklyDays : f.frequency === 'Monthly' || f.frequency === 'Yearly' ? nthDays : []
  return {
    title: f.title.trim(),
    description: f.description.trim() || null,
    location: f.location.trim() || null,
    allDay: f.allDay,
    startDate: f.startDate,
    startTime: f.allDay ? null : f.startTime,
    endDate: f.endDate || f.startDate,
    endTime: f.allDay ? null : f.endTime,
    timeZone: f.timeZone,
    recurrence: isRecurring.value
      ? {
          frequency: f.frequency,
          interval: Math.max(1, Math.floor(Number(f.interval) || 1)),
          weekDays: days,
          calendar: f.recurCalendar,
          until: f.endMode === 'until' && f.until ? f.until : null,
          count: f.endMode === 'count' ? Math.max(1, Math.floor(Number(f.count) || 1)) : null,
          monthWeek: f.frequency === 'Monthly' || f.frequency === 'Yearly' ? f.monthWeek : 0,
        }
      : null,
    type: f.type,
    status: f.status,
    priority: f.priority,
    color: f.color.trim() || null,
    onlineUrl: f.onlineUrl.trim() || null,
    isPrivate: f.isPrivate,
    reminders: f.reminders,
    attendees: f.attendees.map((a) => ({ userId: a.userId, optional: a.optional })),
    linkedPageId: f.linkedPageId.trim() || null,
    linkedWhiteboardId: f.linkedWhiteboardId.trim() || null,
    editScope: scope,
    occurrenceDate: occurrenceDate.value,
  }
}

async function save() {
  if (offline.value) {
    toast.add({ severity: 'warn', summary: t('calendar.offlineBlock'), life: 4000 })
    return
  }
  const target = editing.value
  if (!target || !form.value.title.trim() || !form.value.startDate) return
  if (target !== 'new' && occurrenceDate.value && target.recurrence) {
    scopePrompt.value = true
    return
  }
  await persist('EntireSeries')
}

async function persist(scope: EditScope) {
  if (offline.value) {
    toast.add({ severity: 'warn', summary: t('calendar.offlineBlock'), life: 4000 })
    return
  }
  const target = editing.value
  if (!target || !form.value.title.trim() || !form.value.startDate) return
  saving.value = true
  scopePrompt.value = false
  try {
    if (target === 'new') {
      if (form.value.calendarId) await calendarApi.createOnCalendar(form.value.calendarId, body())
      else if (spaceKey.value) await calendarApi.create(spaceKey.value, body())
    } else await calendarApi.update(target, body(scope))
    editing.value = null
    await load()
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}

function askRemove() {
  const target = editing.value
  if (!target || target === 'new') return
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('calendar.confirmDelete', { title: target.title }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await calendarApi.remove(target.id)
        editing.value = null
        await load()
      } catch (e) {
        fail(e)
      }
    },
  })
}

async function cancelThis() {
  const target = editing.value
  if (!target || target === 'new' || !occurrenceDate.value) return
  try {
    await calendarApi.cancelOccurrence(target.id, occurrenceDate.value)
    editing.value = null
    await load()
  } catch (e) {
    fail(e)
  }
}

async function savePrefs() {
  if (!prefs.value) return
  try {
    prefs.value = await calendarApi.updatePrefs(prefs.value)
    prefsOpen.value = false
    toast.add({ severity: 'success', summary: t('common.saved'), life: 2500 })
  } catch (e) {
    fail(e)
  }
}

let alertTimer: number | undefined
function scheduleBrowserAlerts() {
  if (alertTimer) window.clearInterval(alertTimer)
  alertTimer = window.setInterval(() => {
    const p = prefs.value
    if (!p?.browserAlerts || typeof Notification === 'undefined') return
    if (Notification.permission === 'default') void Notification.requestPermission()
    if (Notification.permission !== 'granted') return
    const now = Date.now()
    for (const o of occurrences.value) {
      if (!o.startUtc || o.freeBusyOnly || o.sourceKind !== 'Raavi') continue
      const start = Date.parse(o.startUtc)
      const delta = start - now
      if (delta < 0 || delta > 60_000) continue
      const title = o.title || t('calendar.freeBusy')
      new Notification(title, { body: o.location ?? o.startTime ?? '' })
      if (p.ttsEnabled && 'speechSynthesis' in window) {
        const u = new SpeechSynthesisUtterance(title)
        u.lang = p.ttsLocale === 'fa' ? 'fa-IR' : 'en-US'
        u.rate = p.ttsRate
        window.speechSynthesis.speak(u)
      }
    }
  }, 30_000)
}
onUnmounted(() => {
  if (alertTimer) window.clearInterval(alertTimer)
  if (searchTimer) window.clearTimeout(searchTimer)
  if (nowTimer) window.clearInterval(nowTimer)
})

const dayColumns = computed(() => {
  const days: string[] = []
  for (let d = range.value.from; d <= range.value.to; d = addDays(d, 1)) days.push(d)
  return days
})

function countInMonth(monthStart: string): number {
  const m = partsOf(monthStart, calendar.value).m
  let n = 0
  for (const [iso, list] of byDay.value) {
    if (partsOf(iso, calendar.value).m === m && partsOf(iso, calendar.value).y === partsOf(monthStart, calendar.value).y) n += list.length
  }
  return n
}

const contextItems = computed<MenuItem[]>(() => {
  const o = contextOcc.value
  if (!o) return []
  return [
    { label: t('calendar.open'), command: () => void openOccurrence(o) },
    { label: t('calendar.duplicate'), visible: !!o.canEdit && o.sourceKind === 'Raavi' && !offline.value, command: () => void duplicateOccurrence(o) },
    { label: t('calendar.cancelOccurrence'), visible: !!o.canEdit && o.isRecurring && !offline.value, command: () => void cancelFromContext(o) },
    { label: t('common.delete'), visible: !!o.canEdit && o.sourceKind === 'Raavi' && !offline.value, command: () => void deleteFromContext(o) },
  ]
})

function openContext(e: MouseEvent, o: Occurrence) {
  e.preventDefault()
  contextOcc.value = o
  contextMenu.value?.show(e)
}

function onDragStart(e: DragEvent, o: Occurrence) {
  if (!o.canEdit || o.sourceKind !== 'Raavi' || offline.value) {
    e.preventDefault()
    return
  }
  dragging.value = o
  e.dataTransfer?.setData('text/plain', o.eventId ?? '')
  if (e.dataTransfer) e.dataTransfer.effectAllowed = 'move'
}

function onDragEnd() {
  dragging.value = null
}

function onDayDragOver(e: DragEvent) {
  if (dragging.value) e.preventDefault()
}

async function onDayDrop(e: DragEvent, targetDate: string) {
  e.preventDefault()
  const o = dragging.value
  dragging.value = null
  if (!o?.eventId || !o.canEdit || offline.value) return
  const delta = daysBetween(o.occurrenceDate || o.startDate, targetDate)
  if (delta === 0) return
  try {
    const ev = await calendarApi.event(o.eventId)
    const startDate = addDays(ev.startDate, delta)
    const endDate = addDays(ev.endDate, delta)
    const scope: EditScope = ev.recurrence && o.isRecurring ? 'ThisOccurrence' : 'EntireSeries'
    await calendarApi.update(ev, {
      title: ev.title,
      description: ev.description,
      location: ev.location,
      allDay: ev.allDay,
      startDate,
      startTime: ev.startTime,
      endDate,
      endTime: ev.endTime,
      timeZone: ev.timeZone,
      recurrence: scope === 'ThisOccurrence' ? null : ev.recurrence,
      type: ev.type,
      status: ev.status,
      priority: ev.priority,
      color: ev.color,
      onlineUrl: ev.onlineUrl,
      isPrivate: ev.isPrivate,
      reminders: ev.reminders.map((r) => ({ minutesBefore: r.minutesBefore, inApp: r.inApp, email: r.email })),
      editScope: scope,
      occurrenceDate: o.occurrenceDate,
    })
    await load()
  } catch (err) {
    fail(err)
  }
}

async function duplicateOccurrence(o: Occurrence) {
  if (!o.eventId || offline.value) return
  try {
    const ev = await calendarApi.event(o.eventId)
    const body: CalendarEventInput = {
      title: t('calendar.copyOf', { title: ev.title }),
      description: ev.description,
      location: ev.location,
      allDay: ev.allDay,
      startDate: ev.startDate,
      startTime: ev.startTime,
      endDate: ev.endDate,
      endTime: ev.endTime,
      timeZone: ev.timeZone,
      recurrence: null,
      type: ev.type,
      status: ev.status,
      priority: ev.priority,
      color: ev.color,
      onlineUrl: ev.onlineUrl,
      isPrivate: ev.isPrivate,
      reminders: ev.reminders.map((r) => ({ minutesBefore: r.minutesBefore, inApp: r.inApp, email: r.email })),
    }
    await calendarApi.createOnCalendar(ev.calendarId, body)
    await load()
  } catch (err) {
    fail(err)
  }
}

async function cancelFromContext(o: Occurrence) {
  if (!o.eventId || !o.occurrenceDate) return
  try {
    await calendarApi.cancelOccurrence(o.eventId, o.occurrenceDate)
    await load()
  } catch (err) {
    fail(err)
  }
}

async function deleteFromContext(o: Occurrence) {
  if (!o.eventId) return
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('calendar.confirmDelete', { title: o.title || t('calendar.freeBusy') }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await calendarApi.remove(o.eventId!)
        await load()
      } catch (err) {
        fail(err)
      }
    },
  })
}

function openNewBoard() {
  boardForm.value = { name: '', color: '#3b82f6', description: '', scope: spaceKey.value ? 'Space' : 'Personal' }
  boardDialog.value = 'new'
}

function openEditBoard(b: CalendarBoard) {
  boardForm.value = { name: b.name, color: b.color || '#3b82f6', description: b.description ?? '', scope: b.scope }
  boardDialog.value = b
}

async function saveBoard() {
  if (!boardForm.value.name.trim() || offline.value) return
  try {
    if (boardDialog.value === 'new') {
      await calendarApi.createBoard({
        scope: boardForm.value.scope,
        spaceKey: boardForm.value.scope === 'Space' ? spaceKey.value ?? undefined : undefined,
        name: boardForm.value.name.trim(),
        description: boardForm.value.description.trim() || undefined,
        color: boardForm.value.color,
      })
    } else if (boardDialog.value) {
      await calendarApi.updateBoard(boardDialog.value.id, {
        name: boardForm.value.name.trim(),
        description: boardForm.value.description.trim() || undefined,
        color: boardForm.value.color,
      })
    }
    boardDialog.value = null
    await load()
  } catch (err) {
    fail(err)
  }
}

function addAttendee(pick: { subjectType: 'User' | 'Group' | 'AllMembers'; subjectId: string | null; name: string }) {
  if (pick.subjectType !== 'User' || !pick.subjectId) return
  if (form.value.attendees.some((a) => a.userId === pick.subjectId)) return
  form.value.attendees.push({ userId: pick.subjectId, name: pick.name, optional: false })
}

async function sendRsvp(response: string) {
  const target = editing.value
  if (!target || target === 'new' || offline.value) return
  try {
    editing.value = await calendarApi.rsvp(target.id, response)
    form.value = fromEvent(editing.value)
    toast.add({ severity: 'success', summary: t('calendar.rsvpSaved'), life: 2500 })
  } catch (e) {
    fail(e)
  }
}

const monthWeekChoices = computed(() => [
  { value: 0, label: t('calendar.monthWeekDay') },
  { value: 1, label: t('calendar.monthWeek1') },
  { value: 2, label: t('calendar.monthWeek2') },
  { value: 3, label: t('calendar.monthWeek3') },
  { value: 4, label: t('calendar.monthWeek4') },
  { value: -1, label: t('calendar.monthWeekLast') },
])

async function moveEditingTo(calendarId: string) {
  const target = editing.value
  if (!target || target === 'new' || offline.value || target.calendarId === calendarId) return
  try {
    const moved = await calendarApi.move(target.id, calendarId)
    editing.value = moved
    form.value.calendarId = moved.calendarId
    toast.add({ severity: 'success', summary: t('calendar.moved'), life: 2500 })
    await load()
  } catch (err) {
    fail(err)
  }
}

function timelineTop(o: Occurrence): string {
  const m = minutesOf(o.startTime) ?? 0
  return `${(m / (24 * 60)) * 100}%`
}

function timelineHeight(o: Occurrence): string {
  const start = minutesOf(o.startTime) ?? 0
  const end = minutesOf(o.endTime) ?? start + 60
  const span = Math.max(30, end - start)
  return `${(span / (24 * 60)) * 100}%`
}
</script>

<template>
  <section class="calendar" data-testid="calendar">
    <header class="head">
      <h1>{{ spaceKey ? t('calendar.spaceTitle', { name: space?.name ?? spaceKey }) : t('calendar.title') }}</h1>
      <div class="tools">
        <SelectButton v-model="viewMode" :options="viewOptions" option-label="label" option-value="value" :allow-empty="false" data-testid="calendar-view" />
        <SelectButton v-model="calendar" :options="calendars" option-label="label" option-value="value" :allow-empty="false" :aria-label="t('calendar.system')" data-testid="calendar-system" />
        <InputText v-model="searchQ" :placeholder="t('calendar.search')" data-testid="calendar-search" class="search" />
        <Select v-model="filterType" :options="typeFilterChoices" option-label="label" option-value="value" data-testid="filter-type" class="filter" />
        <Select v-model="filterSource" :options="sourceFilterChoices" option-label="label" option-value="value" data-testid="filter-source" class="filter" />
        <Button icon="pi pi-bell" text :label="t('calendar.prefs')" data-testid="calendar-prefs" @click="prefsOpen = true" />
        <Button v-if="canCreate" icon="pi pi-plus" :label="t('calendar.new')" data-testid="new-event" @click="newEvent()" />
        <Button v-if="spaceKey && !offline" as="a" :href="calendarApi.exportUrl(spaceKey)" icon="pi pi-download" text :label="t('calendar.export')" data-testid="export-calendar" />
      </div>
    </header>

    <Message v-if="offline" severity="warn" :closable="false" data-testid="calendar-offline">{{ t('calendar.offlineNotice') }}</Message>

    <div class="layout">
      <aside class="sidebar" :aria-label="t('calendar.calendars')">
        <div class="sidebar-head">
          <h2>{{ t('calendar.calendars') }}</h2>
          <Button v-if="canManageBoards" icon="pi pi-plus" text rounded size="small" :aria-label="t('calendar.newCalendar')" data-testid="new-calendar" @click="openNewBoard" />
        </div>
        <div v-for="b in sortedBoards" :key="b.id" class="board-row">
          <label class="board">
            <Checkbox :model-value="selectedIds.includes(b.id)" binary @update:model-value="(v: boolean) => toggleBoard(b.id, v)" />
            <span class="dot" :style="{ background: b.color }" aria-hidden="true" />
            <span dir="auto">{{ b.name }}</span>
          </label>
          <div class="board-actions">
            <Button :icon="favoriteIds.includes(b.id) ? 'pi pi-star-fill' : 'pi pi-star'" text rounded size="small" :aria-label="t('calendar.favorite')" data-testid="favorite-calendar" @click="toggleFavorite(b.id)" />
            <Button icon="pi pi-pencil" text rounded size="small" :aria-label="t('calendar.editCalendar')" data-testid="edit-calendar" :disabled="offline" @click="openEditBoard(b)" />
            <Button icon="pi pi-share-alt" text rounded size="small" :aria-label="t('calendar.grant')" data-testid="share-calendar" :disabled="offline" @click="openShare(b)" />
            <Button icon="pi pi-upload" text rounded size="small" :aria-label="t('calendar.import')" data-testid="import-calendar" :disabled="offline" @click="openImport(b)" />
            <Button icon="pi pi-inbox" text rounded size="small" :aria-label="t('calendar.archive')" data-testid="archive-calendar" :disabled="offline" @click="archiveBoard(b)" />
          </div>
        </div>
        <label class="inline"><Checkbox v-model="showArchived" binary input-id="cal-archived" /> {{ t('calendar.showArchived') }}</label>
        <div v-for="b in archivedBoards" :key="b.id" class="board-row archived">
          <span dir="auto">{{ b.name }}</span>
          <div class="board-actions">
            <Button icon="pi pi-replay" text rounded size="small" :aria-label="t('calendar.restore')" data-testid="restore-calendar" :disabled="offline" @click="restoreBoard(b)" />
            <Button icon="pi pi-trash" text rounded size="small" severity="danger" :aria-label="t('calendar.purge')" data-testid="purge-calendar" :disabled="offline" @click="askPurge(b)" />
          </div>
        </div>
      </aside>

      <div class="main">
        <nav class="nav" :aria-label="t('calendar.navigate')">
          <Button :icon="rtl ? 'pi pi-chevron-right' : 'pi pi-chevron-left'" text rounded :aria-label="t('calendar.previous')" data-testid="prev-month" @click="go(-1)" />
          <Button :icon="rtl ? 'pi pi-chevron-left' : 'pi pi-chevron-right'" text rounded :aria-label="t('calendar.next')" data-testid="next-month" @click="go(1)" />
          <strong class="month" data-testid="month-title" aria-live="polite">{{ title }}</strong>
          <Button :label="t('calendar.today')" text size="small" data-testid="today" @click="goToday" />
        </nav>

        <div v-if="viewMode === 'year'" class="year" data-testid="year-view" :aria-busy="loading">
          <button
            v-for="m in yearMonthStarts"
            :key="m"
            type="button"
            class="year-month"
            data-testid="year-month"
            @click="jumpMonth(m)"
          >
            <strong>{{ monthLabel(m, calendar, intlTag) }}</strong>
            <span class="muted">{{ t('calendar.yearCount', { count: num(countInMonth(m)) }) }}</span>
          </button>
        </div>

        <div v-else-if="viewMode === 'month'" class="grid" role="grid" :aria-label="title" :aria-busy="loading">
          <div v-for="w in weekdays" :key="w.iso" class="weekday" role="columnheader" :abbr="w.long">{{ w.label }}</div>
          <div
            v-for="day in grid"
            :key="day.iso"
            :class="['day', { muted: !day.inMonth, today: day.iso === today, drop: !!dragging, create: canCreate }]"
            role="gridcell"
            :data-date="day.iso"
            :data-testid="canCreate ? 'calendar-day' : undefined"
            :title="canCreate ? t('calendar.dblClickCreate', { date: preview(day.iso) }) : undefined"
            @dragover="onDayDragOver"
            @drop="onDayDrop($event, day.iso)"
            @dblclick="onDayDblClick(day.iso)"
          >
            <span class="num" :aria-label="preview(day.iso)">{{ num(day.day) }}</span>
            <ul class="events">
              <li v-for="o in (byDay.get(day.iso) ?? []).slice(0, 3)" :key="occKey(o)">
                <button
                  type="button"
                  class="event"
                  data-testid="occurrence"
                  :draggable="o.canEdit && o.sourceKind === 'Raavi' && !offline"
                  :style="o.color ? { borderInlineStartColor: o.color } : undefined"
                  :title="o.title"
                  @click="openOccurrence(o)"
                  @dblclick.stop
                  @contextmenu="openContext($event, o)"
                  @dragstart="onDragStart($event, o)"
                  @dragend="onDragEnd"
                >
                  <span class="time">{{ timeLabel(o) }}</span> <span class="name" dir="auto">{{ o.title || t('calendar.freeBusy') }}</span>
                  <span v-if="o.sourceKind === 'Jira'" class="space">· {{ t('calendar.sourceJira') }}</span>
                  <span v-else-if="!spaceKey && o.spaceName" class="space">· {{ o.spaceName }}</span>
                </button>
              </li>
              <li v-if="(byDay.get(day.iso)?.length ?? 0) > 3" class="more">{{ t('calendar.more', { count: num((byDay.get(day.iso)?.length ?? 0) - 3) }) }}</li>
            </ul>
          </div>
        </div>

        <div v-else-if="viewMode === 'timeline'" class="timeline" data-testid="timeline-view" :aria-busy="loading">
          <div class="timeline-hours" aria-hidden="true">
            <span v-for="h in TIMELINE_HOURS" :key="h" class="hour" :style="{ insetBlockStart: `${(h / 24) * 100}%` }">{{ String(h).padStart(2, '0') }}:00</span>
          </div>
          <div
            class="timeline-track"
            :class="{ create: canCreate }"
            :data-testid="canCreate ? 'calendar-day' : undefined"
            :title="canCreate ? t('calendar.dblClickCreate', { date: preview(anchor) }) : undefined"
            @dragover="onDayDragOver"
            @drop="onDayDrop($event, anchor)"
            @dblclick="onDayDblClick(anchor)"
          >
            <div v-if="anchor === today" class="now-line" data-testid="now-line" :style="{ insetBlockStart: `${(nowMinutes / (24 * 60)) * 100}%` }" />
            <button
              v-for="o in byDay.get(anchor) ?? []"
              :key="occKey(o)"
              type="button"
              class="event timeline-event"
              data-testid="occurrence"
              :draggable="o.canEdit && o.sourceKind === 'Raavi' && !offline"
              :style="{ insetBlockStart: timelineTop(o), blockSize: o.allDay ? '1.5rem' : timelineHeight(o), ...(o.color ? { borderInlineStartColor: o.color } : {}) }"
              @click="openOccurrence(o)"
              @dblclick.stop
              @contextmenu="openContext($event, o)"
              @dragstart="onDragStart($event, o)"
              @dragend="onDragEnd"
            >
              <span class="time">{{ timeLabel(o) }}</span> <span dir="auto">{{ o.title || t('calendar.freeBusy') }}</span>
            </button>
          </div>
        </div>

        <div v-else-if="viewMode === 'agenda'" class="agenda" data-testid="agenda-view">
          <article v-for="o in agendaList" :key="occKey(o)" class="agenda-row">
            <button type="button" class="event wide" data-testid="occurrence" @click="openOccurrence(o)" @contextmenu="openContext($event, o)">
              <strong>{{ preview(o.startDate) }}</strong>
              <span>{{ timeLabel(o) }}</span>
              <span dir="auto">{{ o.title || t('calendar.freeBusy') }}</span>
            </button>
          </article>
        </div>

        <div v-else class="week" data-testid="week-view">
          <div
            v-for="d in dayColumns"
            :key="d"
            class="week-col"
            :class="{ today: d === today, create: canCreate }"
            :data-testid="canCreate ? 'calendar-day' : undefined"
            :data-date="d"
            :title="canCreate ? t('calendar.dblClickCreate', { date: preview(d) }) : undefined"
            @dragover="onDayDragOver"
            @drop="onDayDrop($event, d)"
            @dblclick="onDayDblClick(d)"
          >
            <h3>{{ preview(d) }}</h3>
            <div v-if="d === today" class="week-now" data-testid="week-now" :aria-label="t('calendar.now')" />
            <button
              v-for="o in byDay.get(d) ?? []"
              :key="occKey(o)"
              type="button"
              class="event wide"
              data-testid="occurrence"
              :draggable="o.canEdit && o.sourceKind === 'Raavi' && !offline"
              @click="openOccurrence(o)"
              @dblclick.stop
              @contextmenu="openContext($event, o)"
              @dragstart="onDragStart($event, o)"
              @dragend="onDragEnd"
            >
              <span class="time">{{ timeLabel(o) }}</span> <span dir="auto">{{ o.title || t('calendar.freeBusy') }}</span>
            </button>
          </div>
        </div>

        <p v-if="!loading && occurrences.length === 0" class="muted" data-testid="no-events">{{ t('calendar.none') }}</p>
      </div>
    </div>

    <Dialog :visible="!!editing" modal :header="editing === 'new' ? t('calendar.new') : readOnly ? t('calendar.details') : t('calendar.edit')" :style="{ inlineSize: 'min(40rem, 95vw)' }" @update:visible="(v) => !v && (editing = null)">
      <form class="form" @submit.prevent="save">
        <Message v-if="offline" severity="warn" :closable="false">{{ t('calendar.offlineBlock') }}</Message>
        <label for="ev-title">{{ t('calendar.eventTitle') }}</label>
        <InputText id="ev-title" v-model="form.title" maxlength="200" required :disabled="readOnly || offline" data-testid="event-title" />

        <label v-if="editing === 'new'" for="ev-cal">{{ t('calendar.calendars') }}</label>
        <Select v-if="editing === 'new'" v-model="form.calendarId" input-id="ev-cal" :options="boardChoices" option-label="label" option-value="value" :disabled="readOnly || offline" data-testid="event-calendar" />
        <label v-if="editing && editing !== 'new' && !readOnly" for="ev-move">{{ t('calendar.moveTo') }}</label>
        <Select
          v-if="editing && editing !== 'new' && !readOnly"
          :model-value="form.calendarId"
          input-id="ev-move"
          :options="boardChoices"
          option-label="label"
          option-value="value"
          :disabled="offline"
          data-testid="move-calendar"
          @update:model-value="(id: string) => moveEditingTo(id)"
        />

        <label class="inline"><Checkbox v-model="form.allDay" binary input-id="ev-allday" :disabled="readOnly || offline" /> <span>{{ t('calendar.allDay') }}</span></label>
        <label class="inline"><Checkbox v-model="form.isPrivate" binary input-id="ev-private" :disabled="readOnly || offline" /> <span>{{ t('calendar.isPrivate') }}</span></label>

        <div class="pair">
          <div>
            <label for="ev-start">{{ t('calendar.starts') }}</label>
            <CalendarDateInput id="ev-start" v-model="form.startDate" required :disabled="readOnly" data-testid="event-start" />
            <small class="muted" data-testid="start-label">{{ preview(form.startDate) }}</small>
            <InputText v-if="!form.allDay" v-model="form.startTime" type="time" dir="ltr" :aria-label="t('calendar.startTime')" :disabled="readOnly" />
          </div>
          <div>
            <label for="ev-end">{{ t('calendar.ends') }}</label>
            <CalendarDateInput id="ev-end" v-model="form.endDate" :min="form.startDate" :disabled="readOnly" data-testid="event-end" />
            <small class="muted">{{ preview(form.endDate) }}</small>
            <InputText v-if="!form.allDay" v-model="form.endTime" type="time" dir="ltr" :aria-label="t('calendar.endTime')" :disabled="readOnly" />
          </div>
        </div>
        <small class="muted" dir="ltr">{{ form.timeZone }}</small>

        <div class="pair">
          <div>
            <label for="ev-type">{{ t('calendar.type') }}</label>
            <Select id="ev-type" v-model="form.type" :options="typeChoices" option-label="label" option-value="value" :disabled="readOnly" />
          </div>
          <div>
            <label for="ev-status">{{ t('calendar.status') }}</label>
            <Select id="ev-status" v-model="form.status" :options="statusChoices" option-label="label" option-value="value" :disabled="readOnly" />
          </div>
        </div>
        <div class="pair">
          <div>
            <label for="ev-priority">{{ t('calendar.priority') }}</label>
            <Select id="ev-priority" v-model="form.priority" :options="priorityChoices" option-label="label" option-value="value" :disabled="readOnly" />
          </div>
          <div>
            <label for="ev-color">{{ t('calendar.color') }}</label>
            <InputText id="ev-color" v-model="form.color" dir="ltr" maxlength="7" placeholder="#3b82f6" :disabled="readOnly" />
          </div>
        </div>
        <label for="ev-url">{{ t('calendar.onlineUrl') }}</label>
        <InputText id="ev-url" v-model="form.onlineUrl" dir="ltr" maxlength="2000" :disabled="readOnly || offline" />

        <label for="ev-page">{{ t('calendar.linkedPage') }}</label>
        <InputText id="ev-page" v-model="form.linkedPageId" dir="ltr" maxlength="36" :placeholder="t('calendar.linkIdHint')" :disabled="readOnly || offline" data-testid="linked-page" />
        <label for="ev-board">{{ t('calendar.linkedBoard') }}</label>
        <InputText id="ev-board" v-model="form.linkedWhiteboardId" dir="ltr" maxlength="36" :placeholder="t('calendar.linkIdHint')" :disabled="readOnly || offline" data-testid="linked-board" />

        <fieldset class="reminders">
          <legend>{{ t('calendar.attendees') }}</legend>
          <PrincipalPicker v-if="!readOnly && !offline" input-id="ev-attendees" users-only @pick="addAttendee" />
          <ul class="grant-list" data-testid="attendee-list">
            <li v-for="(a, i) in form.attendees" :key="a.userId" class="grant-row">
              <span dir="auto">{{ a.name }}</span>
              <label class="inline"><Checkbox v-model="a.optional" binary :disabled="readOnly || offline" /> {{ t('calendar.optional') }}</label>
              <Button v-if="!readOnly && !offline" icon="pi pi-times" text rounded size="small" :aria-label="t('common.delete')" @click="form.attendees.splice(i, 1)" />
            </li>
          </ul>
        </fieldset>

        <div v-if="editing && editing !== 'new' && editing.myResponse" class="buttons" data-testid="rsvp-actions">
          <span class="muted">{{ t('calendar.myResponse', { response: editing.myResponse }) }}</span>
          <Button type="button" size="small" :label="t('calendar.rsvpAccept')" :disabled="offline" @click="sendRsvp('Accepted')" />
          <Button type="button" size="small" outlined :label="t('calendar.rsvpTentative')" :disabled="offline" @click="sendRsvp('Tentative')" />
          <Button type="button" size="small" severity="secondary" outlined :label="t('calendar.rsvpDecline')" :disabled="offline" @click="sendRsvp('Declined')" />
        </div>

        <label for="ev-location">{{ t('calendar.location') }}</label>
        <InputText id="ev-location" v-model="form.location" maxlength="200" :disabled="readOnly" />
        <label for="ev-description">{{ t('calendar.description') }}</label>
        <Textarea id="ev-description" v-model="form.description" rows="3" maxlength="4000" auto-resize :disabled="readOnly" />

        <fieldset class="reminders">
          <legend>{{ t('calendar.reminders') }}</legend>
          <div v-for="(r, i) in form.reminders" :key="i" class="pair">
            <InputNumber v-model="r.minutesBefore" :min="0" :max="43200" :use-grouping="false" :disabled="readOnly" :aria-label="t('calendar.minutesBefore')" />
            <label class="inline"><Checkbox v-model="r.inApp" binary :disabled="readOnly" /> in-app</label>
            <label class="inline"><Checkbox v-model="r.email" binary :disabled="readOnly" /> e-mail</label>
          </div>
          <Button v-if="!readOnly && form.reminders.length < 5" type="button" text size="small" :label="t('calendar.addReminder')" @click="form.reminders.push({ minutesBefore: 15, inApp: true, email: false })" />
        </fieldset>

        <label>{{ t('calendar.presets') }}</label>
        <div class="presets" data-testid="recurrence-presets">
          <Button
            v-for="p in recurrencePresets"
            :key="p.id"
            type="button"
            size="small"
            text
            :label="p.label"
            :disabled="readOnly || offline"
            @click="applyPreset(p.id)"
          />
        </div>

        <label for="ev-repeat">{{ t('calendar.repeat') }}</label>
        <Select v-model="form.frequency" input-id="ev-repeat" :options="frequencies" option-label="label" option-value="value" :disabled="readOnly || offline" data-testid="event-repeat" />

        <template v-if="isRecurring">
          <label for="ev-interval">{{ t('calendar.every') }}</label>
          <InputNumber v-model="form.interval" input-id="ev-interval" :min="1" :max="99" :use-grouping="false" :disabled="readOnly || offline" data-testid="event-interval" />
          <fieldset v-if="form.frequency === 'Weekly'" class="days">
            <legend>{{ t('calendar.onDays') }}</legend>
            <label v-for="w in weekdayChoices" :key="w.index" class="inline">
              <Checkbox :model-value="form.weekDays.includes(w.index) || w.index === startWeekday" binary :disabled="readOnly || offline || w.index === startWeekday" :input-id="`ev-day-${w.index}`" @update:model-value="(v: boolean) => toggleDay(w.index, v)" />
              <span>{{ w.label }}</span>
            </label>
          </fieldset>
          <template v-if="form.frequency === 'Monthly' || form.frequency === 'Yearly'">
            <label for="ev-recal">{{ t('calendar.repeatIn') }}</label>
            <SelectButton v-model="form.recurCalendar" input-id="ev-recal" :options="recurCalendars" option-label="label" option-value="value" :allow-empty="false" :disabled="readOnly || offline" data-testid="event-recur-calendar" />
            <label for="ev-monthweek">{{ t('calendar.monthWeek') }}</label>
            <Select id="ev-monthweek" v-model="form.monthWeek" :options="monthWeekChoices" option-label="label" option-value="value" :disabled="readOnly || offline" data-testid="event-monthweek" />
          </template>
          <label>{{ t('calendar.endsRule') }}</label>
          <SelectButton v-model="form.endMode" :options="endModes" option-label="label" option-value="value" :allow-empty="false" :disabled="readOnly || offline" />
          <CalendarDateInput v-if="form.endMode === 'until'" v-model="form.until" :min="form.startDate" :aria-label="t('calendar.endUntil')" :disabled="readOnly || offline" />
          <InputNumber v-if="form.endMode === 'count'" v-model="form.count" :min="1" :max="1000" :use-grouping="false" :aria-label="t('calendar.endCount')" :disabled="readOnly || offline" />
        </template>

        <div class="buttons">
          <Button v-if="!readOnly && !offline" type="submit" :label="t('common.save')" :loading="saving" :disabled="!form.title.trim() || !form.startDate" data-testid="save-event" />
          <Button v-if="!readOnly && !offline && occurrenceDate" type="button" severity="secondary" outlined :label="t('calendar.cancelOccurrence')" data-testid="cancel-occurrence" @click="cancelThis" />
          <Button v-if="!readOnly && !offline && editing !== 'new'" type="button" severity="danger" text :label="t('common.delete')" data-testid="delete-event" @click="askRemove" />
        </div>
      </form>
    </Dialog>

    <Dialog v-model:visible="scopePrompt" modal :header="t('calendar.editScope')" :style="{ inlineSize: 'min(28rem, 95vw)' }">
      <SelectButton v-model="editScope" :options="scopeChoices" option-label="label" option-value="value" :allow-empty="false" data-testid="edit-scope" />
      <div class="buttons">
        <Button :label="t('common.save')" data-testid="confirm-scope" :disabled="offline" @click="persist(editScope)" />
      </div>
    </Dialog>

    <Dialog v-model:visible="prefsOpen" modal :header="t('calendar.prefs')" :style="{ inlineSize: 'min(28rem, 95vw)' }">
      <form v-if="prefs" class="form" @submit.prevent="savePrefs">
        <label class="inline"><Checkbox v-model="prefs.browserAlerts" binary /> {{ t('calendar.browserAlerts') }}</label>
        <label class="inline"><Checkbox v-model="prefs.ttsEnabled" binary /> {{ t('calendar.tts') }}</label>
        <label class="inline"><Checkbox v-model="prefs.hidePrivateTitlesInTts" binary /> {{ t('calendar.hidePrivateTts') }}</label>
        <label for="tts-rate">{{ t('calendar.ttsRate') }}</label>
        <InputNumber id="tts-rate" v-model="prefs.ttsRate" :min="0.5" :max="2" :step="0.1" :min-fraction-digits="1" />
        <fieldset class="reminders">
          <legend>{{ t('calendar.quietHours') }}</legend>
          <label for="quiet-start">{{ t('calendar.quietStart') }}</label>
          <InputNumber id="quiet-start" v-model="prefs.quietStartMinutes" :min="0" :max="1439" :use-grouping="false" :show-buttons="true" />
          <label for="quiet-end">{{ t('calendar.quietEnd') }}</label>
          <InputNumber id="quiet-end" v-model="prefs.quietEndMinutes" :min="0" :max="1439" :use-grouping="false" :show-buttons="true" />
          <small class="muted">{{ t('calendar.quietHint') }}</small>
        </fieldset>
        <Button type="submit" :label="t('common.save')" :disabled="offline" />
      </form>
    </Dialog>

    <Dialog :visible="!!boardDialog" modal :header="boardDialog === 'new' ? t('calendar.newCalendar') : t('calendar.editCalendar')" :style="{ inlineSize: 'min(28rem, 95vw)' }" data-testid="board-dialog" @update:visible="(v) => !v && (boardDialog = null)">
      <form class="form" @submit.prevent="saveBoard">
        <label v-if="boardDialog === 'new'" for="board-scope">{{ t('calendar.scope') }}</label>
        <Select v-if="boardDialog === 'new'" id="board-scope" v-model="boardForm.scope" :options="scopeCreateChoices" option-label="label" option-value="value" data-testid="board-scope" />
        <label for="board-name">{{ t('calendar.calendarName') }}</label>
        <InputText id="board-name" v-model="boardForm.name" maxlength="100" required data-testid="board-name" />
        <label for="board-color">{{ t('calendar.color') }}</label>
        <InputText id="board-color" v-model="boardForm.color" dir="ltr" maxlength="7" data-testid="board-color" />
        <label for="board-desc">{{ t('calendar.description') }}</label>
        <Textarea id="board-desc" v-model="boardForm.description" rows="2" maxlength="2000" auto-resize />
        <Button type="submit" :label="t('common.save')" :disabled="!boardForm.name.trim() || offline" data-testid="save-board" />
      </form>
    </Dialog>

    <Menu ref="contextMenu" :model="contextItems" popup data-testid="occurrence-menu" />

    <Dialog :visible="!!shareBoard" modal :header="t('calendar.shareTitle', { name: shareBoard?.name ?? '' })" :style="{ inlineSize: 'min(32rem, 95vw)' }" data-testid="share-dialog" @update:visible="(v) => !v && (shareBoard = null)">
      <div class="form">
        <label for="grant-level">{{ t('calendar.grant') }}</label>
        <Select id="grant-level" v-model="grantLevel" :options="grantChoices" option-label="label" option-value="value" />
        <label for="grant-pick">{{ t('calendar.addShare') }}</label>
        <PrincipalPicker input-id="grant-pick" @pick="addGrant" />
        <ul class="grant-list" data-testid="grant-list">
          <li v-for="g in grants" :key="g.id" class="grant-row">
            <span>{{ g.subjectType }} · {{ t(`calendar.grant${g.grant}`) }}</span>
            <Button icon="pi pi-times" text rounded size="small" :aria-label="t('common.delete')" @click="removeGrant(g)" />
          </li>
        </ul>
      </div>
    </Dialog>

    <Dialog :visible="!!importBoard" modal :header="t('calendar.importTitle', { name: importBoard?.name ?? '' })" :style="{ inlineSize: 'min(36rem, 95vw)' }" data-testid="import-dialog" @update:visible="(v) => !v && (importBoard = null)">
      <div class="form">
        <label for="ics-file">{{ t('calendar.importFile') }}</label>
        <input id="ics-file" type="file" accept=".ics,text/calendar" data-testid="ics-file" @change="onIcsFile" />
        <label for="ics-text">{{ t('calendar.importPaste') }}</label>
        <Textarea id="ics-text" v-model="importText" rows="10" dir="ltr" data-testid="ics-text" />
        <Button :label="t('calendar.import')" :loading="importing" :disabled="!importText.trim()" data-testid="run-import" @click="runImport" />
      </div>
    </Dialog>
  </section>
</template>

<style scoped>
.calendar { display: grid; gap: var(--raavi-space-3); }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: center; }
h1 { margin: 0; font-size: 1.5rem; }
h2 { margin: 0 0 var(--raavi-space-2); font-size: 1rem; }
.tools, .nav { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.search { inline-size: min(12rem, 100%); }
.filter { inline-size: min(10rem, 100%); }
.layout { display: grid; grid-template-columns: 16rem minmax(0, 1fr); gap: var(--raavi-space-3); }
.sidebar { display: grid; gap: var(--raavi-space-2); align-content: start; }
.sidebar-head { display: flex; justify-content: space-between; align-items: center; gap: var(--raavi-space-2); }
.board-row { display: grid; gap: 2px; }
.board-row.archived { opacity: 0.85; }
.board { display: flex; gap: var(--raavi-space-2); align-items: center; }
.board-actions { display: flex; gap: 0; flex-wrap: wrap; }
.day.drop { outline: 1px dashed var(--raavi-primary, #2563eb); }
.timeline { display: grid; grid-template-columns: 3.5rem minmax(0, 1fr); min-block-size: 36rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); position: relative; }
.timeline-hours { position: relative; border-inline-end: 1px solid var(--raavi-border); }
.timeline-hours .hour { position: absolute; inset-inline-start: 0.25rem; font-size: 0.7rem; color: var(--raavi-text-muted); transform: translateY(-50%); }
.timeline-track { position: relative; min-block-size: 36rem; }
.timeline-event { position: absolute; inset-inline: 0.5rem; z-index: 1; white-space: nowrap; overflow: hidden; text-overflow: ellipsis; }
.now-line { position: absolute; inset-inline: 0; border-block-start: 2px solid var(--raavi-danger, #dc2626); z-index: 2; pointer-events: none; }
.week-col.today { background: color-mix(in srgb, var(--raavi-primary, #2563eb) 6%, transparent); }
.week-now { block-size: 2px; background: var(--raavi-danger, #dc2626); margin-block-end: var(--raavi-space-1); }
.dot { inline-size: 0.75rem; block-size: 0.75rem; border-radius: 999px; flex: none; }
.month { min-inline-size: 10rem; font-size: 1.1rem; }
.year { display: grid; grid-template-columns: repeat(auto-fill, minmax(10rem, 1fr)); gap: var(--raavi-space-2); }
.year-month { display: grid; gap: var(--raavi-space-1); text-align: start; padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: transparent; color: inherit; font: inherit; cursor: pointer; }
.presets { display: flex; flex-wrap: wrap; gap: var(--raavi-space-1); }
.grant-list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-1); }
.grant-row { display: flex; justify-content: space-between; align-items: center; gap: var(--raavi-space-2); }
.grid { display: grid; grid-template-columns: repeat(7, minmax(0, 1fr)); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); overflow: hidden; }
.weekday { padding: var(--raavi-space-2); text-align: center; font-weight: 600; font-size: 0.85rem; background: var(--raavi-surface-muted, transparent); border-block-end: 1px solid var(--raavi-border); }
.day { min-block-size: 6.5rem; padding: var(--raavi-space-1); border-inline-end: 1px solid var(--raavi-border); border-block-end: 1px solid var(--raavi-border); min-inline-size: 0; }
.day.create, .week-col.create, .timeline-track.create { cursor: cell; }
.day.muted { color: var(--raavi-text-muted); background: var(--raavi-surface-muted, transparent); }
.day.today .num { background: var(--raavi-primary, #2563eb); color: #fff; border-radius: 999px; }
.num { display: inline-block; min-inline-size: 1.6rem; padding: 0 0.3rem; text-align: center; border: 0; background: none; color: inherit; font: inherit; cursor: default; }
button.num { cursor: pointer; }
.events { list-style: none; margin: 0; padding: 0; display: grid; gap: 2px; }
.event { inline-size: 100%; text-align: start; border: 0; border-inline-start: 3px solid var(--raavi-primary, #2563eb); border-radius: 4px; padding: 1px 4px; background: color-mix(in srgb, var(--raavi-primary, #2563eb) 15%, transparent); color: inherit; font: inherit; font-size: 0.8rem; cursor: pointer; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.event.wide { white-space: normal; display: grid; gap: 2px; margin-block-end: var(--raavi-space-1); }
.event .time, .event .space { color: var(--raavi-text-muted); }
.more { font-size: 0.75rem; color: var(--raavi-text-muted); }
.week { display: grid; grid-template-columns: repeat(auto-fit, minmax(8rem, 1fr)); gap: var(--raavi-space-2); }
.week-col { border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: var(--raavi-space-2); min-block-size: 12rem; }
.agenda { display: grid; gap: var(--raavi-space-2); }
.form { display: grid; gap: var(--raavi-space-2); }
.pair { display: grid; grid-template-columns: 1fr 1fr; gap: var(--raavi-space-3); }
.pair > div { display: grid; gap: var(--raavi-space-1); align-content: start; }
.inline { display: flex; align-items: center; gap: var(--raavi-space-2); }
.days, .reminders { display: flex; gap: var(--raavi-space-3); flex-wrap: wrap; border: 0; padding: 0; margin: 0; }
.buttons { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.muted { color: var(--raavi-text-muted); margin: 0; }
@media (max-width: 50rem) {
  .layout { grid-template-columns: 1fr; }
  .day { min-block-size: 4.5rem; }
  .event .time, .event .space { display: none; }
  .pair { grid-template-columns: 1fr; }
}
</style>
