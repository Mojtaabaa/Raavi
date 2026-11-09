<script setup lang="ts">
import { useI18n } from 'vue-i18n'
import { THEMES, type ThemeId } from '@/core/theme/themes'
import { useAuthStore } from '@/stores/auth'
import { useThemeStore } from '@/stores/theme'

/** Colour theme choice: a swatch per theme. Saved in the profile for signed-in users (stores/theme.ts). */
const { t } = useI18n()
const theme = useThemeStore()
const auth = useAuthStore()
const pick = (id: ThemeId) => theme.choose(id, !!auth.me)
</script>

<template>
  <div class="themes" role="radiogroup" :aria-label="t('theme.title')" data-testid="theme-picker">
    <button
      v-for="th in THEMES"
      :key="th.id"
      type="button"
      role="radio"
      class="theme"
      :class="{ on: theme.theme === th.id }"
      :aria-checked="theme.theme === th.id"
      :data-testid="`theme-${th.id}`"
      @click="pick(th.id)"
    >
      <span class="swatch" aria-hidden="true" :style="{ '--a': th.swatch[0], '--b': th.swatch[1], '--c': th.swatch[2] }"><i /><i /><i /></span>
      <span class="label">{{ t(`theme.names.${th.id}`) }}</span>
    </button>
  </div>
</template>

<style scoped>
.themes { display: grid; grid-template-columns: repeat(auto-fill, minmax(8.5rem, 1fr)); gap: var(--raavi-space-2); }
.theme { display: flex; align-items: center; gap: var(--raavi-space-2); padding: var(--raavi-space-2); background: var(--raavi-surface); color: var(--raavi-text); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); font: inherit; cursor: pointer; text-align: start; }
.theme:hover { border-color: var(--p-primary-color); }
.theme.on { border-color: var(--p-primary-color); box-shadow: 0 0 0 2px var(--p-primary-color); }
.swatch { display: inline-flex; flex: none; border-radius: 6px; overflow: hidden; border: 1px solid var(--raavi-border); }
.swatch i { display: block; inline-size: 0.9rem; block-size: 1.6rem; }
.swatch i:nth-child(1) { background: var(--a); }
.swatch i:nth-child(2) { background: var(--b); }
.swatch i:nth-child(3) { background: var(--c); }
.label { font-size: 0.9rem; }
</style>
