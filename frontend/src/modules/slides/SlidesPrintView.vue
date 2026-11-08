<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import Button from 'primevue/button'
import { slidesApi, type SlideDeck } from '@/core/api/slides'
import { errorMessage } from '@/core/api/errors'
import SlideCanvas from './SlideCanvas.vue'

/**
 * Print page of a deck (Phase 9d, PDF export): every slide on its own landscape 16:9 page, without the application shell. The browser's
 * "Save as PDF" produces the file, so Persian/Arabic text is shaped by the same engine that shows it on screen. With ?autoprint=1 the
 * print dialog opens as soon as the slides are rendered.
 */
const { t, te } = useI18n()
const route = useRoute()
const deck = ref<SlideDeck | null>(null)
const error = ref('')

onMounted(async () => {
  try {
    deck.value = await slidesApi.get(String(route.params.id))
    document.title = deck.value.title
    if (route.query.autoprint === '1' && typeof window.print === 'function') {
      await new Promise((r) => setTimeout(r, 150)) // let layout settle before the dialog
      window.print()
    }
  } catch (e) {
    error.value = errorMessage(e, t, te)
  }
})

function print() {
  window.print()
}
</script>

<template>
  <main class="print" data-testid="slides-print">
    <p v-if="error" class="error" role="alert">{{ error }}</p>
    <template v-if="deck">
      <div class="bar">
        <strong dir="auto">{{ deck.title }}</strong>
        <Button icon="pi pi-print" :label="t('slides.printSave')" data-testid="print-now" @click="print" />
      </div>
      <section v-for="s in deck.slides" :key="s.id" class="page" data-testid="print-slide">
        <SlideCanvas :layout="s.layout" :title="s.title" :body="s.body" :direction="deck.direction" />
      </section>
    </template>
  </main>
</template>

<style scoped>
.print { padding: 1rem; display: grid; gap: 1rem; max-inline-size: 64rem; margin-inline: auto; }
.bar { display: flex; justify-content: space-between; align-items: center; gap: 1rem; }
.page { border: 1px solid var(--raavi-border); border-radius: 6px; }
.error { color: var(--raavi-danger, #b91c1c); }
</style>

<style>
@page { size: 13.333in 7.5in; margin: 0; }
@media print {
  [data-testid='slides-print'] { padding: 0; gap: 0; max-inline-size: none; display: block; }
  [data-testid='slides-print'] .bar { display: none; }
  [data-testid='slides-print'] .page { border: 0; border-radius: 0; break-after: page; page-break-after: always; inline-size: 13.333in; block-size: 7.5in; }
  [data-testid='slides-print'] .page > * { border-radius: 0; block-size: 100%; }
  [data-testid='slides-print'] .page:last-child { break-after: auto; page-break-after: auto; }
  * { print-color-adjust: exact; -webkit-print-color-adjust: exact; }
}
</style>
