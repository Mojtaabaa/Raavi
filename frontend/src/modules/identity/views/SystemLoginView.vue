<script setup lang="ts">
import BrandLogo from '@/app/BrandLogo.vue'
import HelpLink from '@/app/HelpLink.vue'
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import Card from 'primevue/card'
import InputText from 'primevue/inputtext'
import Password from 'primevue/password'
import Button from 'primevue/button'
import Message from 'primevue/message'
import { ApiError } from '@/core/api/http'
import { identityApi } from '@/core/api/identity'
import { errorMessage } from '@/core/api/errors'
import { useAuthStore } from '@/stores/auth'
import { safeRedirect } from '@/router'

const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const email = ref('')
const password = ref('')
const code = ref('')
const mfa = ref(true) // Raavi:Authentication:SystemMfaRequired
const busy = ref(false)
onMounted(async () => {
  mfa.value = (await identityApi.systemOptions().catch(() => ({ mfaRequired: true }))).mfaRequired
})
const error = ref<string | null>(null)

async function submit() {
  if (busy.value) return
  busy.value = true
  error.value = null
  try {
    await auth.systemLogin(email.value, password.value, code.value)
    await router.replace(safeRedirect(route.query.redirect) ?? { name: 'system-tenants' })
  } catch (e) {
    // Correct temporary password of a bootstrap account: continue with the first sign-in (new password + authenticator) instead of failing.
    if (e instanceof ApiError && e.code === 'auth.first_login_required') {
      try {
        const started = await identityApi.systemFirstLogin(email.value, password.value)
        const hash = new URL(started.acceptUrl, window.location.origin).hash
        password.value = ''
        await router.replace({ name: 'system-setup', hash })
        return
      } catch (inner) {
        error.value = errorMessage(inner, t, te)
        password.value = ''
        return
      }
    }
    error.value = errorMessage(e, t, te)
    password.value = ''
    code.value = ''
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="auth-page">
    <BrandLogo class="auth-logo" :size="64" />
    <Card class="auth-card">
      <template #title><h1 class="auth-title">{{ t('auth.systemTitle') }}</h1></template>
      <template #content>
        <form class="auth-form" novalidate @submit.prevent="submit">
          <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>
          <label for="email">{{ t('common.email') }}</label>
          <InputText id="email" v-model="email" type="email" dir="ltr" autocomplete="username" />
          <label for="password">{{ t('common.password') }}</label>
          <Password input-id="password" v-model="password" :feedback="false" toggle-mask autocomplete="current-password" :input-props="{ dir: 'ltr' }" />
          <label v-if="mfa" for="otp">{{ t('auth.code') }}</label>
          <InputText v-if="mfa" id="otp" v-model="code" inputmode="numeric" autocomplete="one-time-code" maxlength="6" pattern="[0-9]{6}" dir="ltr" class="otp" data-testid="otp" />
          <Button type="submit" :label="t('auth.signIn')" :loading="busy" />
        </form>
      </template>
    </Card>
    <HelpLink />
  </section>
</template>

<style scoped>
.auth-page { display: grid; place-items: center; gap: var(--raavi-space-4); padding-block: var(--raavi-space-8); }
.auth-card { inline-size: min(100%, 26rem); }
.auth-title { margin: 0; font-size: 1.35rem; }
.auth-form { display: grid; gap: var(--raavi-space-2); }
.auth-form :deep(.p-password), .auth-form :deep(.p-password-input) { inline-size: 100%; }
.otp { inline-size: 9rem; letter-spacing: 0.3em; font-variant-numeric: tabular-nums; }
</style>
