<script setup lang="ts">
import { computed } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { staticTrail } from '@/core/navigation/trail'
import { useCrumbsStore } from '@/stores/crumbs'

/**
 * Where am I, and the way back up: Home › Spaces › Space › … › Page. The trail comes from the view (it knows the space and the page
 * ancestors) or from the route. Long trails keep the first and the last two entries on phones and fold the middle into "…" (still
 * reachable: the folded entries show in a menu). The last entry is the current page (aria-current), not a link.
 */
const { t } = useI18n()
const route = useRoute()
const crumbs = useCrumbsStore()

const trail = computed(() => crumbs.trail ?? staticTrail(route.name as string | null, t))
const items = computed(() => trail.value.map((c, i) => ({ ...c, last: i === trail.value.length - 1, middle: i > 0 && i < trail.value.length - 2 })))
const folded = computed(() => items.value.filter((i) => i.middle))
</script>

<template>
  <nav v-if="items.length > 1" class="crumbs" :aria-label="t('nav.breadcrumbs')" data-testid="breadcrumbs">
    <ol>
      <li v-for="(c, i) in items" :key="i" :class="{ middle: c.middle }" :data-last="c.last || undefined">
        <i v-if="i > 0" class="pi pi-angle-right sep" aria-hidden="true" />
        <span v-if="c.last" class="here" :title="c.label" aria-current="page" dir="auto">{{ c.label }}</span>
        <RouterLink v-else-if="c.to" :to="c.to" class="link" :title="c.label" dir="auto">{{ c.label }}</RouterLink>
        <span v-else class="plain" dir="auto">{{ c.label }}</span>
        <!-- phones: the folded middle entries sit behind one "…" placed right after the first crumb -->
        <details v-if="i === 0 && folded.length" class="fold">
          <summary :aria-label="t('nav.breadcrumbsMore')">…</summary>
          <ul>
            <li v-for="(f, j) in folded" :key="j"><RouterLink v-if="f.to" :to="f.to">{{ f.label }}</RouterLink><span v-else>{{ f.label }}</span></li>
          </ul>
        </details>
      </li>
    </ol>
  </nav>
</template>

<style scoped>
.crumbs { max-inline-size: 90rem; margin: 0 auto var(--raavi-space-3); font-size: 0.88rem; }
ol { list-style: none; display: flex; align-items: center; flex-wrap: wrap; gap: 2px var(--raavi-space-1); margin: 0; padding: 0; }
li { display: inline-flex; align-items: center; gap: var(--raavi-space-1); min-inline-size: 0; }
.sep { font-size: 0.7rem; color: var(--raavi-text-muted); }
.crumbs :dir(rtl) .sep, .sep:dir(rtl) { transform: scaleX(-1); }
.link, .plain, .here { max-inline-size: 16rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; padding: 2px 6px; border-radius: var(--raavi-radius-sm); }
.link { color: var(--raavi-text-muted); text-decoration: none; }
.link:hover { color: var(--p-primary-color); background: var(--p-primary-50, rgba(0, 0, 0, 0.05)); }
.plain { color: var(--raavi-text-muted); }
.here { font-weight: 600; color: var(--raavi-text); }
.fold { display: none; position: relative; }
.fold summary { cursor: pointer; list-style: none; padding: 0 6px; color: var(--raavi-text-muted); }
.fold ul { position: absolute; inset-inline-start: 0; z-index: 20; margin: 4px 0 0; padding: var(--raavi-space-1); list-style: none; background: var(--raavi-surface); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); box-shadow: var(--raavi-shadow-2); min-inline-size: 10rem; display: grid; }
@media (max-width: 640px) {
  li.middle { display: none; }
  .fold { display: inline-block; }
  .link, .plain, .here { max-inline-size: 9rem; }
}
</style>
