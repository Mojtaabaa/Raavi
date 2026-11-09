<script setup lang="ts">
import BrandLogo from '@/app/BrandLogo.vue'
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import Card from 'primevue/card'
import InputText from 'primevue/inputtext'
import Password from 'primevue/password'
import Button from 'primevue/button'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import Divider from 'primevue/divider'
import { identityApi, safeExternalUrl, type ProviderSummary } from '@/core/api/identity'
import { errorMessage } from '@/core/api/errors'
import { ApiError } from '@/core/api/http'
import { useAuthStore } from '@/stores/auth'
import { useHostStore } from '@/stores/host'
import { useLocaleStore } from '@/stores/locale'
import { safeRedirect } from '@/router'

const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const auth = useAuthStore()
const host = useHostStore()
const localeStore = useLocaleStore()

const providers = ref<ProviderSummary[] | null>(null)
const credentialProviderId = ref<string | null>(null)
const email = ref('')
const password = ref('')
const busy = ref<string | null>(null)
const error = ref<string | null>(null)

const name = (p: ProviderSummary) => (localeStore.locale === 'fa' ? p.displayName.fa || p.displayName.en : p.displayName.en || p.displayName.fa)
const credentialProviders = computed(() => providers.value?.filter((p) => p.flow === 'credentials') ?? [])
const redirectProviders = computed(() => providers.value?.filter((p) => p.flow === 'redirect') ?? [])
const selected = computed(() => credentialProviders.value.find((p) => p.id === credentialProviderId.value) ?? null)
const isLocal = computed(() => selected.value?.type === 'Local')
const tenantName = computed(() => {
  const tenant = host.tenant
  if (!tenant) return t('app.name')
  return localeStore.locale === 'fa' ? tenant.displayName.fa : tenant.displayName.en
})
const returnPath = computed(() => safeRedirect(route.query.redirect) ?? '/')

/** Errors from a provider round-trip arrive as ?error=<code> (codes only, never free text). */
function showQueryError() {
  const code = route.query.error
  if (typeof code === 'string' && /^[a-z_.]{3,64}$/.test(code)) error.value = errorMessage(new ApiError(401, code, code), t, te)
}

onMounted(async () => {
  showQueryError()
  try {
    providers.value = await identityApi.providers()
    const preferred = credentialProviders.value.find((p) => p.isDefault) ?? credentialProviders.value[0]
    credentialProviderId.value = preferred?.id ?? null
  } catch (e) {
    providers.value = []
    error.value = errorMessage(e, t, te)
  }
})
watch(() => route.query.error, showQueryError)

async function submit() {
  if (busy.value || !selected.value) return
  busy.value = 'credentials'
  error.value = null
  try {
    const me = isLocal.value
      ? await auth.loginLocal(email.value, password.value)
      : await auth.loginCredentials(selected.value.id, email.value, password.value)
    if (me.preferredLocale) localeStore.applyUserPreference(me.preferredLocale)
    await router.replace(returnPath.value)
  } catch (e) {
    error.value = errorMessage(e, t, te)
    password.value = ''
  } finally {
    busy.value = null
  }
}

async function redirectTo(p: ProviderSummary) {
  if (busy.value) return
  busy.value = p.id
  error.value = null
  try {
    const { redirectUrl } = await identityApi.beginLogin(p.id, returnPath.value)
    const target = safeExternalUrl(redirectUrl)
    if (!target) throw new ApiError(500, 'auth.provider_error', 'invalid redirect')
    window.location.assign(target)
  } catch (e) {
    error.value = errorMessage(e, t, te)
    busy.value = null
  }
}
</script>

<template>
  <section class="auth-page">
    <BrandLogo class="auth-logo" :size="64" />
    <Card class="auth-card">
      <template #title>
        <h1 class="auth-title">{{ t('auth.signInTitle', { name: tenantName }) }}</h1>
      </template>
      <template #content>
        <Message v-if="error" severity="error" :closable="false" data-testid="login-error">{{ error }}</Message>
        <p v-if="providers && providers.length === 0 && !error">{{ t('auth.noProviders') }}</p>

        <div v-if="redirectProviders.length" class="redirects">
          <Button
            v-for="p in redirectProviders"
            :key="p.id"
            :label="t('auth.continueWith', { name: name(p) })"
            :loading="busy === p.id"
            severity="secondary"
            outlined
            :icon="p.type === 'JiraDataCenter' ? 'pi pi-ticket' : 'pi pi-sign-in'"
            :data-testid="`provider-${p.id}`"
            @click="redirectTo(p)"
          />
        </div>
        <Divider v-if="redirectProviders.length && credentialProviders.length" align="center"><span class="muted small">{{ t('auth.or') }}</span></Divider>

        <form v-if="selected" class="auth-form" novalidate @submit.prevent="submit">
          <SelectButton
            v-if="credentialProviders.length > 1"
            v-model="credentialProviderId"
            :options="credentialProviders"
            option-value="id"
            :allow-empty="false"
            data-testid="credential-provider"
          >
            <template #option="{ option }">{{ name(option) }}</template>
          </SelectButton>
          <label for="email">{{ isLocal ? t('common.email') : t('auth.username') }}</label>
          <InputText id="email" v-model="email" :type="isLocal ? 'email' : 'text'" autocomplete="username" dir="ltr" required data-testid="login-email" />
          <label for="password">{{ t('common.password') }}</label>
          <Password input-id="password" v-model="password" :feedback="false" toggle-mask autocomplete="current-password" :input-props="{ dir: 'ltr' }" required data-testid="login-password" />
          <Button type="submit" :label="busy === 'credentials' ? t('auth.signingIn') : t('auth.signIn')" :loading="busy === 'credentials'" data-testid="login-submit" />
        </form>
      </template>
    </Card>
  </section>
</template>

<style scoped>
.auth-page { display: grid; place-items: center; gap: var(--raavi-space-4); padding-block: var(--raavi-space-8); }
.auth-card { inline-size: min(100%, 26rem); }
.auth-title { margin: 0; font-size: 1.35rem; }
.redirects { display: grid; gap: var(--raavi-space-2); margin-block: var(--raavi-space-3); }
.auth-form { display: grid; gap: var(--raavi-space-2); margin-block: var(--raavi-space-4); }
.auth-form :deep(.p-password), .auth-form :deep(.p-password-input) { inline-size: 100%; }
.auth-form button[type='submit'] { margin-block-start: var(--raavi-space-3); }
.muted { color: var(--raavi-text-muted); }
.small { font-size: 0.85rem; }
</style>
