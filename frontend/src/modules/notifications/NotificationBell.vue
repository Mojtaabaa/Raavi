<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import Button from 'primevue/button'
import Popover from 'primevue/popover'
import Badge from 'primevue/badge'
import type { AppNotification } from '@/core/api/comments'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import { useNotificationsStore } from '@/stores/notifications'

/** Header bell: unread badge, inbox popover, mark as read, deep link to the page/comment. */
const { t } = useI18n()
const router = useRouter()
const locale = useLocaleStore()
const store = useNotificationsStore()
const panel = ref<InstanceType<typeof Popover> | null>(null)
const busy = ref(false)

onMounted(() => void store.start())
onBeforeUnmount(() => void store.stop())

async function toggle(e: Event) {
  panel.value?.toggle(e)
  busy.value = true
  await store.refreshList().catch(() => undefined)
  busy.value = false
}

const icon: Record<string, string> = {
  mention: 'pi pi-at',
  'comment.reply': 'pi pi-reply',
  'comment.created': 'pi pi-comment',
  'comment.resolved': 'pi pi-check-circle',
  'page.published': 'pi pi-file',
  workflow: 'pi pi-bolt',
}

function text(n: AppNotification) {
  // i18n keys cannot contain dots: comment.reply → comment_reply
  return t(`notifications.kind.${n.kind.replace('.', '_')}`, { name: n.actorName ?? t('wiki.unknownUser'), title: n.title })
}

async function open(n: AppNotification) {
  panel.value?.hide()
  await store.markRead(n).catch(() => undefined)
  if (n.spaceKey) await router.push({ name: 'page', params: { key: n.spaceKey, id: n.contentId }, hash: n.commentId ? `#comment-${n.commentId}` : '' })
}
</script>

<template>
  <span class="bell">
    <Button
      text
      rounded
      size="small"
      icon="pi pi-bell"
      :aria-label="store.unread ? t('notifications.openUnread', { n: store.unread }) : t('notifications.open')"
      aria-haspopup="dialog"
      data-testid="notifications-bell"
      @click="toggle"
    />
    <Badge v-if="store.unread" :value="store.unread > 99 ? '99+' : store.unread" severity="danger" class="count" data-testid="notifications-count" aria-hidden="true" />
    <Popover ref="panel">
      <div class="inbox" role="dialog" :aria-label="t('notifications.title')">
        <header>
          <strong>{{ t('notifications.title') }}</strong>
          <Button v-if="store.unread" text size="small" :label="t('notifications.markAllRead')" data-testid="mark-all-read" @click="store.markAllRead()" />
        </header>
        <p v-if="!busy && store.items.length === 0" class="muted">{{ t('notifications.empty') }}</p>
        <ul>
          <li v-for="n in store.items" :key="n.id">
            <button type="button" :class="['item', { unread: !n.readAt }]" data-testid="notification" @click="open(n)">
              <i :class="icon[n.kind] ?? 'pi pi-info-circle'" aria-hidden="true" />
              <span class="text">
                <span dir="auto">{{ text(n) }}</span>
                <span v-if="n.excerpt" class="excerpt" dir="auto">{{ n.excerpt }}</span>
                <time :datetime="n.createdAt" class="muted">{{ formatDate(n.createdAt, locale.locale, { dateStyle: 'medium', timeStyle: 'short' }) }}</time>
              </span>
              <span v-if="!n.readAt" class="sr-only">{{ t('notifications.unread') }}</span>
            </button>
          </li>
        </ul>
      </div>
    </Popover>
  </span>
</template>

<style scoped>
.bell { position: relative; display: inline-flex; }
.count { position: absolute; inset-block-start: -0.2rem; inset-inline-end: -0.2rem; pointer-events: none; }
.inbox { inline-size: min(24rem, 90vw); display: grid; gap: var(--raavi-space-2); }
.inbox header { display: flex; justify-content: space-between; align-items: center; }
ul { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-1); max-block-size: 60vh; overflow: auto; }
.item { all: unset; box-sizing: border-box; inline-size: 100%; display: flex; gap: var(--raavi-space-2); padding: var(--raavi-space-2); border-radius: var(--raavi-radius-sm); cursor: pointer; }
.item:hover, .item:focus-visible { background: var(--p-content-hover-background, #f4f4f5); }
.item:focus-visible { outline: 2px solid var(--p-primary-color); }
.item.unread { background: color-mix(in srgb, var(--p-primary-color, #6366f1) 8%, transparent); font-weight: 600; }
.text { display: grid; gap: 0.15rem; min-inline-size: 0; }
.excerpt { font-weight: 400; color: var(--raavi-text-muted); overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.muted { color: var(--raavi-text-muted); font-weight: 400; font-size: 0.85rem; }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
</style>
