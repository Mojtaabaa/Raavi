<script setup lang="ts">
import { useThemeStore } from '@/stores/theme'
/**
 * The Raavi logo (icon + wordmark). The dark wordmark is used on light themes and the white one on dark themes.
 * `src` replaces the Raavi icon by a workspace's own square logo (icon mode only).
 * `icon` shows only the square icon. Files live in public/ (brand package, docs/branding).
 */
defineProps<{ icon?: boolean; size?: number; src?: string | null }>()
const theme = useThemeStore()

// Files of public/, served from the site root. Bound (not literal) so the bundler leaves them alone.
const ICON = '/raavi-icon.svg'
const LOGO = '/raavi-logo.svg'
const LOGO_WHITE = '/raavi-logo-white.svg'
</script>

<template>
  <img v-if="icon" class="brand-icon" :src="src || ICON" alt="" :width="size ?? 32" :height="size ?? 32" />
  <picture v-else class="brand-logo">
    <img :src="theme.dark ? LOGO_WHITE : LOGO" alt="Raavi" :height="size ?? 56" :style="{ blockSize: `${size ?? 56}px`, inlineSize: 'auto' }" />
  </picture>
</template>

<style scoped>
.brand-icon { display: block; border-radius: 8px; }
.brand-logo { display: inline-block; line-height: 0; }
.brand-logo img { display: block; max-inline-size: 100%; }
</style>
