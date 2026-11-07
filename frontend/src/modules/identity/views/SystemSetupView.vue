<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import QRCode from 'qrcode'
import Card from 'primevue/card'
import InputText from 'primevue/inputtext'
import Password from 'primevue/password'
import Button from 'primevue/button'
import Message from 'primevue/message'
import { identityApi, takeFragmentToken } from '@/core/api/identity'
import { errorMessage } from '@/core/api/errors'
import { useAuthStore } from '@/stores/auth'

const { t, te } = useI18n()
const router = useRouter()
const auth = useAuthStore()

const token = ref<string | null>(null)
const email = ref<string | null>(null)
const invalid = ref(false)
const secret = ref<string | null>(null)
const qr = ref<string | null>(null)
const displayName = ref('')
const password = ref('')
const code = ref('')
const busy = ref(false)
const error = ref<string | null>(null)

onMounted(async () => {
  token.value = takeFragmentToken()
  if (!token.value) {
    invalid.value = true
    return
  }
  try {
    email.value = (await identityApi.inspectSetup(token.value)).email
  } catch {
    invalid.value = true
  }
})

async function begin() {
  if (!token.value) return
  try {
    const r = await identityApi.beginSetup(token.value)
    secret.value = r.secret
    // QR code rendered locally (no external service): the secret never leaves the browser/server.
    qr.value = await QRCode.toDataURL(r.otpAuthUri, { margin: 1, width: 192 })
  } catch (e) {
    error.value = errorMessage(e, t, te)
  }
}

async function complete() {
  if (!token.value || busy.value) return
  busy.value = true
  error.value = null
  try {
    await auth.completeSetup(token.value, displayName.value, password.value, code.value)
    await router.replace({ name: 'system-tenants' })
  } catch (e) {
    error.value = errorMessage(e, t, te)
    code.value = ''
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="auth-page">
    <Card class="auth-card">
      <template #title><h1 class="auth-title">{{ t('setup.title') }}</h1></template>
      <template #content>
        <Message v-if="invalid" severity="warn" :closable="false">{{ t('errors.setup.invalid') }}</Message>
        <template v-else-if="email">
          <p dir="ltr" class="email">{{ email }}</p>
          <Button v-if="!secret" :label="t('setup.start')" @click="begin" />
          <form v-else class="auth-form" novalidate @submit.prevent="complete">
            <p>{{ t('setup.scan') }}</p>
            <img v-if="qr" :src="qr" alt="" width="192" height="192" class="qr" />
            <p class="small">{{ t('setup.manual') }} <code dir="ltr">{{ secret }}</code></p>
            <label for="name">{{ t('common.displayName') }}</label>
            <InputText id="name" v-model="displayName" autocomplete="name" />
            <label for="pw">{{ t('common.password') }}</label>
            <Password input-id="pw" v-model="password" :feedback="false" toggle-mask autocomplete="new-password" :placeholder="t('invitation.passwordHint')" :input-props="{ dir: 'ltr' }" />
            <p class="small muted">{{ t('setup.existingNote') }}</p>
            <label for="otp">{{ t('auth.code') }}</label>
            <InputText id="otp" v-model="code" inputmode="numeric" autocomplete="one-time-code" maxlength="6" pattern="[0-9]{6}" dir="ltr" class="otp" data-testid="otp" />
            <Message v-if="error" severity="error" :closable="false">{{ error }}</Message>
            <Button type="submit" :label="t('setup.complete')" :loading="busy" />
          </form>
        </template>
        <p v-else>{{ t('common.loading') }}</p>
      </template>
    </Card>
  </section>
</template>

<style scoped>
.auth-page { display: grid; place-items: center; padding-block: var(--raavi-space-8); }
.auth-card { inline-size: min(100%, 30rem); }
.auth-title { margin: 0; font-size: 1.35rem; }
.auth-form { display: grid; gap: var(--raavi-space-2); }
.auth-form :deep(.p-password), .auth-form :deep(.p-password-input) { inline-size: 100%; }
.qr { justify-self: center; border-radius: var(--raavi-radius-sm); background: #fff; }
.email { font-weight: 600; }
.small { font-size: 0.85rem; }
.muted { color: var(--raavi-text-muted); }
code { word-break: break-all; }
.otp { inline-size: 9rem; letter-spacing: 0.3em; font-variant-numeric: tabular-nums; }
</style>
