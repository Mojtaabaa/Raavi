<script setup lang="ts">
import { onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import {
  tasksApi,
  type TaskActivity,
  type TaskAttachment,
  type TaskComment,
  type TaskWatcher,
} from '@/core/api/tasks'
import type { RaaviDoc } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import CommentComposer from '@/modules/wiki/comments/CommentComposer.vue'
import PrincipalPicker from '@/modules/wiki/components/PrincipalPicker.vue'

const props = defineProps<{ taskId: string }>()
const { t, te, d } = useI18n()
const toast = useToast()

const comments = ref<TaskComment[]>([])
const total = ref(0)
const canComment = ref(false)
const watching = ref(false)
const attachments = ref<TaskAttachment[]>([])
const watchers = ref<TaskWatcher[]>([])
const canManageWatchers = ref(false)
const activity = ref<TaskActivity[]>([])
const loading = ref(true)
const busy = ref(false)
const sortAsc = ref(false)
const composer = ref<InstanceType<typeof CommentComposer> | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })

async function load() {
  loading.value = true
  try {
    const [c, a, w, act] = await Promise.all([
      tasksApi.comments(props.taskId, { take: 30, sort: sortAsc.value ? 'asc' : 'desc' }),
      tasksApi.attachments(props.taskId),
      tasksApi.watchers(props.taskId),
      tasksApi.activity(props.taskId, 30),
    ])
    comments.value = c.items
    total.value = c.total
    canComment.value = c.canComment
    watching.value = c.watching
    attachments.value = a
    watchers.value = w.items
    canManageWatchers.value = w.canManage
    watching.value = w.watching
    activity.value = act
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}

onMounted(load)
watch(() => props.taskId, load)
watch(sortAsc, load)

async function postComment(doc: RaaviDoc) {
  busy.value = true
  try {
    await tasksApi.createComment(props.taskId, doc)
    composer.value?.clear()
    await load()
  } catch (e) {
    fail(e)
  } finally {
    busy.value = false
  }
}

async function removeComment(id: string) {
  try {
    await tasksApi.deleteComment(id)
    await load()
  } catch (e) {
    fail(e)
  }
}

async function toggleWatch() {
  try {
    const r = await tasksApi.setWatch(props.taskId, !watching.value)
    watching.value = r.watching
    watchers.value = r.items
  } catch (e) {
    fail(e)
  }
}

function formatSize(n: number): string {
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / (1024 * 1024)).toFixed(1)} MB`
}

async function onPickWatcher(p: { subjectType: string; subjectId: string | null; name: string }) {
  if (!p.subjectId) return
  try {
    const r = await tasksApi.addWatcher(props.taskId, p.subjectId)
    watchers.value = r.items
    watching.value = r.watching
  } catch (e) {
    fail(e)
  }
}

async function onFile(ev: Event) {
  const input = ev.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file) return
  try {
    await tasksApi.uploadAttachment(props.taskId, file)
    attachments.value = await tasksApi.attachments(props.taskId)
    activity.value = await tasksApi.activity(props.taskId, 30)
  } catch (e) {
    fail(e)
  }
}

async function removeAttachment(id: string) {
  try {
    await tasksApi.deleteAttachment(id)
    attachments.value = await tasksApi.attachments(props.taskId)
  } catch (e) {
    fail(e)
  }
}

function plain(c: TaskComment): string {
  if (c.isDeleted) return t('tasks.commentDeleted')
  const walk = (n: { type?: string; text?: string; attrs?: { label?: string }; content?: unknown[] }): string => {
    if (n.type === 'text') return n.text ?? ''
    if (n.type === 'mention') return `@${n.attrs?.label ?? ''}`
    return ((n.content as typeof n[] | undefined) ?? []).map(walk).join('')
  }
  return walk(c.body.doc as never).trim() || '…'
}
</script>

<template>
  <section class="collab" data-testid="task-collaboration">
    <p v-if="loading" class="muted">{{ t('common.loading') }}</p>
    <template v-else>
      <div class="row head">
        <h3>{{ t('tasks.collaboration') }}</h3>
        <Button
          type="button"
          size="small"
          text
          :label="watching ? t('tasks.unwatch') : t('tasks.watch')"
          data-testid="task-watch-toggle"
          @click="toggleWatch"
        />
      </div>

      <h4>{{ t('tasks.comments') }}</h4>
      <div class="row">
        <Button type="button" size="small" text :label="sortAsc ? t('tasks.sortOldest') : t('tasks.sortNewest')" @click="sortAsc = !sortAsc" />
        <span class="muted">{{ t('tasks.commentCount', { n: total }) }}</span>
      </div>
      <p v-if="!comments.length" class="muted" data-testid="task-comments-empty">{{ t('tasks.noComments') }}</p>
      <ul class="list" data-testid="task-comments">
        <li v-for="c in comments" :key="c.id" class="item">
          <div class="meta">
            <strong>{{ c.authorName ?? c.authorId }}</strong>
            <time>{{ d(c.createdAt, 'short') }}</time>
            <span v-if="c.editedAt" class="muted">{{ t('tasks.edited') }}</span>
          </div>
          <p class="body" dir="auto">{{ plain(c) }}</p>
          <Button v-if="c.canDelete" type="button" size="small" text severity="danger" :label="t('common.delete')" @click="removeComment(c.id)" />
        </li>
      </ul>
      <CommentComposer v-if="canComment" ref="composer" :busy="busy" @submit="postComment" />

      <h4>{{ t('tasks.attachments') }}</h4>
      <p v-if="!attachments.length" class="muted">{{ t('tasks.noAttachments') }}</p>
      <ul class="list" data-testid="task-attachments">
        <li v-for="a in attachments" :key="a.id" class="item row">
          <a :href="tasksApi.attachmentUrl(a.id)" target="_blank" rel="noopener">{{ a.fileName }}</a>
          <span class="muted">{{ formatSize(a.size) }} · {{ a.mediaType }}</span>
          <Button type="button" size="small" text severity="danger" :label="t('common.delete')" @click="removeAttachment(a.id)" />
        </li>
      </ul>
      <div v-if="canComment" class="row">
        <input ref="fileInput" type="file" class="sr" data-testid="task-attach-input" @change="onFile" />
        <Button type="button" size="small" :label="t('tasks.upload')" @click="fileInput?.click()" />
      </div>

      <h4>{{ t('tasks.watchers') }}</h4>
      <ul class="list" data-testid="task-watchers">
        <li v-for="w in watchers" :key="w.userId">{{ w.name ?? w.userId }}</li>
      </ul>
      <PrincipalPicker v-if="canManageWatchers" input-id="task-watcher" users-only data-testid="task-watcher-picker" @pick="onPickWatcher" />

      <h4>{{ t('tasks.activity') }}</h4>
      <p v-if="!activity.length" class="muted">{{ t('tasks.noActivity') }}</p>
      <ul class="list" data-testid="task-activity">
        <li v-for="e in activity" :key="e.id" class="item">
          <span>{{ e.actorName ?? e.actorId }} · {{ e.kind }}</span>
          <time class="muted">{{ d(e.occurredAt, 'short') }}</time>
        </li>
      </ul>
    </template>
  </section>
</template>

<style scoped>
.collab { display: grid; gap: var(--raavi-space-2); margin-block-start: var(--raavi-space-3); border-block-start: 1px solid var(--raavi-border); padding-block-start: var(--raavi-space-3); }
.head { justify-content: space-between; }
.row { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.item { display: grid; gap: 0.25rem; }
.meta { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; font-size: 0.85rem; color: var(--raavi-text-muted); }
.body { margin: 0; overflow-wrap: anywhere; white-space: pre-wrap; }
.muted { color: var(--raavi-text-muted); margin: 0; }
h3, h4 { margin: var(--raavi-space-2) 0 0; font-size: 1rem; }
.sr { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip: rect(0 0 0 0); }
</style>
