import { defineStore } from 'pinia'
import { computed, ref } from 'vue'
import { ApiError, resetAntiforgery } from '@/core/api/http'
import { identityApi, type Me } from '@/core/api/identity'

/** Signed-in user of the current host. The session itself is an HttpOnly cookie the SPA never reads. */
export const useAuthStore = defineStore('auth', () => {
  const me = ref<Me | null>(null)
  const loaded = ref(false)
  const isAuthenticated = computed(() => me.value !== null)

  async function load(): Promise<Me | null> {
    try {
      me.value = await identityApi.me()
    } catch (e) {
      if (!(e instanceof ApiError) || (e.status !== 401 && e.status !== 403)) throw e
      me.value = null
    } finally {
      loaded.value = true
    }
    return me.value
  }

  /** Identity changed: antiforgery tokens are bound to it, so drop the cached one. */
  function signedIn(user: Me): Me {
    resetAntiforgery()
    me.value = user
    loaded.value = true
    return user
  }

  async function loginLocal(email: string, password: string) {
    return signedIn(await identityApi.loginLocal(email, password))
  }

  async function loginCredentials(providerId: string, email: string, password: string) {
    return signedIn(await identityApi.loginCredentials(providerId, email, password))
  }

  async function systemLogin(email: string, password: string, code: string) {
    return signedIn(await identityApi.systemLogin(email, password, code))
  }

  async function acceptInvitation(token: string, mode: 'new' | 'existing', password: string, displayName?: string) {
    return signedIn(await identityApi.acceptInvitation(token, mode, password, displayName))
  }

  async function completeSetup(token: string, displayName: string, password: string, code: string, newPassword?: string) {
    return signedIn(await identityApi.completeSetup(token, displayName, password, code, newPassword))
  }

  async function logout() {
    try {
      await identityApi.logout()
    } finally {
      resetAntiforgery()
      me.value = null
      // Locally cached live drafts must not outlive the session (ADR-011 §7).
      await import('@/modules/editor/collaboration').then((m) => m.clearCollaborationCache()).catch(() => undefined)
    }
  }

  return { me, loaded, isAuthenticated, load, loginLocal, loginCredentials, systemLogin, acceptInvitation, completeSetup, logout }
})
