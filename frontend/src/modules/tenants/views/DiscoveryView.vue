<script setup lang="ts">
import { ref } from 'vue'
import { useI18n } from 'vue-i18n'
import Card from 'primevue/card'
import InputText from 'primevue/inputtext'
import Button from 'primevue/button'
import Message from 'primevue/message'
import { identityApi } from '@/core/api/identity'
import { useAuthStore } from '@/stores/auth'

const { t } = useI18n()
const auth = useAuthStore()
const code = ref('')
const busy = ref(false)
const notFound = ref(false)

async function go() {
  if (!code.value.trim() || busy.value) return
  busy.value = true
  notFound.value = false
  try {
    const { url } = await identityApi.discover(code.value.trim())
    // Only http(s) URLs returned by our own API are followed.
    const target = new URL(url)
    if (target.protocol === 'https:' || target.protocol === 'http:') window.location.assign(new URL('/login', target).toString())
  } catch {
    notFound.value = true
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="auth-page">
    <Card class="auth-card">
      <template #title><h1 class="auth-title">{{ t('auth.findWorkspace') }}</h1></template>
      <template #content>
        <form class="auth-form" @submit.prevent="go">
          <label for="code">{{ t('auth.workspaceCode') }}</label>
          <InputText id="code" v-model="code" dir="ltr" autocomplete="organization" autocapitalize="characters" data-testid="discovery-code" />
          <Message v-if="notFound" severity="warn" :closable="false">{{ t('auth.notFound') }}</Message>
          <Button type="submit" :label="t('auth.go')" :loading="busy" />
        </form>
        <RouterLink v-if="!auth.me?.isSystemAdministrator" :to="{ name: 'system-login' }" class="small">{{ t('auth.adminSignIn') }}</RouterLink>
        <RouterLink v-else :to="{ name: 'system-tenants' }" class="small">{{ t('nav.tenants') }}</RouterLink>
      </template>
    </Card>
  </section>
</template>

<style scoped>
.auth-page { display: grid; place-items: center; padding-block: var(--raavi-space-8); }
.auth-card { inline-size: min(100%, 26rem); }
.auth-title { margin: 0; font-size: 1.35rem; }
.auth-form { display: grid; gap: var(--raavi-space-2); margin-block-end: var(--raavi-space-4); }
.small { font-size: 0.9rem; }
</style>
