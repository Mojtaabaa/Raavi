<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputNumber from 'primevue/inputnumber'
import InputText from 'primevue/inputtext'
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Textarea from 'primevue/textarea'
import { calendarApi, type CalendarEvent, type CalendarEventInput, type Frequency, type Occurrence, type RecurrenceCalendar } from '@/core/api/calendar'
import { wikiApi, type Space } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { LOCALES } from '@/core/i18n/locales'
import { useHostStore } from '@/stores/host'
import { useLocaleStore } from '@/stores/locale'
import { addDays, dateLabel, monthGrid, monthLabel, shiftMonth, toIso, weekdayLabel, weekdayOf, type CalendarSystem } from './calendarMath'

/**
 * Calendar (Phase 9b). Under a space it shows that space's events (create/edit/export need Edit); at /calendar it shows the events of every
 * space the member can view. The month grid follows the chosen calendar (Jalali or Gregorian); recurrence is expanded by the server.
 */
const { t, te } = useI18n()
const route = useRoute()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()
const host = useHostStore()

const spaceKey = computed(() => (typeof route.params.key === 'string' ? route.params.key : null))
const intlTag = computed(() => LOCALES[locale.locale].intlTag)
const space = ref<Space | null>(null)
const calendar = ref<CalendarSystem>(host.tenant?.calendar === 'gregorian' ? 'gregory' : host.tenant?.calendar === 'persian' ? 'persian' : LOCALES[locale.locale].defaultCalendar)
const calendars = computed(() => [
  { value: 'persian', label: t('calendar.jalali') },
  { value: 'gregory', label: t('calendar.gregorian') },
])
const anchor = ref(toIso(new Date()))
const occurrences = ref<Occurrence[]>([])
const loading = ref(true)

const weekStart = computed(() => (calendar.value === 'persian' || locale.locale === 'fa' ? 6 : 0))
const grid = computed(() => monthGrid(anchor.value, calendar.value, weekStart.value))
const title = computed(() => monthLabel(anchor.value, calendar.value, intlTag.value))
const weekdays = computed(() => grid.value.slice(0, 7).map((d) => ({ iso: d.iso, label: weekdayLabel(d.iso, intlTag.value), long: weekdayLabel(d.iso, intlTag.value, 'long') })))
const today = toIso(new Date())
const rtl = computed(() => LOCALES[locale.locale].direction === 'rtl')
const num = (n: number) => new Intl.NumberFormat(intlTag.value).format(n)
const canCreate = computed(() => !!spaceKey.value && !!space.value?.rights.canCreate)

const byDay = computed(() => {
  const map = new Map<string, Occurrence[]>()
  const first = grid.value[0]!.iso
  const last = grid.value[grid.value.length - 1]!.iso
  for (const o of occurrences.value) {
    for (let d = o.startDate < first ? first : o.startDate; d <= o.endDate && d <= last; d = addDays(d, 1)) {
      const list = map.get(d) ?? []
      list.push(o)
      map.set(d, list)
    }
  }
  return map
})

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const timeLabel = (o: Occurrence) => (o.allDay ? t('calendar.allDay') : (o.startTime ?? ''))

async function load() {
  loading.value = true
  try {
    const g = grid.value
    const from = g[0]!.iso
    const to = g[g.length - 1]!.iso
    if (spaceKey.value && space.value?.key !== spaceKey.value) space.value = (await wikiApi.space(spaceKey.value)).value
    occurrences.value = await calendarApi.occurrences(spaceKey.value, from, to)
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}
onMounted(load)
watch([anchor, calendar, spaceKey], load)

const go = (delta: number) => (anchor.value = shiftMonth(anchor.value, calendar.value, delta))
const goToday = () => (anchor.value = toIso(new Date()))

// ── Editor ────────────────────────────────────────────────────────────────────────────────────

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
  }
}

function fromEvent(e: CalendarEvent): Form {
  const r = e.recurrence
  return {
    title: e.title, description: e.description ?? '', location: e.location ?? '', allDay: e.allDay, startDate: e.startDate, startTime: e.startTime ?? '09:00', endDate: e.endDate,
    endTime: e.endTime ?? '10:00', timeZone: e.timeZone, frequency: r?.frequency ?? 'None', interval: r?.interval ?? 1, weekDays: r?.weekDays.length ? [...r.weekDays] : [weekdayOf(e.startDate)],
    recurCalendar: r?.calendar ?? (calendar.value === 'persian' ? 'Persian' : 'Gregorian'), endMode: r?.until ? 'until' : r?.count ? 'count' : 'never', until: r?.until ?? '', count: r?.count ?? 10,
  }
}

function newEvent(date: string = anchor.value) {
  form.value = blank(date)
  occurrenceDate.value = null
  readOnly.value = false
  editing.value = 'new'
}

async function openOccurrence(o: Occurrence) {
  try {
    const ev = await calendarApi.event(o.eventId)
    form.value = fromEvent(ev)
    occurrenceDate.value = o.isRecurring ? o.occurrenceDate : null
    readOnly.value = !ev.canEdit
    editing.value = ev
  } catch (e) {
    fail(e)
  }
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

function toggleDay(index: number, on: boolean) {
  const set = new Set(form.value.weekDays)
  if (on) set.add(index)
  else set.delete(index)
  form.value.weekDays = [...set].sort()
}

const isRecurring = computed(() => form.value.frequency !== 'None')
const preview = (iso: string) => (iso ? dateLabel(iso, calendar.value, intlTag.value) : '')

function body(): CalendarEventInput {
  const f = form.value
  const days = f.frequency === 'Weekly' ? [...new Set([...f.weekDays, startWeekday.value])].sort() : []
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
      ? { frequency: f.frequency, interval: Math.max(1, Math.floor(Number(f.interval) || 1)), weekDays: days, calendar: f.recurCalendar, until: f.endMode === 'until' && f.until ? f.until : null, count: f.endMode === 'count' ? Math.max(1, Math.floor(Number(f.count) || 1)) : null }
      : null,
  }
}

async function save() {
  const target = editing.value
  if (!target || !form.value.title.trim() || !form.value.startDate) return
  saving.value = true
  try {
    if (target === 'new') await calendarApi.create(spaceKey.value!, body())
    else await calendarApi.update(target, body())
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
</script>

<template>
  <section class="calendar" data-testid="calendar">
    <header class="head">
      <h1>{{ spaceKey ? t('calendar.spaceTitle', { name: space?.name ?? spaceKey }) : t('calendar.title') }}</h1>
      <div class="tools">
        <SelectButton v-model="calendar" :options="calendars" option-label="label" option-value="value" :allow-empty="false" :aria-label="t('calendar.system')" data-testid="calendar-system" />
        <Button v-if="canCreate" icon="pi pi-plus" :label="t('calendar.new')" data-testid="new-event" @click="newEvent()" />
        <Button v-if="spaceKey" as="a" :href="calendarApi.exportUrl(spaceKey)" icon="pi pi-download" text :label="t('calendar.export')" data-testid="export-calendar" />
      </div>
    </header>

    <nav class="nav" :aria-label="t('calendar.navigate')">
      <Button :icon="rtl ? 'pi pi-chevron-right' : 'pi pi-chevron-left'" text rounded :aria-label="t('calendar.previous')" data-testid="prev-month" @click="go(-1)" />
      <Button :icon="rtl ? 'pi pi-chevron-left' : 'pi pi-chevron-right'" text rounded :aria-label="t('calendar.next')" data-testid="next-month" @click="go(1)" />
      <strong class="month" data-testid="month-title" aria-live="polite">{{ title }}</strong>
      <Button :label="t('calendar.today')" text size="small" data-testid="today" @click="goToday" />
    </nav>

    <div class="grid" role="grid" :aria-label="title" :aria-busy="loading">
      <div v-for="w in weekdays" :key="w.iso" class="weekday" role="columnheader" :abbr="w.long">{{ w.label }}</div>
      <div v-for="day in grid" :key="day.iso" :class="['day', { muted: !day.inMonth, today: day.iso === today }]" role="gridcell" :data-date="day.iso">
        <button v-if="canCreate" type="button" class="num" :aria-label="t('calendar.addOn', { date: preview(day.iso) })" @click="newEvent(day.iso)">{{ num(day.day) }}</button>
        <span v-else class="num">{{ num(day.day) }}</span>
        <ul class="events">
          <li v-for="o in (byDay.get(day.iso) ?? []).slice(0, 3)" :key="`${o.eventId}-${o.occurrenceDate}`">
            <button type="button" class="event" data-testid="occurrence" :title="o.title" @click="openOccurrence(o)">
              <span class="time">{{ timeLabel(o) }}</span> <span class="name" dir="auto">{{ o.title }}</span>
              <span v-if="!spaceKey" class="space">· {{ o.spaceName }}</span>
            </button>
          </li>
          <li v-if="(byDay.get(day.iso)?.length ?? 0) > 3" class="more">{{ t('calendar.more', { count: num((byDay.get(day.iso)?.length ?? 0) - 3) }) }}</li>
        </ul>
      </div>
    </div>
    <p v-if="!loading && occurrences.length === 0" class="muted" data-testid="no-events">{{ t('calendar.none') }}</p>

    <Dialog :visible="!!editing" modal :header="editing === 'new' ? t('calendar.new') : readOnly ? t('calendar.details') : t('calendar.edit')" :style="{ inlineSize: 'min(36rem, 95vw)' }" @update:visible="(v) => !v && (editing = null)">
      <form class="form" @submit.prevent="save">
        <label for="ev-title">{{ t('calendar.eventTitle') }}</label>
        <InputText id="ev-title" v-model="form.title" maxlength="200" required :disabled="readOnly" data-testid="event-title" />

        <label class="inline"><Checkbox v-model="form.allDay" binary input-id="ev-allday" :disabled="readOnly" /> <span>{{ t('calendar.allDay') }}</span></label>

        <div class="pair">
          <div>
            <label for="ev-start">{{ t('calendar.starts') }}</label>
            <InputText id="ev-start" v-model="form.startDate" type="date" dir="ltr" required :disabled="readOnly" data-testid="event-start" />
            <small class="muted" data-testid="start-label">{{ preview(form.startDate) }}</small>
            <InputText v-if="!form.allDay" v-model="form.startTime" type="time" dir="ltr" :aria-label="t('calendar.startTime')" :disabled="readOnly" />
          </div>
          <div>
            <label for="ev-end">{{ t('calendar.ends') }}</label>
            <InputText id="ev-end" v-model="form.endDate" type="date" dir="ltr" :min="form.startDate" :disabled="readOnly" data-testid="event-end" />
            <small class="muted">{{ preview(form.endDate) }}</small>
            <InputText v-if="!form.allDay" v-model="form.endTime" type="time" dir="ltr" :aria-label="t('calendar.endTime')" :disabled="readOnly" />
          </div>
        </div>
        <small class="muted" dir="ltr">{{ form.timeZone }}</small>

        <label for="ev-location">{{ t('calendar.location') }}</label>
        <InputText id="ev-location" v-model="form.location" maxlength="200" :disabled="readOnly" />
        <label for="ev-description">{{ t('calendar.description') }}</label>
        <Textarea id="ev-description" v-model="form.description" rows="3" maxlength="4000" auto-resize :disabled="readOnly" />

        <label for="ev-repeat">{{ t('calendar.repeat') }}</label>
        <Select v-model="form.frequency" input-id="ev-repeat" :options="frequencies" option-label="label" option-value="value" :disabled="readOnly" data-testid="event-repeat" />

        <template v-if="isRecurring">
          <label for="ev-interval">{{ t('calendar.every') }}</label>
          <InputNumber v-model="form.interval" input-id="ev-interval" :min="1" :max="99" :use-grouping="false" :disabled="readOnly" data-testid="event-interval" />

          <fieldset v-if="form.frequency === 'Weekly'" class="days">
            <legend>{{ t('calendar.onDays') }}</legend>
            <label v-for="w in weekdayChoices" :key="w.index" class="inline">
              <Checkbox :model-value="form.weekDays.includes(w.index) || w.index === startWeekday" binary :disabled="readOnly || w.index === startWeekday" :input-id="`ev-day-${w.index}`" @update:model-value="(v: boolean) => toggleDay(w.index, v)" />
              <span>{{ w.label }}</span>
            </label>
          </fieldset>

          <template v-if="form.frequency === 'Monthly' || form.frequency === 'Yearly'">
            <label for="ev-recal">{{ t('calendar.repeatIn') }}</label>
            <SelectButton v-model="form.recurCalendar" input-id="ev-recal" :options="recurCalendars" option-label="label" option-value="value" :allow-empty="false" :disabled="readOnly" data-testid="event-recur-calendar" />
          </template>

          <label>{{ t('calendar.endsRule') }}</label>
          <SelectButton v-model="form.endMode" :options="endModes" option-label="label" option-value="value" :allow-empty="false" :disabled="readOnly" />
          <InputText v-if="form.endMode === 'until'" v-model="form.until" type="date" dir="ltr" :min="form.startDate" :aria-label="t('calendar.endUntil')" :disabled="readOnly" />
          <InputNumber v-if="form.endMode === 'count'" v-model="form.count" :min="1" :max="1000" :use-grouping="false" :aria-label="t('calendar.endCount')" :disabled="readOnly" />
        </template>

        <div class="buttons">
          <Button v-if="!readOnly" type="submit" :label="t('common.save')" :loading="saving" :disabled="!form.title.trim() || !form.startDate" data-testid="save-event" />
          <Button v-if="!readOnly && occurrenceDate" type="button" severity="secondary" outlined :label="t('calendar.cancelOccurrence')" data-testid="cancel-occurrence" @click="cancelThis" />
          <Button v-if="!readOnly && editing !== 'new'" type="button" severity="danger" text :label="t('common.delete')" data-testid="delete-event" @click="askRemove" />
        </div>
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.calendar { display: grid; gap: var(--raavi-space-3); }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: center; }
h1 { margin: 0; font-size: 1.5rem; }
.tools, .nav { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.month { min-inline-size: 10rem; font-size: 1.1rem; }
.grid { display: grid; grid-template-columns: repeat(7, minmax(0, 1fr)); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); overflow: hidden; }
.weekday { padding: var(--raavi-space-2); text-align: center; font-weight: 600; font-size: 0.85rem; background: var(--raavi-surface-muted, transparent); border-block-end: 1px solid var(--raavi-border); }
.day { min-block-size: 6.5rem; padding: var(--raavi-space-1); border-inline-end: 1px solid var(--raavi-border); border-block-end: 1px solid var(--raavi-border); min-inline-size: 0; }
.day.muted { color: var(--raavi-text-muted); background: var(--raavi-surface-muted, transparent); }
.day.today .num { background: var(--raavi-primary, #2563eb); color: #fff; border-radius: 999px; }
.num { display: inline-block; min-inline-size: 1.6rem; padding: 0 0.3rem; text-align: center; border: 0; background: none; color: inherit; font: inherit; cursor: default; }
button.num { cursor: pointer; }
.events { list-style: none; margin: 0; padding: 0; display: grid; gap: 2px; }
.event { inline-size: 100%; text-align: start; border: 0; border-radius: 4px; padding: 1px 4px; background: color-mix(in srgb, var(--raavi-primary, #2563eb) 15%, transparent); color: inherit; font: inherit; font-size: 0.8rem; cursor: pointer; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.event .time, .event .space { color: var(--raavi-text-muted); }
.more { font-size: 0.75rem; color: var(--raavi-text-muted); }
.form { display: grid; gap: var(--raavi-space-2); }
.pair { display: grid; grid-template-columns: 1fr 1fr; gap: var(--raavi-space-3); }
.pair > div { display: grid; gap: var(--raavi-space-1); align-content: start; }
.inline { display: flex; align-items: center; gap: var(--raavi-space-2); }
.days { display: flex; gap: var(--raavi-space-3); flex-wrap: wrap; border: 0; padding: 0; margin: 0; }
.buttons { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.muted { color: var(--raavi-text-muted); margin: 0; }
@media (max-width: 40rem) { .day { min-block-size: 4.5rem; } .event .time, .event .space { display: none; } .pair { grid-template-columns: 1fr; } }
</style>
