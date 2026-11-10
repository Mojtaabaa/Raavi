<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Select from 'primevue/select'
import DataTable from 'primevue/datatable'
import Column from 'primevue/column'
import { promptRunApi, type Analytics } from '@/core/api/prompts'
import { errorMessage } from '@/core/api/errors'

/** Workspace-wide prompt usage for administrators: runs, copies and confirmed uses are separate columns (a copy is not a run). */
const { t, te } = useI18n()
const toast = useToast()
const days = ref(30)
const data = ref<Analytics | null>(null)
const options = [7, 30, 90, 365].map((d) => ({ value: d, label: `${d}` }))

async function load() {
  try {
    const from = new Date(Date.now() - days.value * 86_400_000).toISOString()
    data.value = await promptRunApi.analytics(from)
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
}
watch(days, load)
onMounted(load)
</script>

<template>
  <section class="analytics" data-testid="prompt-analytics">
    <div class="head">
      <h2>{{ t('prompts.analytics.title') }}</h2>
      <label>{{ t('prompts.analytics.lastDays') }} <Select v-model="days" :options="options" option-label="label" option-value="value" size="small" /></label>
    </div>
    <dl v-if="data" class="totals">
      <dt>{{ t('prompts.stats.automatic') }}</dt><dd>{{ data.automatic }}</dd>
      <dt>{{ t('prompts.stats.successful') }}</dt><dd>{{ data.successful }}</dd>
      <dt>{{ t('prompts.stats.failed') }}</dt><dd>{{ data.failed }}</dd>
      <dt>{{ t('prompts.stats.copies') }}</dt><dd>{{ data.copies }}</dd>
      <dt>{{ t('prompts.stats.confirmed') }}</dt><dd>{{ data.confirmedManual }}</dd>
      <dt>{{ t('prompts.stats.users') }}</dt><dd>{{ data.users }}</dd>
      <dt>{{ t('prompts.stats.tokens') }}</dt><dd>{{ data.tokens }}</dd>
    </dl>
    <DataTable v-if="data" :value="data.prompts" data-key="promptId" size="small" striped-rows>
      <template #empty>{{ t('common.empty') }}</template>
      <Column field="title" :header="t('prompts.fields.title')" />
      <Column field="ownerName" :header="t('prompts.analytics.owner')" />
      <Column field="automatic" :header="t('prompts.stats.automatic')" />
      <Column field="failed" :header="t('prompts.stats.failed')" />
      <Column field="copies" :header="t('prompts.stats.copies')" />
      <Column field="confirmedManual" :header="t('prompts.stats.confirmed')" />
      <Column field="users" :header="t('prompts.stats.users')" />
      <Column field="tokens" :header="t('prompts.stats.tokens')" />
    </DataTable>
  </section>
</template>

<style scoped>
.analytics { display: grid; gap: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-md, 8px); padding: var(--raavi-space-3); }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-2); align-items: center; flex-wrap: wrap; }
h2 { margin: 0; font-size: 1.1rem; }
.totals { display: grid; grid-template-columns: repeat(auto-fit, minmax(10rem, 1fr)); gap: 0.4rem 1rem; margin: 0; }
.totals dt { color: var(--raavi-text-muted); font-size: 0.85rem; }
.totals dd { margin: 0; font-weight: 700; font-size: 1.2rem; }
</style>
