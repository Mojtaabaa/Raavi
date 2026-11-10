<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import Button from 'primevue/button'

/** An address that does not exist in the application: say so (the old behaviour was a silent redirect to the home page) and offer the ways on. */
const { t } = useI18n()
const route = useRoute()
/** The last part of the address (without its extension) as a starting point for a search. */
const lastWord = [route.params.pathMatch ?? []].flat().pop()?.replace(/\.[a-z0-9]+$/i, '') ?? ''
</script>

<template>
  <section class="nf" data-testid="page-not-found">
    <p class="code" aria-hidden="true">404</p>
    <h1>{{ t('notFoundPage.title') }}</h1>
    <p class="muted">{{ t('notFoundPage.help') }}</p>
    <p class="path"><code dir="ltr">{{ route.fullPath }}</code></p>
    <div class="actions">
      <Button as="router-link" :to="{ name: 'home' }" icon="pi pi-home" :label="t('notFoundPage.home')" />
      <Button as="router-link" :to="{ name: 'search', query: { q: lastWord } }" outlined icon="pi pi-search" :label="t('notFoundPage.search')" />
    </div>
  </section>
</template>

<style scoped>
.nf { display: grid; justify-items: center; gap: var(--raavi-space-2); text-align: center; padding-block: var(--raavi-space-8); }
.code { margin: 0; font-size: 4rem; font-weight: 800; color: var(--p-primary-color); line-height: 1; }
h1 { margin: 0; font-size: 1.4rem; }
.muted { color: var(--raavi-text-muted); margin: 0; }
.path { margin: 0; max-inline-size: 100%; overflow-wrap: anywhere; }
.actions { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; justify-content: center; margin-block-start: var(--raavi-space-3); }
</style>
