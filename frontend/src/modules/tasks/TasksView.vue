<script setup lang="ts">
import CalendarDateInput from '@/app/CalendarDateInput.vue'
import UserAvatar from '@/app/UserAvatar.vue'
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Textarea from 'primevue/textarea'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { tasksApi, type Task } from '@/core/api/tasks'
import { wikiApi, contentRoute, type Space } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import PrincipalPicker from '@/modules/wiki/components/PrincipalPicker.vue'

/**
 * Tasks (Phase 9a). Under a space it lists that space's tasks (create/edit/export need Edit); at /tasks it lists the signed-in
 * member's assigned tasks across every space they can still view. Dates are calendar dates, shown in the active calendar.
 */
const { t, te } = useI18n()
const route = useRoute()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()

const spaceKey = computed(() => (typeof route.params.key === 'string' ? route.params.key : null))
const tasks = ref<Task[]>([])
const space = ref<Space | null>(null)
const loading = ref(true)
const filter = ref<'Open' | 'Done' | 'All'>('Open')
const filters = computed(() => [
  { value: 'Open', label: t('tasks.filterOpen') },
  { value: 'Done', label: t('tasks.filterDone') },
  { value: 'All', label: t('tasks.filterAll') },
])

const editing = ref<Task | 'new' | null>(null)
const form = ref({ title: '', notes: '', dueDate: '', assigneeId: null as string | null, assigneeName: '' })
const saving = ref(false)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const canCreate = computed(() => !!spaceKey.value && !!space.value?.rights.canCreate)
const dueLabel = (d: string | null) => (d ? formatDate(`${d}T00:00:00Z`, locale.locale, { dateStyle: 'medium', timeZone: 'UTC' }) : '')

async function load() {
  loading.value = true
  try {
    const status = filter.value === 'All' ? undefined : filter.value
    if (spaceKey.value) {
      const [list, sp] = await Promise.all([tasksApi.list(spaceKey.value, status), space.value?.key === spaceKey.value ? Promise.resolve({ value: space.value }) : wikiApi.space(spaceKey.value)])
      tasks.value = list
      space.value = sp.value
    } else {
      tasks.value = await tasksApi.mine(status)
    }
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}
onMounted(load)
watch([filter, spaceKey], load)

async function toggle(task: Task) {
  try {
    const updated = await (task.status === 'Open' ? tasksApi.complete(task.id) : tasksApi.reopen(task.id))
    tasks.value = tasks.value.map((x) => (x.id === updated.id ? updated : x))
    if (filter.value !== 'All') tasks.value = tasks.value.filter((x) => x.status === filter.value)
  } catch (e) {
    fail(e)
    await load()
  }
}

function openEditor(task: Task | 'new') {
  editing.value = task
  form.value = task === 'new'
    ? { title: '', notes: '', dueDate: '', assigneeId: null, assigneeName: '' }
    : { title: task.title, notes: task.notes ?? '', dueDate: task.dueDate ?? '', assigneeId: task.assigneeId, assigneeName: task.assigneeName ?? '' }
}

function pickAssignee(s: { subjectType: string; subjectId: string | null; name: string }) {
  if (s.subjectType === 'User') {
    form.value.assigneeId = s.subjectId
    form.value.assigneeName = s.name
  }
}

async function save() {
  const target = editing.value
  if (!target || !form.value.title.trim()) return
  saving.value = true
  const body = { title: form.value.title.trim(), notes: form.value.notes.trim() || null, assigneeId: form.value.assigneeId, dueDate: form.value.dueDate || null, contentId: target === 'new' ? null : target.contentId }
  try {
    if (target === 'new') await tasksApi.create(spaceKey.value!, body)
    else await tasksApi.update(target, body)
    editing.value = null
    await load()
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}

function remove(task: Task) {
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('tasks.confirmDelete', { title: task.title }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await tasksApi.remove(task.id)
        tasks.value = tasks.value.filter((x) => x.id !== task.id)
      } catch (e) {
        fail(e)
      }
    },
  })
}
</script>

<template>
  <section class="tasks" data-testid="tasks">
    <header class="head">
      <h1>{{ spaceKey ? t('tasks.spaceTitle', { name: space?.name ?? spaceKey }) : t('tasks.myTitle') }}</h1>
      <div class="tools">
        <SelectButton v-model="filter" :options="filters" option-label="label" option-value="value" :allow-empty="false" :aria-label="t('tasks.filter')" />
        <Button v-if="canCreate" icon="pi pi-plus" :label="t('tasks.new')" data-testid="new-task" @click="openEditor('new')" />
        <Button v-if="spaceKey" as="a" :href="tasksApi.exportUrl(spaceKey)" icon="pi pi-download" text :label="t('tasks.export')" data-testid="export-tasks" />
      </div>
    </header>

    <p v-if="!loading && tasks.length === 0" class="muted" data-testid="no-tasks">{{ spaceKey ? t('tasks.noneSpace') : t('tasks.noneMine') }}</p>
    <ul class="list">
      <li v-for="task in tasks" :key="task.id" :class="['task', { done: task.status === 'Done' }]" data-testid="task">
        <Checkbox :model-value="task.status === 'Done'" binary :disabled="!task.rights.canComplete" :input-id="`t-${task.id}`" :aria-label="t(task.status === 'Done' ? 'tasks.reopen' : 'tasks.complete', { title: task.title })" @update:model-value="toggle(task)" />
        <div class="body">
          <label :for="`t-${task.id}`" class="title" dir="auto">{{ task.title }}</label>
          <p v-if="task.notes" class="notes" dir="auto">{{ task.notes }}</p>
          <p class="meta">
            <Tag v-if="task.isOverdue" severity="danger" :value="t('tasks.overdue')" />
            <span v-if="task.dueDate">{{ t('tasks.due', { date: dueLabel(task.dueDate) }) }}</span>
            <span v-if="task.assigneeName" class="who">· <UserAvatar :user-id="task.assigneeId" :name="task.assigneeName" :size="20" /> {{ task.assigneeName }}</span>
            <RouterLink v-if="!spaceKey" :to="{ name: 'space-tasks', params: { key: task.spaceKey } }">· {{ task.spaceName }}</RouterLink>
            <RouterLink v-if="task.contentId" :to="contentRoute({ id: task.contentId, spaceKey: task.spaceKey })">· {{ t('tasks.page') }}</RouterLink>
          </p>
        </div>
        <div v-if="task.rights.canEdit" class="actions">
          <Button text rounded size="small" icon="pi pi-pencil" :aria-label="t('common.edit')" @click="openEditor(task)" />
          <Button text rounded size="small" severity="danger" icon="pi pi-trash" :aria-label="t('common.delete')" @click="remove(task)" />
        </div>
      </li>
    </ul>

    <Dialog :visible="!!editing" modal :header="editing === 'new' ? t('tasks.new') : t('tasks.edit')" :style="{ inlineSize: 'min(32rem, 95vw)' }" @update:visible="(v) => !v && (editing = null)">
      <form class="form" @submit.prevent="save">
        <label for="task-title">{{ t('tasks.title') }}</label>
        <InputText id="task-title" v-model="form.title" maxlength="300" required data-testid="task-title" />
        <label for="task-notes">{{ t('tasks.notes') }}</label>
        <Textarea id="task-notes" v-model="form.notes" rows="3" maxlength="4000" auto-resize />
        <label for="task-due">{{ t('tasks.dueDate') }}</label>
        <CalendarDateInput id="task-due" v-model="form.dueDate" />
        <label for="task-assignee">{{ t('tasks.assignee') }}</label>
        <p v-if="form.assigneeId" class="muted">
          {{ form.assigneeName }}
          <Button text size="small" :label="t('tasks.unassign')" type="button" @click="(form.assigneeId = null), (form.assigneeName = '')" />
        </p>
        <PrincipalPicker input-id="task-assignee" @pick="pickAssignee" />
        <Button type="submit" :label="t('common.save')" :loading="saving" :disabled="!form.title.trim()" data-testid="save-task" />
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.tasks { display: grid; gap: var(--raavi-space-3); max-inline-size: 56rem; }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: center; }
h1 { margin: 0; font-size: 1.5rem; }
.tools { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.task { display: flex; gap: var(--raavi-space-3); align-items: flex-start; padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.task.done .title { text-decoration: line-through; color: var(--raavi-text-muted); }
.body { flex: 1; min-inline-size: 0; }
.title { font-weight: 600; overflow-wrap: anywhere; }
.notes { margin: var(--raavi-space-1) 0 0; color: var(--raavi-text-muted); white-space: pre-wrap; overflow-wrap: anywhere; }
.meta { margin: var(--raavi-space-1) 0 0; display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; font-size: 0.85rem; color: var(--raavi-text-muted); }
.actions { display: flex; }
.form { display: grid; gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); margin: 0; }
</style>
