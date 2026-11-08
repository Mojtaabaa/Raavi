<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import SelectButton from 'primevue/selectbutton'
import Button from 'primevue/button'
import Tag from 'primevue/tag'
import Select from 'primevue/select'
import type { Workspace } from '@/core/api/identity'
import { LOCALES, SUPPORTED_LOCALES, type SupportedLocale } from '@/core/i18n/locales'
import { identityApi } from '@/core/api/identity'
import { useLocaleStore } from '@/stores/locale'
import { useHostStore } from '@/stores/host'
import { useAuthStore } from '@/stores/auth'
import CommandPalette from '@/modules/wiki/components/CommandPalette.vue'
import NotificationBell from '@/modules/notifications/NotificationBell.vue'

const { t } = useI18n()
const router = useRouter()
const store = useLocaleStore()
const host = useHostStore()
const auth = useAuthStore()

const options = SUPPORTED_LOCALES.map((code) => ({ code, label: LOCALES[code].nativeName }))

const current = computed<SupportedLocale>({
  get: () => store.locale,
  set: (value) => {
    if (!value) return
    store.setLocale(value)
    // Signed-in users keep the choice in their profile (user-level preference, ADR-022 §3).
    if (auth.me) identityApi.setLocale(value).catch(() => undefined)
  },
})

/** Tenant display name in the active language; the product name on the system host. */
const brandName = computed(() => {
  const tenant = host.tenant
  if (!tenant) return t('app.name')
  return store.locale === 'fa' ? tenant.displayName.fa : tenant.displayName.en
})
const isSystemHost = computed(() => host.context?.kind === 'system')

const nav = computed(() => {
  const me = auth.me
  if (!me) return []
  if (isSystemHost.value) return me.isSystemAdministrator ? [{ to: { name: 'system-tenants' }, label: t('nav.tenants'), icon: 'pi pi-building' }] : []
  const items = [
    { to: { name: 'home' }, label: t('nav.home'), icon: 'pi pi-home' },
    { to: { name: 'spaces' }, label: t('nav.spaces'), icon: 'pi pi-th-large' },
    { to: { name: 'tasks' }, label: t('nav.tasks'), icon: 'pi pi-check-square' },
  ]
  if (me.isTenantAdministrator) {
    items.push({ to: { name: 'admin-members' }, label: t('nav.members'), icon: 'pi pi-users' })
    items.push({ to: { name: 'admin-groups' }, label: t('nav.groups'), icon: 'pi pi-sitemap' })
    items.push({ to: { name: 'admin-providers' }, label: t('nav.providers'), icon: 'pi pi-key' })
    items.push({ to: { name: 'admin-permissions' }, label: t('nav.permissions'), icon: 'pi pi-shield' })
    items.push({ to: { name: 'admin-migrations' }, label: t('nav.migrations'), icon: 'pi pi-download' })
  }
  return items
})

/** Workspace switcher: the signed-in user's own active workspaces (each workspace host has its own sign-in). */
const workspaces = ref<Workspace[]>([])
async function loadWorkspaces() {
  workspaces.value = auth.me && !isSystemHost.value ? await identityApi.workspaces().catch(() => []) : []
}
onMounted(loadWorkspaces)
watch(() => auth.me?.id, loadWorkspaces)
const workspaceName = (w: Workspace) => (store.locale === 'fa' ? w.displayName.fa : w.displayName.en)
function switchWorkspace(w: Workspace | null) {
  if (w && !w.isCurrent) window.location.assign(w.url)
}

/** Quick find (Ctrl/⌘+K) on tenant hosts for signed-in members. */
const paletteOpen = ref(false)
const canSearch = computed(() => !!auth.me && !isSystemHost.value)
function onGlobalKey(e: KeyboardEvent) {
  if (canSearch.value && (e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 'k') {
    e.preventDefault()
    paletteOpen.value = true
  }
}
onMounted(() => window.addEventListener('keydown', onGlobalKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onGlobalKey))

async function signOut() {
  await auth.logout()
  await router.replace(isSystemHost.value ? { name: 'system-home' } : { name: 'login' })
}
</script>

<template>
  <a class="raavi-skip-link" href="#main">{{ t('shell.skipToContent') }}</a>
  <header class="shell-header">
    <div class="brand">
      <span class="brand-mark" aria-hidden="true">ر</span>
      <span class="brand-name" data-testid="brand-name">{{ brandName }}</span>
      <Tag v-if="isSystemHost" severity="secondary" :value="t('host.systemBadge')" data-testid="system-badge" />
    </div>
    <nav v-if="nav.length" class="nav" :aria-label="t('nav.administration')">
      <RouterLink v-for="item in nav" :key="item.label" :to="item.to" class="nav-link" active-class="active">
        <i :class="item.icon" aria-hidden="true" /> <span>{{ item.label }}</span>
      </RouterLink>
    </nav>
    <div class="end">
      <SelectButton v-model="current" :options="options" option-label="label" option-value="code" :allow-empty="false" :aria-label="t('shell.language')" data-testid="locale-switch" />
      <Button v-if="canSearch" text size="small" icon="pi pi-search" :label="t('wiki.quickFind.button')" :aria-keyshortcuts="'Control+K'" data-testid="quick-find" @click="paletteOpen = true" />
      <template v-if="auth.me">
        <Select
          v-if="workspaces.length > 1"
          :model-value="workspaces.find((w) => w.isCurrent) ?? null"
          :options="workspaces"
          :option-label="workspaceName"
          :aria-label="t('nav.switchWorkspace')"
          size="small"
          data-testid="workspace-switcher"
          @update:model-value="switchWorkspace"
        />
        <NotificationBell v-if="!isSystemHost" />
        <RouterLink v-if="!isSystemHost" :to="{ name: 'profile' }" class="user" data-testid="user-name">{{ auth.me.displayName }}</RouterLink>
        <span v-else class="user" data-testid="user-name">{{ auth.me.displayName }}</span>
        <Button text size="small" icon="pi pi-sign-out" :label="t('nav.signOut')" data-testid="sign-out" @click="signOut" />
      </template>
    </div>
  </header>
  <CommandPalette v-if="canSearch" v-model:visible="paletteOpen" />
  <main id="main" class="shell-main" tabindex="-1">
    <slot />
  </main>
</template>

<style scoped>
.shell-header {
  position: sticky;
  inset-block-start: 0;
  z-index: 10;
  display: flex;
  align-items: center;
  gap: var(--raavi-space-4);
  min-block-size: var(--raavi-header-height);
  padding-inline: var(--raavi-space-4);
  background: var(--raavi-surface);
  border-block-end: 1px solid var(--raavi-border);
  flex-wrap: wrap;
}
.brand { display: flex; align-items: center; gap: var(--raavi-space-2); font-weight: 700; }
.brand-mark {
  display: grid;
  place-items: center;
  inline-size: 32px;
  block-size: 32px;
  border-radius: var(--raavi-radius-sm);
  background: var(--p-primary-color);
  color: var(--p-primary-contrast-color);
  font-family: var(--raavi-font-fa);
}
.nav { display: flex; gap: var(--raavi-space-1); }
.nav-link {
  display: inline-flex;
  align-items: center;
  gap: var(--raavi-space-1);
  padding: var(--raavi-space-1) var(--raavi-space-3);
  border-radius: var(--raavi-radius-sm);
  color: var(--raavi-text-muted);
  text-decoration: none;
}
.nav-link:hover, .nav-link.active { background: var(--p-primary-50, rgba(0, 0, 0, 0.04)); color: var(--p-primary-color); }
.end { display: flex; align-items: center; gap: var(--raavi-space-3); margin-inline-start: auto; }
.user { color: var(--raavi-text-muted); font-size: 0.9rem; }
.shell-main { padding: var(--raavi-space-6) var(--raavi-space-4); outline: none; }
@media (max-width: 640px) {
  .user { display: none; }
}
</style>
