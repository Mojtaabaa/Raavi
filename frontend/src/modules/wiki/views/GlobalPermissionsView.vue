<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import { wikiApi, type Grant } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import GrantMatrix from '../components/GrantMatrix.vue'

/** Tenant-scope grants: who besides tenant administrators may create spaces. */
const { t, te } = useI18n()
const toast = useToast()
const grants = ref<Grant[]>([])
const loaded = ref(false)
const saving = ref(false)

onMounted(async () => {
  try {
    grants.value = (await wikiApi.tenantPermissions()).grants
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  } finally {
    loaded.value = true
  }
})

async function save() {
  saving.value = true
  try {
    grants.value = (await wikiApi.saveTenantPermissions(grants.value)).grants
    toast.add({ severity: 'success', summary: t('common.saved'), life: 2500 })
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <section class="page">
    <h1>{{ t('wiki.globalPermissions.title') }}</h1>
    <p class="muted">{{ t('wiki.globalPermissions.help') }}</p>
    <GrantMatrix v-if="loaded" v-model="grants" :actions="['Create']" id-prefix="tp" />
    <div><Button :label="t('common.save')" :loading="saving" @click="save" /></div>
  </section>
</template>

<style scoped>
.page { max-inline-size: 48rem; margin-inline: auto; display: grid; gap: var(--raavi-space-3); }
h1 { margin: 0; font-size: 1.4rem; }
.muted { color: var(--raavi-text-muted); }
</style>
