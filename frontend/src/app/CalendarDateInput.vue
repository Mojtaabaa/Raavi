<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { display } from '@/core/runtime/display'
import { LOCALES } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import { daysInMonth, isoFromParts, partsOf, type CalendarSystem } from '@/modules/calendar/calendarMath'

/**
 * A date (and optionally time) field that follows the workspace's calendar: the Persian calendar shows year / month name / day pickers, the Gregorian
 * calendar the browser's date field. The value is always an ISO date (yyyy-MM-dd) or, with <c>time</c>, yyyy-MM-ddTHH:mm — whatever calendar is shown.
 */
defineOptions({ inheritAttrs: false })
const props = defineProps<{ modelValue: string; time?: boolean; id?: string; label?: string; min?: string; required?: boolean; disabled?: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [string] }>()
const { t } = useI18n()
const locale = useLocaleStore()

const cal = computed<CalendarSystem>(() => display.calendar ?? LOCALES[locale.locale].defaultCalendar)
const datePart = computed(() => props.modelValue.slice(0, 10))
const timePart = computed(() => (props.modelValue.length > 11 ? props.modelValue.slice(11, 16) : '00:00'))
const compose = (d: string, tm: string) => (props.time ? (d ? `${d}T${tm || '00:00'}` : '') : d)

const parts = computed(() => (datePart.value ? partsOf(datePart.value, cal.value) : null))
const today = computed(() => partsOf(new Date().toISOString().slice(0, 10), cal.value))
const years = computed(() => {
  const base = parts.value?.y ?? today.value.y
  return Array.from({ length: 31 }, (_, i) => base - 15 + i)
})
const monthLabel = (m: number) => {
  const y = parts.value?.y ?? today.value.y
  const iso = isoFromParts(y, m, 1, cal.value)
  return new Intl.DateTimeFormat(`${LOCALES[locale.locale].intlTag}-u-ca-${cal.value}`, { timeZone: 'UTC', month: 'long' }).format(new Date(`${iso}T12:00:00Z`))
}
const dayCount = computed(() => (parts.value ? daysInMonth(isoFromParts(parts.value.y, parts.value.m, 1, cal.value), cal.value) : 31))
const nf = (n: number) => new Intl.NumberFormat(LOCALES[locale.locale].intlTag).format(n)

function setParts(y: number, m: number, d: number) {
  const first = isoFromParts(y, m, 1, cal.value)
  const max = daysInMonth(first, cal.value)
  emit('update:modelValue', compose(isoFromParts(y, m, Math.min(d, max), cal.value), timePart.value))
}
const onY = (e: Event) => setParts(+(e.target as HTMLSelectElement).value, parts.value?.m ?? today.value.m, parts.value?.d ?? today.value.d)
const onM = (e: Event) => setParts(parts.value?.y ?? today.value.y, +(e.target as HTMLSelectElement).value, parts.value?.d ?? 1)
const onD = (e: Event) => setParts(parts.value?.y ?? today.value.y, parts.value?.m ?? today.value.m, +(e.target as HTMLSelectElement).value)
const onNative = (e: Event) => emit('update:modelValue', compose((e.target as HTMLInputElement).value, timePart.value))
const onTime = (e: Event) => emit('update:modelValue', compose(datePart.value || new Date().toISOString().slice(0, 10), (e.target as HTMLInputElement).value))
</script>

<template>
  <span class="cdi" :data-calendar="cal" data-testid="calendar-date">
    <input v-if="cal === 'gregory'" v-bind="$attrs" :id="id" type="date" :value="datePart" :min="min" :required="required" :disabled="disabled" :aria-label="label" dir="ltr" @input="onNative" />
    <span v-else class="jalali" role="group" :aria-label="label ?? t('calendarInput.date')">
      <select v-bind="$attrs" :id="id" :value="parts?.d ?? ''" :disabled="disabled" :aria-label="t('calendarInput.day')" data-testid="cdi-day" @change="onD">
        <option v-if="!parts" value="" />
        <option v-for="d in dayCount" :key="d" :value="d">{{ nf(d) }}</option>
      </select>
      <select :value="parts?.m ?? ''" :disabled="disabled" :aria-label="t('calendarInput.month')" data-testid="cdi-month" @change="onM">
        <option v-if="!parts" value="" />
        <option v-for="m in 12" :key="m" :value="m">{{ monthLabel(m) }}</option>
      </select>
      <select :value="parts?.y ?? ''" :disabled="disabled" :aria-label="t('calendarInput.year')" data-testid="cdi-year" @change="onY">
        <option v-if="!parts" :value="''">{{ nf(today.y) }}</option>
        <option v-for="y in years" :key="y" :value="y">{{ nf(y).replace(/[,٬]/g, '') }}</option>
      </select>
    </span>
    <input v-if="time" type="time" :value="timePart" :disabled="disabled" :aria-label="t('calendarInput.time')" dir="ltr" @input="onTime" />
  </span>
</template>

<style scoped>
.cdi { display: inline-flex; gap: 0.35rem; align-items: center; flex-wrap: wrap; }
.jalali { display: inline-flex; gap: 0.25rem; }
input, select { font: inherit; padding: 0.4rem 0.5rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: var(--raavi-surface); color: inherit; }
</style>
