<script setup lang="ts">
import { watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import Toast from 'primevue/toast'
import ConfirmDialog from 'primevue/confirmdialog'
import AppShell from '@/layouts/AppShell.vue'
import ConnectionBanner from '@/app/ConnectionBanner.vue'
import { useLocaleStore } from '@/stores/locale'

const route = useRoute()
const { locale } = useI18n()
const localeStore = useLocaleStore()

// The locale store is the single source of truth; vue-i18n follows it.
watch(() => localeStore.locale, (value) => (locale.value = value), { immediate: true })
</script>

<template>
  <Toast position="bottom-center" />
  <ConfirmDialog />
  <ConnectionBanner />
  <RouterView v-if="route.meta.bare" />
  <AppShell v-else>
    <RouterView />
  </AppShell>
</template>
