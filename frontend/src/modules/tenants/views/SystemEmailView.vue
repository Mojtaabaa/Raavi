<script setup lang="ts">
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import Password from 'primevue/password'
import Select from 'primevue/select'
import Message from 'primevue/message'
import { api, ApiError } from '@/core/api/http'
import { errorMessage } from '@/core/api/errors'

/** E-mail delivery of the installation (ADR-053): saved in the application, password stored protected and never shown again. */
interface Dto { mode: string; fromAddress: string; fromName: string; host: string; port: number; security: string; userName: string | null; hasPassword: boolean; timeoutSeconds: number; source: string }
const { t, te } = useI18n()
const toast = useToast()
const form = ref({ mode: 'Disabled', fromAddress: '', fromName: 'Raavi', host: '', port: 587, security: 'StartTls', userName: '', password: '', timeoutSeconds: 30 })
const hasPassword = ref(false)
const source = ref('configuration')
const testTo = ref('')
const saving = ref(false)
const testing = ref(false)
const detail = ref('')
const modes = ['Disabled', 'Smtp', 'Pickup'].map((v) => ({ value: v, label: t(`email.modes.${v}`) }))
const securities = ['StartTls', 'SslOnConnect', 'None'].map((v) => ({ value: v, label: t(`email.security.${v}`) }))

function fill(d: Dto) {
  form.value = { mode: d.mode, fromAddress: d.fromAddress, fromName: d.fromName, host: d.host, port: d.port, security: d.security, userName: d.userName ?? '', password: '', timeoutSeconds: d.timeoutSeconds }
  hasPassword.value = d.hasPassword
  source.value = d.source
}
onMounted(async () => {
  try {
    fill(await api<Dto>('GET', '/system/email'))
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
})

async function save(clearPassword = false) {
  saving.value = true
  try {
    fill(await api<Dto>('PUT', '/system/email', { ...form.value, password: clearPassword ? '-' : form.value.password }))
    toast.add({ severity: 'success', summary: t('email.saved'), life: 3000 })
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
  } finally {
    saving.value = false
  }
}

async function test() {
  testing.value = true
  detail.value = ''
  try {
    await api('POST', '/system/email/test', { to: testTo.value })
    toast.add({ severity: 'success', summary: t('email.testSent'), life: 4000 })
  } catch (e) {
    detail.value = e instanceof ApiError && e.code === 'email.send_failed' ? e.message : ''
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
  } finally {
    testing.value = false
  }
}
</script>

<template>
  <section class="page" data-testid="email-settings">
    <h1>{{ t('email.title') }}</h1>
    <p class="muted">{{ t('email.intro') }}</p>
    <Message v-if="source === 'configuration'" severity="info" :closable="false">{{ t('email.fromConfig') }}</Message>
    <form class="form" @submit.prevent="save(false)">
      <label for="e-mode">{{ t('email.mode') }}</label>
      <Select v-model="form.mode" input-id="e-mode" :options="modes" option-label="label" option-value="value" data-testid="email-mode" />
      <template v-if="form.mode !== 'Disabled'">
        <label for="e-from">{{ t('email.from') }}</label>
        <InputText id="e-from" v-model="form.fromAddress" type="email" dir="ltr" placeholder="raavi@company.com" data-testid="email-from" />
        <label for="e-name">{{ t('email.fromName') }}</label>
        <InputText id="e-name" v-model="form.fromName" />
      </template>
      <template v-if="form.mode === 'Smtp'">
        <label for="e-host">{{ t('email.host') }}</label>
        <InputText id="e-host" v-model="form.host" dir="ltr" data-testid="email-host" />
        <label for="e-port">{{ t('email.port') }}</label>
        <input id="e-port" v-model.number="form.port" type="number" dir="ltr" class="p-inputtext" />
        <label for="e-sec">{{ t('email.securityLabel') }}</label>
        <Select v-model="form.security" input-id="e-sec" :options="securities" option-label="label" option-value="value" />
        <label for="e-user">{{ t('email.user') }}</label>
        <InputText id="e-user" v-model="form.userName" dir="ltr" autocomplete="off" />
        <label for="e-pass">{{ t('email.password') }}</label>
        <Password v-model="form.password" input-id="e-pass" :feedback="false" toggle-mask autocomplete="new-password" :placeholder="hasPassword ? t('email.passwordKept') : ''" data-testid="email-password" />
        <Button v-if="hasPassword" type="button" text size="small" severity="danger" :label="t('email.removePassword')" @click="save(true)" />
      </template>
      <Button type="submit" :label="t('common.save')" :loading="saving" data-testid="email-save" />
    </form>

    <div v-if="form.mode !== 'Disabled'" class="form test">
      <h2>{{ t('email.testTitle') }}</h2>
      <label for="e-test">{{ t('email.testTo') }}</label>
      <InputText id="e-test" v-model="testTo" type="email" dir="ltr" data-testid="email-test-to" />
      <Button outlined icon="pi pi-send" :label="t('email.testSend')" :loading="testing" :disabled="!testTo" data-testid="email-test" @click="test" />
      <p v-if="detail" class="muted" dir="ltr">{{ detail }}</p>
      <p class="muted">{{ t('email.saveFirst') }}</p>
    </div>
  </section>
</template>

<style scoped>
.page { max-inline-size: 40rem; margin-inline: auto; display: grid; gap: var(--raavi-space-3); }
h1 { margin: 0; font-size: 1.4rem; }
h2 { margin: 0; font-size: 1.05rem; }
.form { display: grid; gap: var(--raavi-space-2); }
label { font-weight: 600; font-size: 0.9rem; }
.muted { color: var(--raavi-text-muted); margin: 0; }
.test { border-block-start: 1px solid var(--raavi-border); padding-block-start: var(--raavi-space-3); }
</style>
