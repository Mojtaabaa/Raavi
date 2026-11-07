<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Card from 'primevue/card'
import Message from 'primevue/message'
import { contentRoute, wikiApi, type Activity, type ContentListItem } from '@/core/api/wiki'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'
import ActivityList from '../components/ActivityList.vue'
import { useSpaceContext } from '../spaceContext'

const { t } = useI18n()
const locale = useLocaleStore()
const ctx = useSpaceContext()

const recent = ref<ContentListItem[]>([])
const activity = ref<Activity[]>([])

onMounted(async () => {
  ctx.activePath.value = []
  const space = ctx.space.value
  if (!space?.rights.canView) return
  ;[recent.value, activity.value] = await Promise.all([wikiApi.recent(space.key).catch(() => []), wikiApi.spaceActivity(space.key).catch(() => [])])
})
</script>

<template>
  <section v-if="ctx.space.value" class="overview">
    <header>
      <h1>{{ ctx.space.value.name }}</h1>
      <p class="muted">{{ t(`wiki.spaceTypes.${ctx.space.value.type}`) }} · {{ ctx.space.value.key }}</p>
      <p v-if="ctx.space.value.description" class="description">{{ ctx.space.value.description }}</p>
    </header>

    <Message v-if="!ctx.space.value.rights.canView" severity="warn" :closable="false">{{ t('wiki.space.adminOnly') }}</Message>
    <Message v-else-if="ctx.space.value.status === 'Archived'" severity="info" :closable="false">{{ t('wiki.space.archivedNotice') }}</Message>

    <template v-if="ctx.space.value.rights.canView">
      <RouterLink v-if="ctx.space.value.homePageId" :to="{ name: 'page', params: { key: ctx.space.value.key, id: ctx.space.value.homePageId } }" class="home-link">
        <i class="pi pi-home" aria-hidden="true" /> {{ t('wiki.space.openHome') }}
      </RouterLink>
      <div class="columns">
        <Card>
          <template #title>{{ t('wiki.space.recent') }}</template>
          <template #content>
            <p v-if="recent.length === 0" class="muted">{{ t('wiki.tree.empty') }}</p>
            <ul class="list">
              <li v-for="c in recent" :key="c.id">
                <RouterLink :to="contentRoute(c)">{{ c.title }}</RouterLink>
                <small class="muted">{{ formatDate(c.updatedAt, locale.locale) }}</small>
              </li>
            </ul>
          </template>
        </Card>
        <Card>
          <template #title>{{ t('wiki.home.recentActivity') }}</template>
          <template #content><ActivityList :items="activity" /></template>
        </Card>
      </div>
    </template>
  </section>
</template>

<style scoped>
.overview { display: grid; gap: var(--raavi-space-4); }
h1 { margin: 0; font-size: 1.6rem; }
.muted { color: var(--raavi-text-muted); margin: 0; }
.description { white-space: pre-wrap; }
.home-link { display: inline-flex; gap: var(--raavi-space-2); align-items: center; }
.columns { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: var(--raavi-space-4); }
@media (max-width: 1100px) { .columns { grid-template-columns: minmax(0, 1fr); } }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.list li { display: flex; justify-content: space-between; gap: var(--raavi-space-2); }
</style>
