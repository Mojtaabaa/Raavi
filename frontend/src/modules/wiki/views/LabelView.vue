<script setup lang="ts">
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { contentRoute, wikiApi, type ContentListItem } from '@/core/api/wiki'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'

const { t } = useI18n()
const route = useRoute()
const locale = useLocaleStore()
const items = ref<ContentListItem[]>([])
const loading = ref(true)

watch(
  () => route.params.name,
  async (name) => {
    loading.value = true
    items.value = await wikiApi.labelContent(String(name)).catch(() => [])
    loading.value = false
  },
  { immediate: true },
)
</script>

<template>
  <section class="label-page">
    <h1><i class="pi pi-tag" aria-hidden="true" /> {{ route.params.name }}</h1>
    <p v-if="!loading && items.length === 0" class="muted">{{ t('wiki.labels.noContent') }}</p>
    <ul class="list">
      <li v-for="c in items" :key="c.id">
        <RouterLink :to="contentRoute(c)">{{ c.title }}</RouterLink>
        <small class="muted">{{ c.spaceName }} · {{ formatDate(c.updatedAt, locale.locale) }}</small>
      </li>
    </ul>
  </section>
</template>

<style scoped>
.label-page { max-inline-size: 60rem; margin-inline: auto; display: grid; gap: var(--raavi-space-3); }
h1 { margin: 0; font-size: 1.4rem; }
.list { list-style: none; padding: 0; margin: 0; display: grid; gap: var(--raavi-space-2); }
.list li { display: flex; justify-content: space-between; gap: var(--raavi-space-2); flex-wrap: wrap; }
.muted { color: var(--raavi-text-muted); }
</style>
