<script setup lang="ts">
/**
 * The Raavi logo (icon + wordmark). The dark wordmark is used on light backgrounds and the white one in dark mode (prefers-color-scheme).
 * `icon` shows only the square icon. Files live in public/ (brand package, docs/branding).
 */
defineProps<{ icon?: boolean; size?: number }>()

// Files of public/, served from the site root. Bound (not literal) so the bundler leaves them alone.
const ICON = '/raavi-icon.svg'
const LOGO = '/raavi-logo.svg'
const LOGO_WHITE = '/raavi-logo-white.svg'
</script>

<template>
  <img v-if="icon" class="brand-icon" :src="ICON" alt="" :width="size ?? 32" :height="size ?? 32" />
  <picture v-else class="brand-logo">
    <source :srcset="LOGO_WHITE" media="(prefers-color-scheme: dark)" />
    <img :src="LOGO" alt="Raavi" :height="size ?? 56" :style="{ blockSize: `${size ?? 56}px`, inlineSize: 'auto' }" />
  </picture>
</template>

<style scoped>
.brand-icon { display: block; border-radius: 8px; }
.brand-logo { display: inline-block; line-height: 0; }
.brand-logo img { display: block; max-inline-size: 100%; }
</style>
