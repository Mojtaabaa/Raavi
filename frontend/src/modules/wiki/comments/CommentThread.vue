<script setup lang="ts">
import UserAvatar from '@/app/UserAvatar.vue'
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import Popover from 'primevue/popover'
import { useConfirm } from 'primevue/useconfirm'
import { commentsApi, type Comment, type Thread } from '@/core/api/comments'
import type { RaaviDoc } from '@/core/api/wiki'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import RaaviEditor from '@/modules/editor/components/RaaviEditor.vue'
import CommentComposer from './CommentComposer.vue'

/** One thread: root comment, flat replies, reactions and thread actions. */
const props = defineProps<{ thread: Thread; reactions: string[]; active: boolean; orphaned: boolean }>()
const emit = defineEmits<{ changed: [thread: Thread]; focusAnchor: [id: string]; error: [e: unknown] }>()
const { t } = useI18n()
const locale = useLocaleStore()
const confirm = useConfirm()

const replying = ref(false)
const editing = ref<string | null>(null)
const busy = ref(false)
const picker = ref<InstanceType<typeof Popover> | null>(null)
const pickerFor = ref<Comment | null>(null)
const replyComposer = ref<InstanceType<typeof CommentComposer> | null>(null)

const when = (iso: string) => formatDate(iso, locale.locale, { dateStyle: 'medium', timeStyle: 'short' })

function replace(comment: Comment) {
  emit('changed', { ...props.thread, comments: props.thread.comments.map((c) => (c.id === comment.id ? comment : c)) })
}

async function guard(fn: () => Promise<void>) {
  busy.value = true
  try {
    await fn()
  } catch (e) {
    emit('error', e)
  } finally {
    busy.value = false
  }
}

const reply = (doc: RaaviDoc) => guard(async () => {
  const c = await commentsApi.reply(props.thread.id, doc)
  emit('changed', { ...props.thread, comments: [...props.thread.comments, c] })
  replyComposer.value?.clear()
  replying.value = false
})

const saveEdit = (comment: Comment, doc: RaaviDoc) => guard(async () => {
  replace(await commentsApi.edit(comment, doc))
  editing.value = null
})

function remove(comment: Comment) {
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('comments.confirmDelete'),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: () => guard(async () => {
      await commentsApi.remove(comment.id)
      replace({ ...comment, isDeleted: true, body: null, reactions: [], rights: { canEdit: false, canDelete: false } })
    }),
  })
}

const resolve = (resolved: boolean) => guard(async () => emit('changed', await commentsApi.resolve(props.thread.id, resolved)))

const react = (comment: Comment, emoji: string) => guard(async () => {
  const mine = comment.reactions.some((r) => r.emoji === emoji && r.mine)
  replace({ ...comment, reactions: await commentsApi.react(comment.id, emoji, !mine) })
  picker.value?.hide()
})

function openPicker(e: Event, comment: Comment) {
  pickerFor.value = comment
  picker.value?.toggle(e)
}
</script>

<template>
  <article :class="['thread', { active, resolved: thread.status === 'Resolved' }]" :aria-label="t('comments.thread')" :data-thread="thread.id">
    <header v-if="thread.kind === 'Inline' && thread.anchor" class="quote">
      <button type="button" class="quote-text" dir="auto" :disabled="orphaned" :title="orphaned ? t('comments.orphaned') : t('comments.showInPage')" @click="emit('focusAnchor', thread.id)">
        {{ thread.anchor.exact }}
      </button>
      <Tag v-if="orphaned" severity="secondary" :value="t('comments.orphanedShort')" />
    </header>
    <Tag v-if="thread.status === 'Resolved'" severity="success" icon="pi pi-check" :value="t('comments.resolvedBy', { name: thread.resolvedByName ?? t('wiki.unknownUser') })" />

    <ol class="comments">
      <li v-for="c in thread.comments" :id="`comment-${c.id}`" :key="c.id" class="comment">
        <div class="meta">
          <UserAvatar :user-id="c.authorId" :name="c.authorName" :size="24" />
          <strong>{{ c.authorName ?? t('wiki.unknownUser') }}</strong>
          <time :datetime="c.createdAt">{{ when(c.createdAt) }}</time>
          <span v-if="c.editedAt" class="muted">({{ t('comments.edited') }})</span>
        </div>
        <p v-if="c.isDeleted" class="muted deleted">{{ t('comments.deleted') }}</p>
        <CommentComposer
          v-else-if="editing === c.id"
          :initial="c.body"
          :busy="busy"
          :submit-label="t('common.save')"
          cancellable
          :label="t('comments.editLabel')"
          @submit="(d) => saveEdit(c, d)"
          @cancel="editing = null"
        />
        <RaaviEditor v-else :model-value="c.body" class="body" />

        <div v-if="!c.isDeleted" class="actions">
          <Button
            v-for="r in c.reactions"
            :key="r.emoji"
            size="small"
            :outlined="!r.mine"
            :severity="r.mine ? 'primary' : 'secondary'"
            :aria-pressed="r.mine"
            :aria-label="t('comments.reactionCount', { emoji: r.emoji, count: r.count })"
            :disabled="!thread.canReply || busy"
            class="reaction"
            @click="react(c, r.emoji)"
          >
            {{ r.emoji }} <span>{{ r.count }}</span>
          </Button>
          <Button v-if="thread.canReply" text rounded size="small" icon="pi pi-face-smile" :aria-label="t('comments.addReaction')" aria-haspopup="true" @click="(e) => openPicker(e, c)" />
          <Button v-if="c.rights.canEdit" text size="small" :label="t('comments.edit')" @click="editing = c.id" />
          <Button v-if="c.rights.canDelete" text size="small" severity="danger" :label="t('comments.delete')" @click="remove(c)" />
        </div>
      </li>
    </ol>

    <footer class="thread-actions">
      <Button v-if="thread.canReply && !replying" text size="small" icon="pi pi-reply" :label="t('comments.reply')" data-testid="reply" @click="replying = true" />
      <template v-if="thread.canResolve">
        <Button v-if="thread.status === 'Open'" text size="small" icon="pi pi-check" :label="t('comments.resolve')" data-testid="resolve" @click="resolve(true)" />
        <Button v-else text size="small" icon="pi pi-replay" :label="t('comments.reopen')" @click="resolve(false)" />
      </template>
    </footer>
    <CommentComposer v-if="replying" ref="replyComposer" :busy="busy" :submit-label="t('comments.reply')" cancellable :label="t('comments.replyLabel')" @submit="reply" @cancel="replying = false" />

    <Popover ref="picker">
      <div class="picker" role="group" :aria-label="t('comments.addReaction')">
        <Button v-for="e in reactions" :key="e" text size="small" :aria-label="e" @click="pickerFor && react(pickerFor, e)">{{ e }}</Button>
      </div>
    </Popover>
  </article>
</template>

<style scoped>
.thread { display: grid; gap: var(--raavi-space-2); padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.thread.active { border-color: #d9a800; box-shadow: 0 0 0 2px color-mix(in srgb, #f5c400 40%, transparent); }
.thread.resolved { opacity: 0.85; }
.quote { display: flex; align-items: center; gap: var(--raavi-space-2); }
.quote-text { all: unset; cursor: pointer; border-inline-start: 3px solid #d9a800; padding-inline-start: var(--raavi-space-2); color: var(--raavi-text-muted); font-style: italic; overflow-wrap: anywhere; }
.quote-text:focus-visible { outline: 2px solid var(--p-primary-color); }
.quote-text:disabled { cursor: default; text-decoration: line-through; }
.comments { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-3); }
.comment + .comment { border-block-start: 1px dashed var(--raavi-border); padding-block-start: var(--raavi-space-2); }
.meta { display: flex; gap: var(--raavi-space-2); align-items: baseline; flex-wrap: wrap; font-size: 0.9rem; }
.muted { color: var(--raavi-text-muted); }
.deleted { font-style: italic; margin: 0; }
.actions, .thread-actions { display: flex; flex-wrap: wrap; gap: var(--raavi-space-1); align-items: center; }
.reaction span { margin-inline-start: 0.25em; }
.picker { display: flex; gap: var(--raavi-space-1); }
</style>
