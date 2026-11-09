<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import Card from 'primevue/card'
import Button from 'primevue/button'
import Checkbox from 'primevue/checkbox'
import { notificationsApi } from '@/core/api/comments'
import Message from 'primevue/message'
import { identityApi, safeExternalUrl, type LinkedIdentity, type ProviderSummary } from '@/core/api/identity'
import { errorMessage } from '@/core/api/errors'
import { ApiError } from '@/core/api/http'
import { formatDate } from '@/core/i18n/locales'
import { useAuthStore } from '@/stores/auth'
import { useAvatarStore } from '@/stores/avatars'
import { useHostStore } from '@/stores/host'
import { avatarsApi } from '@/core/api/avatars'
import { isAcceptedPicture, MAX_PICK_BYTES, squareAvatarBlob } from '@/core/runtime/imageResize'
import UserAvatar from '@/app/UserAvatar.vue'
import { useLocaleStore } from '@/stores/locale'

const { t, te } = useI18n()
const route = useRoute()
const toast = useToast()
const auth = useAuthStore()
const localeStore = useLocaleStore()

const avatars = useAvatarStore()
const host = useHostStore()
const picture = ref<HTMLInputElement | null>(null)
const savingPicture = ref(false)
const hasPicture = computed(() => !!auth.me && !!avatars.versions[auth.me.id])
const canHavePicture = computed(() => host.context?.kind === 'tenant' && !!auth.me)

async function pickPicture(event: Event) {
  const input = event.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || !auth.me) return
  if (!isAcceptedPicture(file) || file.size > MAX_PICK_BYTES) {
    toast.add({ severity: 'error', summary: t('profile.picture.invalid'), life: 5000 })
    return
  }
  savingPicture.value = true
  try {
    const { version } = await avatarsApi.upload(await squareAvatarBlob(file), 'avatar')
    avatars.set(auth.me.id, version)
    toast.add({ severity: 'success', summary: t('profile.picture.saved'), life: 3000 })
  } catch (e) {
    toast.add({ severity: 'error', summary: e instanceof ApiError ? errorMessage(e, t, te) : t('profile.picture.unreadable'), life: 5000 })
  } finally {
    savingPicture.value = false
  }
}

async function removePicture() {
  if (!auth.me) return
  try {
    await avatarsApi.remove()
    avatars.set(auth.me.id, null)
    toast.add({ severity: 'success', summary: t('profile.picture.removed'), life: 3000 })
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
}

const identities = ref<LinkedIdentity[]>([])
const providers = ref<ProviderSummary[]>([])
const notice = ref<{ severity: 'success' | 'error'; text: string } | null>(null)
const emailEnabled = ref(true)
const emailAvailable = ref(false)
const prefsLoaded = ref(false)

async function loadPreferences() {
  try {
    const p = await notificationsApi.preferences()
    emailEnabled.value = p.emailEnabled
    emailAvailable.value = p.emailAvailable
    prefsLoaded.value = true
  } catch {
    prefsLoaded.value = false
  }
}

async function setEmail(value: boolean) {
  try {
    emailEnabled.value = (await notificationsApi.setPreferences(value)).emailEnabled
    toast.add({ severity: 'success', summary: t('profile.notifications.saved'), life: 3000 })
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
}

const linkable = computed(() => providers.value.filter((p) => p.flow === 'redirect' && !identities.value.some((i) => i.providerId === p.id)))
const label = (n: { fa: string; en: string }) => (localeStore.locale === 'fa' ? n.fa || n.en : n.en || n.fa)

async function load() {
  try {
    ;[identities.value, providers.value] = await Promise.all([identityApi.identities(), identityApi.providers()])
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
}

async function link(p: ProviderSummary) {
  try {
    const target = safeExternalUrl((await identityApi.beginLink(p.id)).redirectUrl)
    if (target) window.location.assign(target)
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
}

async function unlink(i: LinkedIdentity) {
  try {
    await identityApi.unlink(i.id)
    await load()
  } catch (e) {
    toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 5000 })
  }
}

onMounted(async () => {
  if (typeof route.query.linked === 'string') notice.value = { severity: 'success', text: t('profile.linked') }
  const code = route.query.error
  if (typeof code === 'string' && /^[a-z_.]{3,64}$/.test(code)) notice.value = { severity: 'error', text: errorMessage(new ApiError(400, code, code), t, te) }
  await Promise.all([load(), loadPreferences()])
})
</script>

<template>
  <section class="page">
    <h1>{{ t('profile.title') }}</h1>
    <Card>
      <template #content>
        <div v-if="canHavePicture" class="picture" data-testid="picture">
          <UserAvatar :user-id="auth.me?.id" :name="auth.me?.displayName" :size="96" />
          <div class="picture-actions">
            <input ref="picture" type="file" accept="image/png,image/jpeg,image/webp" class="visually-hidden" data-testid="picture-input" :aria-label="t('profile.picture.change')" @change="pickPicture" />
            <Button size="small" outlined icon="pi pi-camera" :label="t('profile.picture.change')" :loading="savingPicture" data-testid="picture-change" @click="picture?.click()" />
            <Button v-if="hasPicture" size="small" text severity="danger" icon="pi pi-trash" :label="t('profile.picture.remove')" data-testid="picture-remove" @click="removePicture" />
            <small class="muted">{{ t('profile.picture.hint') }}</small>
          </div>
        </div>
        <dl class="facts">
          <dt>{{ t('common.displayName') }}</dt>
          <dd>{{ auth.me?.displayName }}</dd>
          <dt>{{ t('common.email') }}</dt>
          <dd dir="ltr">{{ auth.me?.email ?? '—' }}</dd>
        </dl>
      </template>
    </Card>

    <h2>{{ t('profile.linkedAccounts') }}</h2>
    <Message v-if="notice" :severity="notice.severity" :closable="false" data-testid="profile-notice">{{ notice.text }}</Message>
    <ul class="list" data-testid="linked-identities">
      <li v-for="i in identities" :key="i.id">
        <div>
          <strong>{{ label(i.providerName) }}</strong>
          <span class="muted" dir="ltr"> {{ i.externalUsername ?? i.externalEmail ?? '' }}</span>
          <div class="muted small">{{ t('profile.linkedOn', { date: formatDate(i.createdAt, localeStore.locale) }) }}</div>
        </div>
        <Button text severity="danger" size="small" :label="t('profile.unlink')" @click="unlink(i)" />
      </li>
      <li v-if="identities.length === 0" class="muted">{{ t('profile.noLinked') }}</li>
    </ul>
    <div v-if="linkable.length" class="actions">
      <Button v-for="p in linkable" :key="p.id" outlined size="small" icon="pi pi-link" :label="t('profile.link', { name: label(p.displayName) })" @click="link(p)" />
    </div>

    <template v-if="prefsLoaded">
      <h2>{{ t('profile.notifications.title') }}</h2>
      <div class="pref">
        <Checkbox :model-value="emailEnabled" binary input-id="pref-email" :disabled="!emailAvailable" data-testid="pref-email" @update:model-value="setEmail" />
        <label for="pref-email">{{ t('profile.notifications.email') }}</label>
      </div>
      <p v-if="!emailAvailable" class="muted small">{{ t('profile.notifications.emailUnavailable') }}</p>
      <p v-else class="muted small">{{ t('profile.notifications.emailHelp') }}</p>
    </template>
  </section>
</template>

<style scoped>
.pref { display: flex; gap: var(--raavi-space-2); align-items: center; }
.page { max-inline-size: 48rem; margin-inline: auto; display: grid; gap: var(--raavi-space-4); }
h1 { margin: 0; font-size: 1.4rem; }
h2 { margin: var(--raavi-space-2) 0 0; font-size: 1.1rem; }
.picture { display: flex; gap: var(--raavi-space-4); align-items: center; margin-block-end: var(--raavi-space-4); }
.picture-actions { display: flex; flex-wrap: wrap; gap: var(--raavi-space-2); align-items: center; }
.visually-hidden { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; }
.facts { display: grid; grid-template-columns: max-content 1fr; gap: var(--raavi-space-2) var(--raavi-space-4); margin: 0; }
.facts dt { color: var(--raavi-text-muted); }
.facts dd { margin: 0; }
.list { list-style: none; padding: 0; margin: 0; display: grid; gap: var(--raavi-space-2); }
.list li { display: flex; align-items: center; justify-content: space-between; gap: var(--raavi-space-3); padding: var(--raavi-space-3); background: var(--raavi-surface); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-md); }
.actions { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.muted { color: var(--raavi-text-muted); }
.small { font-size: 0.85rem; }
</style>
