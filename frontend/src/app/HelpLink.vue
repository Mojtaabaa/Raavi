<script setup lang="ts">
import { useI18n } from 'vue-i18n'

/** Link to the user guide, which is a static site shipped with the web files (web/help/fa). Opens in its own tab. */
defineProps<{ compact?: boolean }>()
const { t } = useI18n()
const HELP = '/help/fa/'
</script>

<template>
  <a class="help-link" :href="HELP" target="_blank" rel="noopener" :aria-label="compact ? t('help.open') : undefined" :title="t('help.open')" data-testid="help-link">
    <i class="pi pi-question-circle" aria-hidden="true" />
    <span v-if="!compact">{{ t('help.open') }}</span>
  </a>
</template>

<style scoped>
.help-link { display: inline-flex; align-items: center; gap: var(--raavi-space-1); color: var(--raavi-text-muted); text-decoration: none; padding: var(--raavi-space-1) var(--raavi-space-2); border-radius: var(--raavi-radius-sm); }
.help-link:hover { color: var(--p-primary-color); background: var(--p-primary-50, rgba(0, 0, 0, 0.04)); }
</style>
