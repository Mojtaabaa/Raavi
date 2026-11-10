<script setup lang="ts">
import { onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'

/** A floating "back to top" button on long pages (after scrolling about a screen and a half). */
const { t } = useI18n()
const shown = ref(false)
const onScroll = () => (shown.value = window.scrollY > 900)
const top = () => window.scrollTo({ top: 0, behavior: window.matchMedia?.('(prefers-reduced-motion: reduce)').matches ? 'auto' : 'smooth' })
onMounted(() => window.addEventListener('scroll', onScroll, { passive: true }))
onBeforeUnmount(() => window.removeEventListener('scroll', onScroll))
</script>

<template>
  <button v-if="shown" type="button" class="to-top" :aria-label="t('shell.backToTop')" :title="t('shell.backToTop')" data-testid="back-to-top" @click="top"><i class="pi pi-arrow-up" aria-hidden="true" /></button>
</template>

<style scoped>
.to-top { position: fixed; inset-inline-end: 1.2rem; inset-block-end: 1.2rem; z-index: 30; inline-size: 2.8rem; block-size: 2.8rem; border-radius: 50%; border: 0; cursor: pointer; background: var(--p-primary-color); color: var(--p-primary-contrast-color, #fff); box-shadow: var(--raavi-shadow-2); display: grid; place-items: center; }
.to-top .pi { color: inherit; }
.to-top:focus-visible { outline: 3px solid var(--raavi-text); outline-offset: 2px; }
</style>
