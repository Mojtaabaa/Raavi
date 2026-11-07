<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import Card from 'primevue/card'
import InputText from 'primevue/inputtext'
import Password from 'primevue/password'
import Button from 'primevue/button'
import Message from 'primevue/message'
import SelectButton from 'primevue/selectbutton'
import { identityApi, takeFragmentToken } from '@/core/api/identity'
import { errorMessage } from '@/core/api/errors'
import { useAuthStore } from '@/stores/auth'

const { t, te } = useI18n()
const router = useRouter()
const auth = useAuthStore()

const token = ref<string | null>(null)
const info = ref<{ email: string; tenantAdministrator: boolean } | null>(null)
const invalid = ref(false)
const mode = ref<'new' | 'existing'>('new')
const displayName = ref('')
const password = ref('')
const busy = ref(false)
const error = ref<string | null>(null)

const modes = [
  { value: 'new', label: () => t('invitation.newAccount') },
  { value: 'existing', label: () => t('invitation.existingAccount') },
]

onMounted(async () => {
  // Token travels in the URL fragment and is removed from the address bar immediately.
  token.value = takeFragmentToken()
  if (!token.value) {
    invalid.value = true
    return
  }
  try {
    info.value = await identityApi.inspectInvitation(token.value)
  } catch {
    invalid.value = true
  }
})

async function submit() {
  if (!token.value || busy.value) return
  busy.value = true
  error.value = null
  try {
    await auth.acceptInvitation(token.value, mode.value, password.value, mode.value === 'new' ? displayName.value : undefined)
    await router.replace('/')
  } catch (e) {
    error.value = errorMessage(e, t, te)
    password.value = ''
  } finally {
    busy.value = false
  }
}
</script>

<template>
  <section class="auth-page">
    <Card class="auth-card">
      <template #title><h1 class="auth-title">{{ t('invitation.title') }}</h1></template>
      <template #content>
        <Message v-if="invalid" severity="warn" :closable="false" data-testid="invitation-invalid">{{ t('invitation.invalid') }}</Message>
        <p v-else-if="!info">{{ t('common.loading') }}</p>
        <form v-else class="auth-form" novalidate @submit.prevent="submit">
          <p>
            {{ t('invitation.invitedAs', { email: info.email }) }}
            <strong v-if="info.tenantAdministrator">{{ t('invitation.asAdmin') }}</strong>
          </p>
          <SelectButton v-model="mode" :options="modes" option-value="value" :allow-empty="false" data-testid="invitation-mode">
            <template #option="{ option }">{{ option.label() }}</template>
          </SelectButton>
          <template v-if="mode === 'new'">
            <label for="name">{{ t('common.displayName') }}</label>
            <InputText id="name" v-model="displayName" autocomplete="name" required />
          </template>
          <label for="pw">{{ t('common.password') }}</label>
          <Password input-id="pw" v-model="password" :feedback="false" toggle-mask :autocomplete="mode === 'new' ? 'new-password' : 'current-password'" :input-props="{ dir: 'ltr' }" :placeholder="mode === 'new' ? t('invitation.passwordHint') : ''" />
          <Message v-if="error" severity="error" :closable="false" data-testid="invitation-error">{{ error }}</Message>
          <Button type="submit" :label="t('invitation.accept')" :loading="busy" />
        </form>
      </template>
    </Card>
  </section>
</template>

<style scoped>
.auth-page { display: grid; place-items: center; padding-block: var(--raavi-space-8); }
.auth-card { inline-size: min(100%, 28rem); }
.auth-title { margin: 0; font-size: 1.35rem; }
.auth-form { display: grid; gap: var(--raavi-space-2); }
.auth-form :deep(.p-password), .auth-form :deep(.p-password-input) { inline-size: 100%; }
</style>
