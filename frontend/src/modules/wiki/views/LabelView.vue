<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import Checkbox from 'primevue/checkbox'
import SelectButton from 'primevue/selectbutton'
import { contentRoute, wikiApi, type ContentListItem, type LabelDetail } from '@/core/api/wiki'
import { formatDate } from '@/core/i18n/locales'
import { useLocaleStore } from '@/stores/locale'

const { t } = useI18n()
const route = useRoute()
const router = useRouter()
const locale = useLocaleStore()
const detail = ref<LabelDetail | null>(null)
const items = ref<ContentListItem[]>([])
const total = ref(0)
const loading = ref(true)
const tagMode = ref<'any' | 'all'>((route.query.tagMode as 'any' | 'all') || 'any')
const includeDescendants = ref(route.query.includeDescendants === 'true')
const modeOptions = computed(() => [
  { value: 'any', label: t('wiki.labels.matchAny') },
  { value: 'all', label: t('wiki.labels.matchAll') },
])

async function load() {
  const name = String(route.params.name)
  loading.value = true
  try {
    detail.value = await wikiApi.labelDetail(name).catch(() => null)
    const search = await wikiApi.labelSearch({
      tags: name,
      tagMode: tagMode.value,
      includeDescendants: includeDescendants.value,
      take: 100,
    }).catch(() => ({ items: [] as ContentListItem[], total: 0 }))
    items.value = search.items
    total.value = search.total
    await router.replace({
      query: {
        ...route.query,
        tagMode: tagMode.value,
        includeDescendants: includeDescendants.value ? 'true' : undefined,
      },
    })
  } finally {
    loading.value = false
  }
}

watch(() => [route.params.name, tagMode.value, includeDescendants.value], load, { immediate: true })
</script>

<template>
  <section class="label-page" data-testid="label-page">
    <header class="head">
      <h1>
        <span v-if="detail?.color" class="swatch" :style="{ background: detail.color }" aria-hidden="true" />
        <i v-else class="pi pi-tag" aria-hidden="true" />
        {{ detail?.displayName ?? route.params.name }}
      </h1>
      <p v-if="detail?.description" class="muted">{{ detail.description }}</p>
      <p v-if="detail?.parentSlug" class="muted">
        {{ t('wiki.labels.parent') }}:
        <RouterLink :to="{ name: 'label', params: { name: detail.parentSlug } }">{{ detail.parentDisplayName }}</RouterLink>
      </p>
      <p class="muted">{{ t('wiki.labels.usageCount', { n: detail?.usageCount ?? total }) }}</p>
    </header>

    <div class="filters">
      <SelectButton v-model="tagMode" :options="modeOptions" option-label="label" option-value="value" :allow-empty="false" data-testid="tag-mode" />
      <label class="desc">
        <Checkbox v-model="includeDescendants" binary input-id="incl-desc" data-testid="include-descendants" />
        <span>{{ t('wiki.labels.includeDescendants') }}</span>
      </label>
    </div>

    <section v-if="detail?.children?.length" class="related">
      <h2>{{ t('wiki.labels.children') }}</h2>
      <div class="chips">
        <RouterLink v-for="c in detail.children" :key="c.id" class="chip" :to="{ name: 'label', params: { name: c.slug } }">{{ c.displayName }}</RouterLink>
      </div>
    </section>
    <section v-if="detail?.related?.length" class="related">
      <h2>{{ t('wiki.labels.related') }}</h2>
      <div class="chips">
        <RouterLink v-for="c in detail.related" :key="c.id" class="chip" :to="{ name: 'label', params: { name: c.slug } }">{{ c.displayName }}</RouterLink>
      </div>
    </section>

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
.head h1 { margin: 0; font-size: 1.4rem; display: flex; align-items: center; gap: var(--raavi-space-2); }
.swatch { inline-size: 0.9rem; block-size: 0.9rem; border-radius: 2px; }
.filters { display: flex; flex-wrap: wrap; gap: var(--raavi-space-3); align-items: center; }
.desc { display: inline-flex; align-items: center; gap: var(--raavi-space-2); }
.chips { display: flex; flex-wrap: wrap; gap: var(--raavi-space-2); }
.chip { padding: 0.15rem 0.55rem; border-radius: 999px; background: var(--raavi-surface-2, var(--raavi-border)); text-decoration: none; color: inherit; }
.list { list-style: none; padding: 0; margin: 0; display: grid; gap: var(--raavi-space-2); }
.list li { display: flex; justify-content: space-between; gap: var(--raavi-space-2); flex-wrap: wrap; }
.muted { color: var(--raavi-text-muted); }
h2 { margin: 0; font-size: 1rem; }
</style>
