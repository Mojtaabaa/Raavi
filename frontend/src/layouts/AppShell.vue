<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
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
import BrandLogo from '@/app/BrandLogo.vue'
import HelpLink from '@/app/HelpLink.vue'
import Breadcrumbs from '@/app/Breadcrumbs.vue'
import ShortcutsDialog from '@/app/ShortcutsDialog.vue'
import NavDropdown, { type NavItem } from './NavDropdown.vue'
import BackToTop from '@/app/BackToTop.vue'
import ThemePicker from '@/app/ThemePicker.vue'
import Popover from 'primevue/popover'
import UserAvatar from '@/app/UserAvatar.vue'

const { t } = useI18n()
const router = useRouter()
const route = useRoute()
const store = useLocaleStore()
const host = useHostStore()
const auth = useAuthStore()

// The header wraps to two lines on medium screens; things that stick below it (editor toolbar, space sidebar) need its real height.
const headerEl = ref<HTMLElement | null>(null)
let headerObserver: ResizeObserver | null = null
function publishHeaderHeight() {
  const h = headerEl.value?.getBoundingClientRect().height
  if (h) document.documentElement.style.setProperty('--raavi-header-offset', `${Math.round(h)}px`)
}
onMounted(() => {
  publishHeaderHeight()
  if (typeof ResizeObserver !== 'undefined' && headerEl.value) {
    headerObserver = new ResizeObserver(publishHeaderHeight)
    headerObserver.observe(headerEl.value)
  }
})
onBeforeUnmount(() => headerObserver?.disconnect())

const themePopover = ref<InstanceType<typeof Popover> | null>(null)
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

/** Items used every day stay in the bar; the rest sit behind "More" (on narrow screens everything is in the folded menu anyway). */
const RARELY = new Set(['calendar', 'approvals'])
const allNav = computed<NavItem[]>(() => {
  const me = auth.me
  if (!me) return []
  if (isSystemHost.value) {
    return me.isSystemAdministrator
      ? [
          { to: { name: 'system-tenants' }, label: t('nav.tenants'), icon: 'pi pi-building' },
          { to: { name: 'system-errors' }, label: t('errorLog.title'), icon: 'pi pi-exclamation-triangle' },
        ]
      : []
  }
  return [
    { to: { name: 'home' }, label: t('nav.home'), icon: 'pi pi-home' },
    { to: { name: 'spaces' }, label: t('nav.spaces'), icon: 'pi pi-th-large' },
    { to: { name: 'tasks' }, label: t('nav.tasks'), icon: 'pi pi-check-square' },
    { to: { name: 'calendar' }, label: t('nav.calendar'), icon: 'pi pi-calendar' },
    { to: { name: 'approvals' }, label: t('nav.approvals'), icon: 'pi pi-verified' },
    { to: { name: 'search' }, label: t('smartSearch.nav'), icon: 'pi pi-sparkles' },
  ]
})

const nav = computed(() => allNav.value.filter((i) => !RARELY.has(i.to.name)))
const moreNav = computed(() => allNav.value.filter((i) => RARELY.has(i.to.name)))

/** Administration pages of a workspace administrator: one menu instead of nine header links (the header overflowed on narrow screens). */
const adminNav = computed<NavItem[]>(() => {
  const me = auth.me
  if (!me || isSystemHost.value || !me.isTenantAdministrator) return []
  return [
    { to: { name: 'admin-members' }, label: t('nav.members'), icon: 'pi pi-users' },
    { to: { name: 'admin-groups' }, label: t('nav.groups'), icon: 'pi pi-sitemap' },
    { to: { name: 'admin-providers' }, label: t('nav.providers'), icon: 'pi pi-key' },
    { to: { name: 'admin-permissions' }, label: t('nav.permissions'), icon: 'pi pi-shield' },
    { to: { name: 'admin-ai' }, label: t('nav.ai'), icon: 'pi pi-sparkles' },
    { to: { name: 'admin-connectors' }, label: t('nav.connectors'), icon: 'pi pi-github' },
    { to: { name: 'admin-webhooks' }, label: t('nav.webhooks'), icon: 'pi pi-link' },
    { to: { name: 'admin-activity' }, label: t('activityLog.title'), icon: 'pi pi-history' },
    { to: { name: 'admin-export' }, label: t('nav.export'), icon: 'pi pi-download' },
    { to: { name: 'admin-migrations' }, label: t('nav.migrations'), icon: 'pi pi-cloud-download' },
  ]
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
const shortcutsOpen = ref(false)
const menuOpen = ref(false) // the navigation folds behind a button on narrow screens
watch(() => route?.fullPath, () => (menuOpen.value = false))
/** True while the person is typing somewhere: single-key shortcuts must not fire then. */
const isTyping = (t: EventTarget | null) => t instanceof HTMLElement && (t.isContentEditable || ['INPUT', 'TEXTAREA', 'SELECT'].includes(t.tagName))
const canSearch = computed(() => !!auth.me && !isSystemHost.value)
function onGlobalKey(e: KeyboardEvent) {
  if (!e.ctrlKey && !e.metaKey && !e.altKey && !isTyping(e.target)) {
    if (e.key === '?') {
      e.preventDefault()
      shortcutsOpen.value = true
      return
    }
    if (e.key === '/' && canSearch.value) {
      e.preventDefault()
      void router.push({ name: 'search' })
      return
    }
  }
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
  <header ref="headerEl" class="shell-header">
    <div class="brand">
      <BrandLogo icon :size="32" :src="host.logoUrl" class="brand-mark" aria-hidden="true" />
      <span class="brand-name" data-testid="brand-name">{{ brandName }}</span>
      <Tag v-if="isSystemHost" severity="secondary" :value="t('host.systemBadge')" data-testid="system-badge" />
    </div>
    <button v-if="nav.length" type="button" class="menu-toggle" :aria-expanded="menuOpen" aria-controls="main-nav" :aria-label="t('shell.menu')" data-testid="menu-toggle" @click="menuOpen = !menuOpen">
      <i :class="menuOpen ? 'pi pi-times' : 'pi pi-bars'" aria-hidden="true" /> <span>{{ t('shell.menu') }}</span>
    </button>
    <nav v-if="nav.length" id="main-nav" :class="['nav', { open: menuOpen }]" :aria-label="t('nav.main')">
      <RouterLink v-for="item in nav" :key="item.label" :to="item.to" class="nav-link" active-class="active">
        <i :class="item.icon" aria-hidden="true" /> <span>{{ item.label }}</span>
      </RouterLink>
      <NavDropdown v-if="moreNav.length" :items="moreNav" :label="t('nav.more')" icon="pi pi-ellipsis-h" toggle-testid="more-menu" list-testid="more-list" />
      <NavDropdown v-if="adminNav.length" :items="adminNav" :label="t('nav.administration')" icon="pi pi-cog" toggle-testid="admin-menu" list-testid="admin-list" />
    </nav>
    <div class="end">
      <SelectButton v-model="current" :options="options" option-label="label" option-value="code" :allow-empty="false" :aria-label="t('shell.language')" data-testid="locale-switch" />
      <HelpLink compact />
      <Button text size="small" icon="pi pi-palette" :aria-label="t('theme.title')" :title="t('theme.title')" data-testid="theme-button" @click="themePopover?.toggle($event)" />
      <Popover ref="themePopover"><div class="theme-pop"><ThemePicker /></div></Popover>
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
        <RouterLink v-if="!isSystemHost" :to="{ name: 'profile' }" class="user" data-testid="user-name"><UserAvatar :user-id="auth.me.id" :name="auth.me.displayName" :size="28" /> <span class="user-name">{{ auth.me.displayName }}</span></RouterLink>
        <span v-else class="user" data-testid="user-name">{{ auth.me.displayName }}</span>
        <Button text size="small" icon="pi pi-sign-out" :label="t('nav.signOut')" data-testid="sign-out" @click="signOut" />
      </template>
    </div>
  </header>
  <CommandPalette v-if="canSearch" v-model:visible="paletteOpen" />
  <ShortcutsDialog v-model:visible="shortcutsOpen" />
  <BackToTop />
  <main id="main" class="shell-main" tabindex="-1">
    <Breadcrumbs v-if="auth.me" />
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
.brand-mark { flex: none; }
.nav { display: flex; gap: var(--raavi-space-1); flex-wrap: wrap; align-items: center; }
.menu-toggle { display: none; align-items: center; gap: var(--raavi-space-1); font: inherit; border: 1px solid var(--raavi-border); background: transparent; color: var(--raavi-text); padding: 0.3rem 0.7rem; border-radius: var(--raavi-radius-sm); cursor: pointer; }
/* Narrow screens: the navigation sits behind a "Menu" button as a column; the administration list opens inline instead of floating. */
@media (max-width: 960px) {
  .menu-toggle { display: inline-flex; }
  .nav { display: none; order: 9; flex-basis: 100%; flex-direction: column; align-items: stretch; padding-block: var(--raavi-space-2); border-block-start: 1px solid var(--raavi-border); }
  .nav.open { display: flex; }
  .nav .nav-link { padding: 0.55rem var(--raavi-space-3); }
}
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
.end { display: flex; align-items: center; flex-wrap: wrap; gap: var(--raavi-space-2) var(--raavi-space-3); margin-inline-start: auto; min-inline-size: 0; }
.user { color: var(--raavi-text-muted); font-size: 0.9rem; display: inline-flex; align-items: center; gap: var(--raavi-space-2); text-decoration: none; }
.theme-pop { inline-size: min(22rem, 86vw); }
.shell-main { padding: var(--raavi-space-6) var(--raavi-space-4); outline: none; }
@media (max-width: 640px) {
  .user-name { display: none; }
  /* Phones: icons only for the quick-find and sign-out buttons; the rest of the header wraps. */
  .end :deep(.p-button-label) { display: none; }
  .shell-header { gap: var(--raavi-space-2); padding-inline: var(--raavi-space-2); }
  .shell-main { padding-inline: var(--raavi-space-2); }
}
</style>
