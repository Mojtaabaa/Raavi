<script setup lang="ts">
import { computed } from 'vue'

/**
 * Small dependency-free SVG charts for the status page. `bars` draws one bar per value (with an optional overlay series, e.g. errors),
 * `line` draws a line with an area under it. Colours come from CSS variables, so every theme works; each chart has a text alternative.
 */
const props = withDefaults(defineProps<{ values: number[]; overlay?: number[]; kind?: 'bars' | 'line'; label: string; unit?: string; height?: number; color?: string }>(), { overlay: () => [], kind: 'bars', unit: '', height: 72, color: 'var(--p-primary-color)' })

const W = 300
const max = computed(() => Math.max(1, ...props.values))
const step = computed(() => W / Math.max(1, props.values.length))
const bars = computed(() =>
  props.values.map((v, i) => {
    const h = (v / max.value) * (props.height - 4)
    const o = ((props.overlay[i] ?? 0) / max.value) * (props.height - 4)
    return { x: i * step.value + 0.5, w: Math.max(1, step.value - 1.2), y: props.height - h, h, oy: props.height - o, oh: o }
  }),
)
const points = computed(() => props.values.map((v, i) => [(i + 0.5) * step.value, props.height - 2 - (v / max.value) * (props.height - 6)] as const))
const linePath = computed(() => points.value.map(([x, y], i) => `${i ? 'L' : 'M'}${x.toFixed(1)} ${y.toFixed(1)}`).join(' '))
const areaPath = computed(() => (points.value.length ? `${linePath.value} L${points.value.at(-1)![0].toFixed(1)} ${props.height} L${points.value[0]![0].toFixed(1)} ${props.height} Z` : ''))
const summary = computed(() => `${props.label}: max ${Math.round(max.value)}${props.unit}, last ${Math.round(props.values.at(-1) ?? 0)}${props.unit}`)
</script>

<template>
  <figure class="chart">
    <svg :viewBox="`0 0 ${W} ${height}`" preserveAspectRatio="none" role="img" :aria-label="summary" :style="{ blockSize: `${height}px` }">
      <line x1="0" :x2="W" :y1="height - 0.5" :y2="height - 0.5" class="base" />
      <template v-if="kind === 'bars'">
        <rect v-for="(b, i) in bars" :key="i" :x="b.x" :y="b.y" :width="b.w" :height="b.h" rx="1" :fill="color" opacity="0.85" />
        <rect v-for="(b, i) in bars" v-show="b.oh > 0" :key="`o${i}`" :x="b.x" :y="b.oy" :width="b.w" :height="b.oh" rx="1" class="overlay" />
      </template>
      <template v-else>
        <path :d="areaPath" :fill="color" opacity="0.14" />
        <path :d="linePath" fill="none" :stroke="color" stroke-width="1.8" vector-effect="non-scaling-stroke" stroke-linejoin="round" />
      </template>
    </svg>
    <figcaption class="cap"><span>{{ label }}</span><span class="max">max {{ Math.round(max) }}{{ unit }}</span></figcaption>
  </figure>
</template>

<style scoped>
.chart { margin: 0; display: grid; gap: 4px; min-inline-size: 0; }
svg { inline-size: 100%; display: block; background: var(--raavi-bg); border-radius: var(--raavi-radius-sm); }
.base { stroke: var(--raavi-border); stroke-width: 1; }
.overlay { fill: var(--raavi-danger); }
.cap { display: flex; justify-content: space-between; font-size: 0.78rem; color: var(--raavi-text-muted); }
.max { font-variant-numeric: tabular-nums; }
</style>
