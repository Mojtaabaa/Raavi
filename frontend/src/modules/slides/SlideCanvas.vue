<script setup lang="ts">
import { computed } from 'vue'
import type { SlideDirection, SlideLayout } from '@/core/api/slides'

/**
 * Renders one 16:9 slide. All sizes are in container-query units, so the same markup serves the editor preview, present mode and the
 * printed page. Text is rendered as text (never as HTML).
 */
const props = defineProps<{ layout: SlideLayout; title: string; body: string; direction: SlideDirection }>()

const lines = computed(() => (props.body ? props.body.split('\n') : []))
const dir = computed(() => (props.direction === 'Rtl' ? 'rtl' : 'ltr'))
</script>

<template>
  <div class="slide" :class="`layout-${layout.toLowerCase()}`" :dir="dir" data-testid="slide-canvas">
    <template v-if="layout === 'Title'">
      <h2 class="cover-title">{{ title }}</h2>
      <p v-for="(l, i) in lines" :key="i" class="cover-sub">{{ l }}</p>
    </template>
    <template v-else>
      <h2 class="head">{{ title }}</h2>
      <ul v-if="layout === 'Bullets'" class="body">
        <template v-for="(l, i) in lines" :key="i"><li v-if="l">{{ l }}</li></template>
      </ul>
      <div v-else class="body">
        <p v-for="(l, i) in lines" :key="i">{{ l || ' ' }}</p>
      </div>
    </template>
  </div>
</template>

<style scoped>
.slide {
  container-type: inline-size;
  aspect-ratio: 16 / 9;
  inline-size: 100%;
  box-sizing: border-box;
  background: #fff;
  color: #1b2430;
  padding: 5cqw 5.2cqw;
  overflow: hidden;
  display: flex;
  flex-direction: column;
  border-radius: 4px;
  font-family: Calibri, 'Segoe UI', Vazirmatn, Tahoma, sans-serif;
}
h2, p, ul { margin: 0; }
.head { font-size: 4.2cqw; font-weight: 700; color: #1f3a5f; margin-block-end: 2.5cqw; overflow-wrap: anywhere; }
.body { font-size: 2.9cqw; line-height: 1.35; overflow-wrap: anywhere; }
.body li { margin-block-end: 0.8cqw; }
ul.body { padding-inline-start: 3.5cqw; }
.layout-title { background: #1f3a5f; color: #fff; justify-content: center; align-items: center; text-align: center; }
.cover-title { font-size: 6cqw; font-weight: 700; margin-block-end: 2cqw; overflow-wrap: anywhere; }
.cover-sub { font-size: 3cqw; color: #d6e0ee; overflow-wrap: anywhere; }
</style>
