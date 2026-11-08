import { createRouter, createWebHistory, type RouteLocationNormalized, type RouteRecordRaw } from 'vue-router'
import { useHostStore } from '@/stores/host'
import { useAuthStore } from '@/stores/auth'
import { useLocaleStore } from '@/stores/locale'
import { applyBranding } from '@/core/theme/branding'

declare module 'vue-router' {
  interface RouteMeta {
    /** Route exists only on this host kind (mirrors the backend's host-kind enforcement). */
    host?: 'tenant' | 'system'
    requires?: 'user' | 'tenantAdmin' | 'systemAdmin'
    /** Signed-in users are sent home. */
    guestOnly?: boolean
    /** Rendered without the application shell. */
    bare?: boolean
  }
}

export const routes: RouteRecordRaw[] = [
  { path: '/', name: 'home', component: () => import('@/modules/wiki/views/HomeView.vue'), meta: { host: 'tenant', requires: 'user' } },
  { path: '/databases/:id', name: 'database', component: () => import('@/modules/databases/DatabaseView.vue'), meta: { host: 'tenant', requires: 'user' } },
  { path: '/calendar', name: 'calendar', component: () => import('@/modules/calendar/CalendarView.vue'), meta: { host: 'tenant', requires: 'user' } },
  { path: '/tasks', name: 'tasks', component: () => import('@/modules/tasks/TasksView.vue'), meta: { host: 'tenant', requires: 'user' } },
  { path: '/spaces', name: 'spaces', component: () => import('@/modules/wiki/views/SpacesView.vue'), meta: { host: 'tenant', requires: 'user' } },
  {
    path: '/spaces/:key',
    component: () => import('@/modules/wiki/views/SpaceLayout.vue'),
    meta: { host: 'tenant', requires: 'user' },
    children: [
      { path: '', name: 'space', component: () => import('@/modules/wiki/views/SpaceOverview.vue') },
      { path: 'databases', name: 'space-databases', component: () => import('@/modules/databases/DatabasesView.vue') },
      { path: 'calendar', name: 'space-calendar', component: () => import('@/modules/calendar/CalendarView.vue') },
      { path: 'tasks', name: 'space-tasks', component: () => import('@/modules/tasks/TasksView.vue') },
      { path: 'settings', name: 'space-settings', component: () => import('@/modules/wiki/views/SpaceSettingsView.vue') },
      { path: 'create', name: 'page-create', component: () => import('@/modules/wiki/views/PageEditView.vue') },
      { path: 'pages/:id/edit', name: 'page-edit', component: () => import('@/modules/wiki/views/PageEditView.vue') },
      { path: 'pages/:id/history', name: 'page-history', component: () => import('@/modules/wiki/views/HistoryView.vue') },
      { path: 'pages/:id/:slug?', name: 'page', component: () => import('@/modules/wiki/views/PageView.vue') },
    ],
  },
  { path: '/labels/:name', name: 'label', component: () => import('@/modules/wiki/views/LabelView.vue'), meta: { host: 'tenant', requires: 'user' } },
  { path: '/admin/permissions', name: 'admin-permissions', component: () => import('@/modules/wiki/views/GlobalPermissionsView.vue'), meta: { host: 'tenant', requires: 'tenantAdmin' } },
  { path: '/login', name: 'login', component: () => import('@/modules/identity/views/LoginView.vue'), meta: { host: 'tenant', guestOnly: true } },
  { path: '/invitations/accept', name: 'accept-invitation', component: () => import('@/modules/identity/views/AcceptInvitationView.vue'), meta: { host: 'tenant' } },
  { path: '/admin/members', name: 'admin-members', component: () => import('@/modules/identity/views/MembersView.vue'), meta: { host: 'tenant', requires: 'tenantAdmin' } },
  { path: '/me', name: 'profile', component: () => import('@/modules/identity/views/ProfileView.vue'), meta: { host: 'tenant', requires: 'user' } },
  { path: '/admin/providers', name: 'admin-providers', component: () => import('@/modules/identity/views/ProvidersView.vue'), meta: { host: 'tenant', requires: 'tenantAdmin' } },
  { path: '/admin/migrations/confluence', name: 'admin-migrations', component: () => import('@/modules/administration/views/ConfluenceMigrationView.vue'), meta: { host: 'tenant', requires: 'tenantAdmin' } },
  { path: '/admin/groups', name: 'admin-groups', component: () => import('@/modules/identity/views/GroupsView.vue'), meta: { host: 'tenant', requires: 'tenantAdmin' } },
  { path: '/system', name: 'system-home', component: () => import('@/modules/tenants/views/DiscoveryView.vue'), meta: { host: 'system' } },
  { path: '/system/login', name: 'system-login', component: () => import('@/modules/identity/views/SystemLoginView.vue'), meta: { host: 'system', guestOnly: true } },
  { path: '/system/setup', name: 'system-setup', component: () => import('@/modules/identity/views/SystemSetupView.vue'), meta: { host: 'system' } },
  { path: '/system/tenants', name: 'system-tenants', component: () => import('@/modules/tenants/views/SystemTenantsView.vue'), meta: { host: 'system', requires: 'systemAdmin' } },
  { path: '/not-found', name: 'not-found', component: () => import('@/modules/tenants/views/WorkspaceNotFound.vue'), meta: { bare: true } },
  { path: '/:pathMatch(.*)*', redirect: '/' },
]

let bootstrapped = false

/** Loads host context and session once, then applies tenant branding and locale precedence (user → tenant → system). */
async function bootstrap(): Promise<void> {
  if (bootstrapped) return
  const host = useHostStore()
  const auth = useAuthStore()
  const locale = useLocaleStore()
  const ctx = await host.load()
  if (ctx.kind === 'tenant') {
    applyBranding(ctx.tenant.branding)
    locale.applyTenantDefault(ctx.tenant.defaultLocale)
  }
  if (ctx.kind === 'tenant' || ctx.kind === 'system') {
    const me = await auth.load()
    if (me?.preferredLocale) locale.applyUserPreference(me.preferredLocale)
  }
  bootstrapped = true
}

/** Exported for tests. */
export async function guard(to: RouteLocationNormalized) {
  await bootstrap()
  const host = useHostStore()
  const auth = useAuthStore()
  const kind = host.context?.kind

  if (kind === 'not-found') return to.name === 'not-found' ? true : { name: 'not-found' }
  if (to.name === 'not-found') return true

  const homeFor = kind === 'system' ? { name: 'system-home' } : { name: 'home' }
  if (to.meta.host && to.meta.host !== kind) return homeFor

  const me = auth.me
  if (to.meta.guestOnly && me) return kind === 'system' ? (me.isSystemAdministrator ? { name: 'system-tenants' } : homeFor) : homeFor

  const loginFor = kind === 'system' ? 'system-login' : 'login'
  if (to.meta.requires && !me) return { name: loginFor, query: { redirect: to.fullPath } }
  if (to.meta.requires === 'tenantAdmin' && !me?.isTenantAdministrator) return homeFor
  if (to.meta.requires === 'systemAdmin' && !me?.isSystemAdministrator) return { name: 'system-login' }
  return true
}

export function createRaaviRouter(history = createWebHistory()) {
  const router = createRouter({ history, routes })
  router.beforeEach(guard)
  return router
}

/** For tests: forget bootstrap state. */
export function resetRouterBootstrap(): void {
  bootstrapped = false
}

/** Only same-app relative paths are accepted as post-login redirects (open-redirect protection). */
export function safeRedirect(value: unknown): string | null {
  return typeof value === 'string' && value.startsWith('/') && !value.startsWith('//') && !value.includes('\\') ? value : null
}
