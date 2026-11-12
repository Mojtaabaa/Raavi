<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Textarea from 'primevue/textarea'
import CalendarDateInput from '@/app/CalendarDateInput.vue'
import { tasksApi, type TaskTimeEntry, type TaskTimeSummary } from '@/core/api/tasks'
import { errorMessage } from '@/core/api/errors'
import { formatDuration } from '@/modules/tasks/duration'

const props = defineProps<{ taskId: string }>()
const emit = defineEmits<{ refreshed: [summary: TaskTimeSummary] }>()
const { t, te } = useI18n()
const toast = useToast()

const summary = ref<TaskTimeSummary | null>(null)
const loading = ref(true)
const busy = ref(false)
const workDate = ref('')
const duration = ref('')
const description = ref('')
const editing = ref<TaskTimeEntry | null>(null)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })

async function load() {
  loading.value = true
  try {
    summary.value = await tasksApi.time(props.taskId)
    emit('refreshed', summary.value)
    if (!workDate.value) {
      const now = new Date()
      workDate.value = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`
    }
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(() => props.taskId, load)

async function submit() {
  const minutes = Number.parseInt(duration.value.trim(), 10)
  if (!workDate.value || !Number.isFinite(minutes) || minutes < 1) {
    toast.add({ severity: 'warn', summary: t('tasks.timeDurationHint'), life: 4000 })
    return
  }
  busy.value = true
  try {
    if (editing.value) {
      await tasksApi.updateTime(editing.value, {
        workDate: workDate.value,
        durationMinutes: minutes,
        description: description.value.trim() || null,
      })
      editing.value = null
    } else {
      await tasksApi.logTime(props.taskId, {
        workDate: workDate.value,
        durationMinutes: minutes,
        description: description.value.trim() || null,
      })
    }
    duration.value = ''
    description.value = ''
    await load()
  } catch (e) {
    fail(e)
  } finally {
    busy.value = false
  }
}

function startEdit(e: TaskTimeEntry) {
  editing.value = e
  workDate.value = e.workDate
  duration.value = String(e.durationMinutes)
  description.value = e.description ?? ''
}

function cancelEdit() {
  editing.value = null
  duration.value = ''
  description.value = ''
}

async function remove(id: string) {
  try {
    await tasksApi.deleteTime(id)
    await load()
  } catch (e) {
    fail(e)
  }
}
</script>

<template>
  <section class="time" data-testid="task-time">
    <h3>{{ t('tasks.timeTracking') }}</h3>
    <p v-if="loading" class="muted">{{ t('common.loading') }}</p>
    <template v-else-if="summary">
      <p class="meta" data-testid="task-time-total">
        {{ t('tasks.timeTotal', { value: formatDuration(summary.totalMinutes, t) }) }}
        <span v-if="summary.openingBalanceMinutes" class="muted">
          · {{ t('tasks.timeOpening', { value: formatDuration(summary.openingBalanceMinutes, t) }) }}
        </span>
      </p>
      <p v-if="!summary.actualSpentMatchesEntries" class="warn" data-testid="task-time-mismatch">
        {{ t('tasks.timeMismatch') }}
      </p>
      <ul v-if="summary.byUser.length" class="by-user" data-testid="task-time-by-user">
        <li v-for="u in summary.byUser" :key="u.userId">
          {{ u.userName ?? u.userId }} — {{ formatDuration(u.durationMinutes, t) }}
        </li>
      </ul>
      <p v-if="!summary.items.length" class="muted" data-testid="task-time-empty">{{ t('tasks.noTimeEntries') }}</p>
      <ul class="list" data-testid="task-time-entries">
        <li v-for="e in summary.items" :key="e.id" class="item">
          <div class="row">
            <strong>{{ e.userName ?? e.userId }}</strong>
            <span>{{ e.workDate }}</span>
            <span>{{ formatDuration(e.durationMinutes, t) }}</span>
            <span v-if="e.isOpeningBalance" class="opening">{{ t('tasks.timeOpeningBalance') }}</span>
          </div>
          <p v-if="e.description" class="desc" dir="auto">{{ e.description }}</p>
          <div class="row">
            <Button v-if="e.canEdit && !e.isOpeningBalance" type="button" size="small" text :label="t('common.edit')" data-testid="edit-time" @click="startEdit(e)" />
            <Button v-if="e.canDelete" type="button" size="small" text severity="danger" :label="t('common.delete')" data-testid="delete-time" @click="remove(e.id)" />
          </div>
        </li>
      </ul>
      <form v-if="summary.canLog || summary.canManageOthers" class="form" data-testid="task-time-form" @submit.prevent="submit">
        <label for="time-date">{{ t('tasks.timeDate') }}</label>
        <CalendarDateInput id="time-date" v-model="workDate" />
        <label for="time-duration">{{ t('tasks.timeDuration') }}</label>
        <InputText id="time-duration" v-model="duration" inputmode="numeric" data-testid="time-duration" :placeholder="t('tasks.timeDurationHint')" />
        <label for="time-note">{{ t('tasks.timeDescription') }}</label>
        <Textarea id="time-note" v-model="description" rows="2" maxlength="1000" auto-resize />
        <div class="row">
          <Button type="submit" size="small" :label="editing ? t('common.save') : t('tasks.logTime')" :loading="busy" data-testid="save-time" />
          <Button v-if="editing" type="button" size="small" text :label="t('common.cancel')" @click="cancelEdit" />
        </div>
      </form>
    </template>
  </section>
</template>

<style scoped>
.time { display: grid; gap: var(--raavi-space-2); margin-block-start: var(--raavi-space-3); border-block-start: 1px solid var(--raavi-border); padding-block-start: var(--raavi-space-3); }
.form { display: grid; gap: var(--raavi-space-2); }
.list, .by-user { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.item { display: grid; gap: 0.25rem; }
.row { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.meta, .muted, .opening { color: var(--raavi-text-muted); margin: 0; }
.warn { color: var(--raavi-danger, var(--raavi-text-muted)); margin: 0; font-size: 0.875rem; }
.desc { margin: 0; overflow-wrap: anywhere; }
h3 { margin: 0; font-size: 1rem; }
.opening { font-size: 0.8rem; }
</style>
