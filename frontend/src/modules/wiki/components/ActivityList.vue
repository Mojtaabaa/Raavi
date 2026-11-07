<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import type { Activity } from '@/core/api/wiki'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'

defineProps<{ items: Activity[]; showSpace?: boolean }>()
const { t, te } = useI18n()
const locale = useLocaleStore()

const kindLabel = (kind: string) => (te(`wiki.activity.kinds.${kind}`) ? t(`wiki.activity.kinds.${kind}`) : kind)
</script>

<template>
  <p v-if="items.length === 0" class="muted">{{ t('wiki.activity.empty') }}</p>
  <ul v-else class="activity">
    <li v-for="a in items" :key="a.id">
      <span class="who">{{ a.actorName ?? t('wiki.unknownUser') }}</span>
      <span class="what">{{ kindLabel(a.kind) }}</span>
      <RouterLink v-if="a.contentId" :to="{ name: 'page', params: { key: a.spaceKey, id: a.contentId } }">{{ a.title }}</RouterLink>
      <RouterLink v-else :to="{ name: 'space', params: { key: a.spaceKey } }">{{ a.title }}</RouterLink>
      <small v-if="a.version" class="muted">{{ t('wiki.page.versionN', { n: a.version }) }}</small>
      <small class="muted when">{{ formatDate(a.occurredAt, locale.locale, { dateStyle: 'medium', timeStyle: 'short' }) }}</small>
    </li>
  </ul>
</template>

<style scoped>
.activity { list-style: none; padding: 0; margin: 0; display: grid; gap: var(--raavi-space-2); }
.activity li { display: flex; flex-wrap: wrap; align-items: baseline; gap: var(--raavi-space-1) var(--raavi-space-2); }
.who { font-weight: 600; }
.muted { color: var(--raavi-text-muted); }
.when { margin-inline-start: auto; }
</style>
