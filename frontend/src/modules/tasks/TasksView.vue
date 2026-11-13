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
import Select from 'primevue/select'
import SelectButton from 'primevue/selectbutton'
import Tag from 'primevue/tag'
import { tasksApi, type Task, type TaskAssigneeRole, type TaskChecklist, type TaskPriority } from '@/core/api/tasks'
import { wikiApi, contentRoute, type Space } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import PrincipalPicker from '@/modules/wiki/components/PrincipalPicker.vue'
import TaskCollaborationPanel from '@/modules/tasks/TaskCollaborationPanel.vue'
import TaskTimePanel from '@/modules/tasks/TaskTimePanel.vue'
import { formatDuration } from '@/modules/tasks/duration'

/**
 * Tasks (Phase 9a + ADR-056). Under a space: create/edit/export need Edit; archive soft-hides.
 * At /tasks: assigned tasks (any role) across viewable spaces. Dates are calendar dates.
 */
const { t, te } = useI18n()
const route = useRoute()
const toast = useToast()
const confirm = useConfirm()
const locale = useLocaleStore()

type FormAssignee = { userId: string; name: string; role: TaskAssigneeRole; isPrimary: boolean }
const roleOptions = computed(() =>
  (['Responsible', 'Contributor', 'Reviewer', 'Approver', 'Observer'] as TaskAssigneeRole[]).map((value) => ({
    value,
    label: t(`tasks.role${value}`),
  })),
)

const spaceKey = computed(() => (typeof route.params.key === 'string' ? route.params.key : null))
const tasks = ref<Task[]>([])
const space = ref<Space | null>(null)
const loading = ref(true)
const filter = ref<'Open' | 'Done' | 'All'>('Open')
const showArchived = ref(false)
const filters = computed(() => [
  { value: 'Open', label: t('tasks.filterOpen') },
  { value: 'Done', label: t('tasks.filterDone') },
  { value: 'All', label: t('tasks.filterAll') },
])
const priorities = computed(() =>
  (['Lowest', 'Low', 'Normal', 'High', 'Highest'] as TaskPriority[]).map((value) => ({
    value,
    label: t(`tasks.priority${value}`),
  })),
)

const editing = ref<Task | 'new' | null>(null)
const form = ref({
  title: '',
  notes: '',
  dueDate: '',
  startDate: '',
  priority: 'Normal' as TaskPriority,
  progressPercent: '' as string,
  originalEstimateMinutes: '' as string,
  remainingEstimateMinutes: '' as string,
  blockCompleteWhenOpenChildren: true,
  blockCompleteWhenMandatoryIncomplete: true,
  parentTaskId: null as string | null,
  assignees: [] as FormAssignee[],
})
const saving = ref(false)
const children = ref<Task[]>([])
const checklists = ref<TaskChecklist[]>([])
const newChildTitle = ref('')
const newChecklistTitle = ref('')
const newItemTitle = ref<Record<string, string>>({})
const detailLoading = ref(false)

const roots = computed(() => tasks.value.filter((t) => !t.parentTaskId || !tasks.value.some((x) => x.id === t.parentTaskId)))
const childrenOf = (id: string) => tasks.value.filter((t) => t.parentTaskId === id)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const canCreate = computed(() => !!spaceKey.value && !!space.value?.rights.canCreate)
const dueLabel = (d: string | null) => (d ? formatDate(`${d}T00:00:00Z`, locale.locale, { dateStyle: 'medium', timeZone: 'UTC' }) : '')
const people = (task: Task) => (task.assignees?.length ? task.assignees : task.assigneeId ? [{ userId: task.assigneeId, name: task.assigneeName, role: 'Responsible', isPrimary: true }] : [])

async function load() {
  loading.value = true
  try {
    const status = filter.value === 'All' ? undefined : filter.value
    if (spaceKey.value) {
      const [list, sp] = await Promise.all([
        tasksApi.list(spaceKey.value, status, { archived: showArchived.value }),
        space.value?.key === spaceKey.value ? Promise.resolve({ value: space.value }) : wikiApi.space(spaceKey.value),
      ])
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
watch([filter, spaceKey, showArchived], load)

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
  if (task === 'new') {
    form.value = {
      title: '', notes: '', dueDate: '', startDate: '', priority: 'Normal', progressPercent: '',
      originalEstimateMinutes: '', remainingEstimateMinutes: '',
      blockCompleteWhenOpenChildren: true, blockCompleteWhenMandatoryIncomplete: true, parentTaskId: null, assignees: [],
    }
    children.value = []
    checklists.value = []
    return
  }
  form.value = {
    title: task.title,
    notes: task.notes ?? '',
    dueDate: task.dueDate ?? '',
    startDate: task.startDate ?? '',
    priority: task.priority ?? 'Normal',
    progressPercent: task.progressPercent == null ? '' : String(task.progressPercent),
    originalEstimateMinutes: task.originalEstimateMinutes == null ? '' : String(task.originalEstimateMinutes),
    remainingEstimateMinutes: task.remainingEstimateMinutes == null ? '' : String(task.remainingEstimateMinutes),
    blockCompleteWhenOpenChildren: task.blockCompleteWhenOpenChildren !== false,
    blockCompleteWhenMandatoryIncomplete: task.blockCompleteWhenMandatoryIncomplete !== false,
    parentTaskId: task.parentTaskId,
    assignees: people(task).map((a) => ({
      userId: a.userId,
      name: a.name ?? '',
      role: (a.role as TaskAssigneeRole) || 'Contributor',
      isPrimary: a.role === 'Responsible',
    })),
  }
  void loadDetail(task.id)
}

async function loadDetail(taskId: string) {
  detailLoading.value = true
  try {
    const [kids, lists] = await Promise.all([tasksApi.children(taskId), tasksApi.checklists(taskId)])
    children.value = kids
    checklists.value = lists
  } catch (e) {
    fail(e)
  } finally {
    detailLoading.value = false
  }
}

function pickAssignee(s: { subjectType: string; subjectId: string | null; name: string }) {
  if (s.subjectType !== 'User' || !s.subjectId) return
  if (form.value.assignees.some((a) => a.userId === s.subjectId)) return
  const needsResponsible = !form.value.assignees.some((a) => a.role === 'Responsible')
  form.value.assignees.push({
    userId: s.subjectId,
    name: s.name,
    role: needsResponsible ? 'Responsible' : 'Contributor',
    isPrimary: needsResponsible,
  })
}

function setAssigneeRole(userId: string, role: TaskAssigneeRole) {
  const list = form.value.assignees
  const target = list.find((a) => a.userId === userId)
  if (!target) return
  if (role === 'Responsible') {
    for (const a of list) a.isPrimary = a.userId === userId
  }
  target.role = role
  target.isPrimary = role === 'Responsible'
  // Only one Responsible: demote others.
  if (role === 'Responsible') {
    for (const a of list) {
      if (a.userId !== userId && a.role === 'Responsible') {
        a.role = 'Contributor'
        a.isPrimary = false
      }
    }
  }
}

function dropAssignee(userId: string) {
  form.value.assignees = form.value.assignees.filter((a) => a.userId !== userId)
}

async function save() {
  const target = editing.value
  if (!target || !form.value.title.trim()) return
  saving.value = true
  const responsibles = form.value.assignees.filter((a) => a.role === 'Responsible')
  if (responsibles.length > 1) {
    toast.add({ severity: 'warn', summary: t('tasks.oneResponsible'), life: 5000 })
    saving.value = false
    return
  }
  const primary = responsibles[0] ?? null
  const progressRaw = form.value.progressPercent.trim()
  const progressPercent = progressRaw === '' ? null : Math.min(100, Math.max(0, Number.parseInt(progressRaw, 10) || 0))
  const parseMinutes = (raw: string) => {
    const t = raw.trim()
    if (t === '') return null
    const n = Number.parseInt(t, 10)
    return Number.isFinite(n) && n >= 0 ? n : null
  }
  const body = {
    title: form.value.title.trim(),
    notes: form.value.notes.trim() || null,
    assigneeId: primary?.userId ?? null,
    dueDate: form.value.dueDate || null,
    startDate: form.value.startDate || null,
    contentId: target === 'new' ? null : target.contentId,
    priority: form.value.priority,
    progressPercent,
    originalEstimateMinutes: parseMinutes(form.value.originalEstimateMinutes),
    remainingEstimateMinutes: parseMinutes(form.value.remainingEstimateMinutes),
    parentTaskId: form.value.parentTaskId,
    blockCompleteWhenOpenChildren: form.value.blockCompleteWhenOpenChildren,
    blockCompleteWhenMandatoryIncomplete: form.value.blockCompleteWhenMandatoryIncomplete,
    assignees: form.value.assignees.map((a) => ({
      userId: a.userId,
      role: a.role,
      isPrimary: a.role === 'Responsible',
    })),
  }
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

async function archiveOrRestore(task: Task) {
  try {
    const updated = await (task.archivedAt ? tasksApi.restore(task.id) : tasksApi.archive(task.id))
    if (!showArchived.value && updated.archivedAt) {
      tasks.value = tasks.value.filter((x) => x.id !== updated.id)
    } else {
      tasks.value = tasks.value.map((x) => (x.id === updated.id ? updated : x))
    }
  } catch (e) {
    fail(e)
  }
}

function openAsChild(parent: Task) {
  openEditor('new')
  form.value.parentTaskId = parent.id
}

async function addChild() {
  const parent = editing.value
  if (parent === 'new' || !parent || !spaceKey.value || !newChildTitle.value.trim()) return
  try {
    await tasksApi.create(spaceKey.value, {
      title: newChildTitle.value.trim(), notes: null, assigneeId: null, dueDate: null, startDate: null,
      contentId: null, priority: 'Normal', parentTaskId: parent.id, assignees: [],
    })
    newChildTitle.value = ''
    await Promise.all([load(), loadDetail(parent.id)])
  } catch (e) {
    fail(e)
  }
}

async function promoteCurrent() {
  const parent = editing.value
  if (parent === 'new' || !parent) return
  try {
    const updated = await tasksApi.promote(parent.id)
    editing.value = updated
    form.value.parentTaskId = null
    await load()
  } catch (e) {
    fail(e)
  }
}

async function addChecklist() {
  const parent = editing.value
  if (parent === 'new' || !parent || !newChecklistTitle.value.trim()) return
  try {
    await tasksApi.createChecklist(parent.id, newChecklistTitle.value.trim())
    newChecklistTitle.value = ''
    checklists.value = await tasksApi.checklists(parent.id)
  } catch (e) {
    fail(e)
  }
}

async function addItem(checklistId: string) {
  const title = (newItemTitle.value[checklistId] ?? '').trim()
  if (!title || editing.value === 'new' || !editing.value) return
  try {
    await tasksApi.addChecklistItem(checklistId, { title, isRequired: false })
    newItemTitle.value[checklistId] = ''
    checklists.value = await tasksApi.checklists(editing.value.id)
  } catch (e) {
    fail(e)
  }
}

async function toggleItem(itemId: string, done: boolean) {
  if (editing.value === 'new' || !editing.value) return
  try {
    await tasksApi.updateChecklistItem(itemId, { isDone: done })
    checklists.value = await tasksApi.checklists(editing.value.id)
  } catch (e) {
    fail(e)
  }
}

async function toggleRequired(itemId: string, required: boolean) {
  if (editing.value === 'new' || !editing.value) return
  try {
    await tasksApi.updateChecklistItem(itemId, { isRequired: required })
    checklists.value = await tasksApi.checklists(editing.value.id)
  } catch (e) {
    fail(e)
  }
}

async function removeItem(itemId: string) {
  if (editing.value === 'new' || !editing.value) return
  try {
    await tasksApi.deleteChecklistItem(itemId)
    checklists.value = await tasksApi.checklists(editing.value.id)
  } catch (e) {
    fail(e)
  }
}
</script>

<template>
  <section class="tasks" data-testid="tasks">
    <header class="head">
      <h1>{{ spaceKey ? t('tasks.spaceTitle', { name: space?.name ?? spaceKey }) : t('tasks.myTitle') }}</h1>
      <div class="tools">
        <SelectButton v-model="filter" :options="filters" option-label="label" option-value="value" :allow-empty="false" :aria-label="t('tasks.filter')" />
        <label v-if="spaceKey" class="archived">
          <Checkbox v-model="showArchived" binary input-id="tasks-archived" data-testid="show-archived" />
          <span>{{ t('tasks.showArchived') }}</span>
        </label>
        <Button v-if="canCreate" icon="pi pi-plus" :label="t('tasks.new')" data-testid="new-task" @click="openEditor('new')" />
        <Button v-if="spaceKey" as="a" :href="tasksApi.exportUrl(spaceKey)" icon="pi pi-download" text :label="t('tasks.export')" data-testid="export-tasks" />
      </div>
    </header>

    <p v-if="!loading && tasks.length === 0" class="muted" data-testid="no-tasks">{{ spaceKey ? t('tasks.noneSpace') : t('tasks.noneMine') }}</p>
    <ul class="list">
      <template v-for="task in (spaceKey ? roots : tasks)" :key="task.id">
        <li :class="['task', { done: task.status === 'Done', archived: !!task.archivedAt }]" data-testid="task">
          <Checkbox :model-value="task.status === 'Done'" binary :disabled="!task.rights.canComplete || !!task.archivedAt" :input-id="`t-${task.id}`" :aria-label="t(task.status === 'Done' ? 'tasks.reopen' : 'tasks.complete', { title: task.title })" @update:model-value="toggle(task)" />
          <div class="body">
            <label :for="`t-${task.id}`" class="title" dir="auto">{{ task.title }}</label>
            <p v-if="task.notes" class="notes" dir="auto">{{ task.notes }}</p>
            <p class="meta">
              <Tag :value="t(`tasks.priority${task.priority || 'Normal'}`)" :severity="task.priority === 'Highest' || task.priority === 'High' ? 'warn' : 'secondary'" />
              <Tag v-if="task.isOverdue" severity="danger" :value="t('tasks.overdue')" />
              <Tag v-if="task.archivedAt" severity="contrast" :value="t('tasks.archived')" />
              <span v-if="task.childCount">{{ t('tasks.childrenCount', { n: task.childCount, open: task.openChildCount }) }}</span>
              <span v-if="task.checklistProgressPercent != null">{{ t('tasks.checklistProgress', { n: task.checklistProgressPercent }) }}</span>
              <span v-if="task.dueDate">{{ t('tasks.due', { date: dueLabel(task.dueDate) }) }}</span>
              <span v-if="task.progressPercent != null">{{ t('tasks.progress', { n: task.progressPercent }) }}</span>
              <span v-if="task.originalEstimateMinutes != null">{{ t('tasks.estimateShort', { value: formatDuration(task.originalEstimateMinutes, t) }) }}</span>
              <span v-if="task.actualSpentMinutes != null">{{ t('tasks.spent', { n: task.actualSpentMinutes }) }}</span>
              <span v-for="a in people(task)" :key="`${a.userId}-${a.role}`" class="who">· <UserAvatar :user-id="a.userId" :name="a.name" :size="20" /> {{ a.name }} <small>({{ te(`tasks.role${a.role}`) ? t(`tasks.role${a.role}`) : a.role }})</small></span>
              <RouterLink v-if="!spaceKey" :to="{ name: 'space-tasks', params: { key: task.spaceKey } }">· {{ task.spaceName }}</RouterLink>
            </p>
          </div>
          <div v-if="task.rights.canEdit" class="actions">
            <Button text rounded size="small" icon="pi pi-sitemap" :aria-label="t('tasks.addChild')" data-testid="add-child" @click="openAsChild(task)" />
            <Button text rounded size="small" icon="pi pi-pencil" :aria-label="t('common.edit')" @click="openEditor(task)" />
            <Button text rounded size="small" :icon="task.archivedAt ? 'pi pi-replay' : 'pi pi-inbox'" :aria-label="t(task.archivedAt ? 'tasks.restore' : 'tasks.archive')" data-testid="archive-task" @click="archiveOrRestore(task)" />
            <Button text rounded size="small" severity="danger" icon="pi pi-trash" :aria-label="t('common.delete')" @click="remove(task)" />
          </div>
        </li>
        <li v-for="child in (spaceKey ? childrenOf(task.id) : [])" :key="child.id" :class="['task', 'child', { done: child.status === 'Done' }]" data-testid="task">
          <Checkbox :model-value="child.status === 'Done'" binary :disabled="!child.rights.canComplete" :input-id="`t-${child.id}`" @update:model-value="toggle(child)" />
          <div class="body">
            <label :for="`t-${child.id}`" class="title" dir="auto">{{ child.title }}</label>
            <p class="meta"><Tag severity="secondary" :value="t('tasks.subtask')" /></p>
          </div>
          <div v-if="child.rights.canEdit" class="actions">
            <Button text rounded size="small" icon="pi pi-pencil" @click="openEditor(child)" />
          </div>
        </li>
      </template>
    </ul>

    <Dialog :visible="!!editing" modal :header="editing === 'new' ? t('tasks.new') : t('tasks.edit')" :style="{ inlineSize: 'min(32rem, 95vw)' }" @update:visible="(v) => !v && (editing = null)">
      <form class="form" @submit.prevent="save">
        <label for="task-title">{{ t('tasks.title') }}</label>
        <InputText id="task-title" v-model="form.title" maxlength="300" required data-testid="task-title" />
        <label for="task-notes">{{ t('tasks.notes') }}</label>
        <Textarea id="task-notes" v-model="form.notes" rows="3" maxlength="4000" auto-resize />
        <label for="task-priority">{{ t('tasks.priority') }}</label>
        <Select id="task-priority" v-model="form.priority" :options="priorities" option-label="label" option-value="value" data-testid="task-priority" />
        <label for="task-start">{{ t('tasks.startDate') }}</label>
        <CalendarDateInput id="task-start" v-model="form.startDate" />
        <label for="task-due">{{ t('tasks.dueDate') }}</label>
        <CalendarDateInput id="task-due" v-model="form.dueDate" />
        <label for="task-original-estimate">{{ t('tasks.originalEstimate') }}</label>
        <InputText id="task-original-estimate" v-model="form.originalEstimateMinutes" inputmode="numeric" data-testid="task-original-estimate" :placeholder="t('tasks.minutesHint')" />
        <label for="task-remaining-estimate">{{ t('tasks.remainingEstimate') }}</label>
        <InputText id="task-remaining-estimate" v-model="form.remainingEstimateMinutes" inputmode="numeric" data-testid="task-remaining-estimate" :placeholder="t('tasks.minutesHint')" />
        <label for="task-progress">{{ t('tasks.progressLabel') }}</label>
        <InputText id="task-progress" v-model="form.progressPercent" inputmode="numeric" maxlength="3" data-testid="task-progress" />
        <p class="muted">{{ t('tasks.progressManualHint') }}</p>
        <label class="archived">
          <Checkbox v-model="form.blockCompleteWhenOpenChildren" binary input-id="task-block-children" data-testid="task-block-children" />
          <span>{{ t('tasks.blockCompleteChildren') }}</span>
        </label>
        <label class="archived">
          <Checkbox v-model="form.blockCompleteWhenMandatoryIncomplete" binary input-id="task-block-mandatory" data-testid="task-block-mandatory" />
          <span>{{ t('tasks.blockCompleteMandatory') }}</span>
        </label>
        <label for="task-assignee">{{ t('tasks.assignees') }}</label>
        <ul v-if="form.assignees.length" class="assignees" data-testid="task-assignees">
          <li v-for="a in form.assignees" :key="a.userId" data-testid="task-assignee-row">
            <span class="who">{{ a.name }}</span>
            <Select
              :model-value="a.role"
              :options="roleOptions"
              option-label="label"
              option-value="value"
              :input-id="`role-${a.userId}`"
              data-testid="task-assignee-role"
              @update:model-value="(v: TaskAssigneeRole) => setAssigneeRole(a.userId, v)"
            />
            <Button text size="small" type="button" :label="t('tasks.unassign')" data-testid="task-unassign" @click="dropAssignee(a.userId)" />
          </li>
        </ul>
        <PrincipalPicker input-id="task-assignee" users-only data-testid="task-assignee-picker" @pick="pickAssignee" />

        <template v-if="editing && editing !== 'new'">
          <h3>{{ t('tasks.subtasks') }}</h3>
          <p v-if="form.parentTaskId" class="muted">
            {{ t('tasks.isSubtask') }}
            <Button text size="small" type="button" :label="t('tasks.promote')" data-testid="promote-task" @click="promoteCurrent" />
          </p>
          <ul class="assignees" data-testid="task-children">
            <li v-for="c in children" :key="c.id">{{ c.title }} <Tag v-if="c.status === 'Done'" :value="t('tasks.filterDone')" /></li>
          </ul>
          <div class="row">
            <InputText v-model="newChildTitle" :placeholder="t('tasks.childTitle')" data-testid="new-child-title" />
            <Button type="button" :label="t('tasks.addChild')" :disabled="!newChildTitle.trim()" data-testid="save-child" @click="addChild" />
          </div>

          <h3>{{ t('tasks.checklists') }}</h3>
          <p v-if="detailLoading" class="muted">{{ t('common.loading') }}</p>
          <div v-for="list in checklists" :key="list.id" class="checklist" data-testid="task-checklist">
            <strong dir="auto">{{ list.title }}</strong>
            <ul>
              <li v-for="item in list.items" :key="item.id" class="item">
                <Checkbox :model-value="item.isDone" binary @update:model-value="(v: boolean) => toggleItem(item.id, v)" />
                <span dir="auto">{{ item.title }}</span>
                <Tag v-if="item.isRequired" severity="warn" :value="t('tasks.required')" />
                <Button text size="small" type="button" :label="item.isRequired ? t('tasks.optional') : t('tasks.makeRequired')" @click="toggleRequired(item.id, !item.isRequired)" />
                <Button text size="small" type="button" severity="danger" :label="t('common.delete')" @click="removeItem(item.id)" />
              </li>
            </ul>
            <div class="row">
              <InputText v-model="newItemTitle[list.id]" :placeholder="t('tasks.itemTitle')" />
              <Button type="button" :label="t('tasks.addItem')" @click="addItem(list.id)" />
            </div>
          </div>
          <div class="row">
            <InputText v-model="newChecklistTitle" :placeholder="t('tasks.checklistTitle')" data-testid="new-checklist-title" />
            <Button type="button" :label="t('tasks.addChecklist')" data-testid="save-checklist" @click="addChecklist" />
          </div>

          <TaskTimePanel :task-id="editing.id" />
          <TaskCollaborationPanel :task-id="editing.id" />
        </template>

        <Button type="submit" :label="t('common.save')" :loading="saving" :disabled="!form.title.trim() || saving" data-testid="save-task" />
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.tasks { display: grid; gap: var(--raavi-space-3); max-inline-size: 56rem; }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: center; }
h1 { margin: 0; font-size: 1.5rem; }
.tools { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.archived { display: inline-flex; gap: var(--raavi-space-2); align-items: center; color: var(--raavi-text-muted); font-size: 0.9rem; }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.task { display: flex; gap: var(--raavi-space-3); align-items: flex-start; padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.task.done .title { text-decoration: line-through; color: var(--raavi-text-muted); }
.task.archived { opacity: 0.72; }
.task.child { margin-inline-start: var(--raavi-space-5); border-style: dashed; }
.body { flex: 1; min-inline-size: 0; }
.row { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.checklist { display: grid; gap: var(--raavi-space-2); padding-block: var(--raavi-space-2); border-block-start: 1px solid var(--raavi-border); }
.checklist .item { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
h3 { margin: var(--raavi-space-3) 0 0; font-size: 1rem; }
.title { font-weight: 600; overflow-wrap: anywhere; }
.notes { margin: var(--raavi-space-1) 0 0; color: var(--raavi-text-muted); white-space: pre-wrap; overflow-wrap: anywhere; }
.meta { margin: var(--raavi-space-1) 0 0; display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; font-size: 0.85rem; color: var(--raavi-text-muted); }
.actions { display: flex; }
.form { display: grid; gap: var(--raavi-space-2); }
.assignees { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-1); }
.assignees li { display: flex; justify-content: space-between; gap: var(--raavi-space-2); align-items: center; }
.muted { color: var(--raavi-text-muted); margin: 0; }
.who { display: inline-flex; gap: 0.25rem; align-items: center; }
</style>
