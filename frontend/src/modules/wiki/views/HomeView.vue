<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Card from 'primevue/card'
import Button from 'primevue/button'
import { wikiApi, type Activity, type ContentListItem, type SpaceSummary } from '@/core/api/wiki'
import { useHostStore } from '@/stores/host'
import { useLocaleStore } from '@/stores/locale'
import { useAuthStore } from '@/stores/auth'
import ActivityList from '../components/ActivityList.vue'
import ActivityChart from '../components/ActivityChart.vue'

const { t } = useI18n()
const host = useHostStore()
const locale = useLocaleStore()
const auth = useAuthStore()

const spaces = ref<SpaceSummary[]>([])
const activity = ref<Activity[]>([])
const drafts = ref<ContentListItem[]>([])
const loading = ref(true)

/** Tenant welcome message — plain text, rendered with interpolation (never v-html). */
const welcome = computed(() => {
  const b = host.tenant?.branding
  if (!b) return null
  return (locale.locale === 'fa' ? b.welcomeMessageFa : b.welcomeMessageEn) ?? b.welcomeMessageFa ?? b.welcomeMessageEn
})

onMounted(async () => {
  try {
    ;[spaces.value, activity.value, drafts.value] = await Promise.all([wikiApi.spaces(), wikiApi.activity(), wikiApi.myDrafts().catch(() => [])])
  } catch {
    spaces.value = []
    activity.value = []
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <section class="home">
    <header>
      <h1>{{ t('wiki.home.greeting', { name: auth.me?.displayName ?? '' }) }}</h1>
      <p v-if="welcome" class="welcome" data-testid="welcome">{{ welcome }}</p>
    </header>

    <ActivityChart />

    <div class="columns">
      <Card>
        <template #title>
          <div class="card-title">
            <span>{{ t('wiki.home.spaces') }}</span>
            <Button as="router-link" :to="{ name: 'spaces' }" text size="small" :label="t('wiki.home.allSpaces')" />
          </div>
        </template>
        <template #content>
          <p v-if="!loading && spaces.length === 0" class="muted">{{ t('wiki.home.noSpaces') }}</p>
          <ul class="spaces">
            <li v-for="s in spaces.slice(0, 12)" :key="s.id">
              <RouterLink :to="{ name: 'space', params: { key: s.key } }" class="space">
                <span class="avatar" aria-hidden="true">{{ s.name.slice(0, 1) }}</span>
                <span class="name">{{ s.name }}</span>
                <small class="muted">{{ s.key }}</small>
              </RouterLink>
            </li>
          </ul>
        </template>
      </Card>

      <Card v-if="drafts.length">
        <template #title>{{ t('wiki.home.myDrafts') }}</template>
        <template #content>
          <ul class="spaces">
            <li v-for="d in drafts" :key="d.id">
              <RouterLink :to="{ name: 'page-edit', params: { key: d.spaceKey, id: d.id } }" dir="auto">{{ d.title }}</RouterLink>
              <small class="muted"> · {{ d.spaceName }}</small>
            </li>
          </ul>
        </template>
      </Card>

      <Card>
        <template #title>{{ t('wiki.home.recentActivity') }}</template>
        <template #content><ActivityList :items="activity" show-space /></template>
      </Card>
    </div>
  </section>
</template>

<style scoped>
.home { max-inline-size: var(--raavi-content-max); margin-inline: auto; display: grid; gap: var(--raavi-space-4); }
h1 { margin: 0; font-size: 1.5rem; }
.welcome { margin: var(--raavi-space-2) 0 0; color: var(--raavi-text-muted); white-space: pre-wrap; }
.columns { display: grid; grid-template-columns: minmax(0, 1fr) minmax(0, 1fr); gap: var(--raavi-space-4); }
@media (max-width: 900px) { .columns { grid-template-columns: minmax(0, 1fr); } }
.card-title { display: flex; align-items: center; justify-content: space-between; }
.spaces { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.space { display: flex; align-items: center; gap: var(--raavi-space-2); text-decoration: none; color: inherit; padding: var(--raavi-space-1); border-radius: var(--raavi-radius-sm); }
.space:hover { background: var(--p-primary-50, rgba(0, 0, 0, 0.04)); }
.avatar { display: grid; place-items: center; inline-size: 2rem; block-size: 2rem; border-radius: var(--raavi-radius-sm); background: var(--p-primary-color); color: var(--p-primary-contrast-color); font-weight: 700; }
.name { font-weight: 600; flex: 1; }
.muted { color: var(--raavi-text-muted); }
</style>
