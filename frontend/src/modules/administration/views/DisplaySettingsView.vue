<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Select from 'primevue/select'
import { api } from '@/core/api/http'
import { errorMessage } from '@/core/api/errors'
import { setDisplay, timeZoneList } from '@/core/runtime/display'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import { useHostStore } from '@/stores/host'

/** Administration → Display: the time zone, calendar and default language the whole workspace uses (ADR-053). */
const { t, te } = useI18n()
const toast = useToast()
const locale = useLocaleStore()
const host = useHostStore()
const form = ref({ timeZone: 'Asia/Tehran', calendar: 'persian', defaultLocale: 'fa' })
const saving = ref(false)
const zones = computed(() => timeZoneList().map((z) => ({ value: z, label: z })))
const calendars = computed(() => [{ value: 'persian', label: t('display.persian') }, { value: 'gregorian', label: t('display.gregorian') }])
const locales = [{ value: 'fa', label: 'فارسی' }, { value: 'en', label: 'English' }]
const preview = computed(() => {
  try {
    return new Intl.DateTimeFormat(`${form.value.defaultLocale === 'fa' ? 'fa-IR' : 'en-GB'}-u-ca-${form.value.calendar === 'persian' ? 'persian' : 'gregory'}`, { dateStyle: 'full', timeStyle: 'long', timeZone: form.value.timeZone }).format(new Date())
  } catch {
    return ''
  }
})

onMounted(async () => {
  try {
    const s = await api<{ defaultLocale: string; timeZone: string; calendar: string }>('GET', '/tenant/settings')
    form.value = { timeZone: s.timeZone, calendar: s.calendar, defaultLocale: s.defaultLocale }
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
})

async function save() {
  saving.value = true
  try {
    const s = await api<{ timeZone: string; calendar: string }>('PUT', '/tenant/settings', form.value)
    setDisplay(s.timeZone, s.calendar)
    const ctx = host.context
    if (ctx?.kind === 'tenant') {
      ctx.tenant.timeZone = s.timeZone
      ctx.tenant.calendar = s.calendar as 'persian' | 'gregorian'
      ctx.tenant.defaultLocale = form.value.defaultLocale
    }
    toast.add({ severity: 'success', summary: t('display.saved'), life: 3000 })
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  } finally {
    saving.value = false
  }
}
void formatDate
void locale
</script>

<template>
  <section class="page" data-testid="display-settings">
    <h1>{{ t('display.title') }}</h1>
    <p class="muted">{{ t('display.intro') }}</p>
    <form class="form" @submit.prevent="save">
      <label for="d-tz">{{ t('display.timeZone') }}</label>
      <Select v-model="form.timeZone" input-id="d-tz" :options="zones" option-label="label" option-value="value" filter data-testid="display-tz" />
      <label for="d-cal">{{ t('display.calendar') }}</label>
      <Select v-model="form.calendar" input-id="d-cal" :options="calendars" option-label="label" option-value="value" data-testid="display-calendar" />
      <label for="d-loc">{{ t('display.language') }}</label>
      <Select v-model="form.defaultLocale" input-id="d-loc" :options="locales" option-label="label" option-value="value" />
      <p class="preview" data-testid="display-preview"><strong>{{ t('display.preview') }}:</strong> {{ preview }}</p>
      <Button type="submit" :label="t('common.save')" :loading="saving" data-testid="display-save" />
    </form>
  </section>
</template>

<style scoped>
.page { max-inline-size: 40rem; margin-inline: auto; display: grid; gap: var(--raavi-space-3); }
h1 { margin: 0; font-size: 1.4rem; }
.form { display: grid; gap: var(--raavi-space-2); }
label { font-weight: 600; font-size: 0.9rem; }
.muted { color: var(--raavi-text-muted); margin: 0; }
.preview { margin: var(--raavi-space-2) 0; padding: var(--raavi-space-2); border: 1px dashed var(--raavi-border); border-radius: var(--raavi-radius-sm); }
</style>
