<script setup lang="ts">
import { useI18n } from 'vue-i18n'

const { t } = useI18n()
</script>

<template>
  <main class="not-found" data-testid="workspace-not-found">
    <span class="mark" aria-hidden="true">ر</span>
    <h1>{{ t('host.notFound.title') }}</h1>
    <p>{{ t('host.notFound.description') }}</p>
  </main>
</template>

<style scoped>
.not-found {
  display: grid;
  place-items: center;
  align-content: center;
  gap: var(--raavi-space-3);
  min-block-size: 100vh;
  padding-inline: var(--raavi-space-4);
  text-align: center;
}
.mark {
  display: grid;
  place-items: center;
  inline-size: 56px;
  block-size: 56px;
  border-radius: var(--raavi-radius-md);
  background: var(--p-primary-color);
  color: var(--p-primary-contrast-color);
  font-size: 1.75rem;
  font-family: var(--raavi-font-fa);
}
h1 { margin: 0; font-size: 1.5rem; }
p { margin: 0; max-inline-size: 36rem; color: var(--raavi-text-muted); }
</style>
