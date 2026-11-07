import { defineStore } from 'pinia'
import { ref } from 'vue'
import { applyDocumentLocale, isSupportedLocale, resolveLocale, type SupportedLocale } from '@/core/i18n/locales'

const STORAGE_KEY = 'raavi.locale'

function readStored(): string | null {
  try {
    return localStorage.getItem(STORAGE_KEY)
  } catch {
    return null
  }
}

function store(value: string): void {
  try {
    localStorage.setItem(STORAGE_KEY, value)
  } catch {
    /* storage unavailable (private mode) — preference just isn't remembered locally */
  }
}

/**
 * Active UI locale with precedence user → tenant → system (ADR-022 §3). The user preference is the server-side profile
 * value when signed in, otherwise the choice remembered in this browser.
 */
export const useLocaleStore = defineStore('locale', () => {
  const userPreference = ref<string | null>(readStored())
  const locale = ref<SupportedLocale>(resolveLocale(userPreference.value, null))

  function set(next: SupportedLocale): void {
    locale.value = next
    applyDocumentLocale(next)
  }

  /** Explicit choice by the user in this browser. */
  function setLocale(next: SupportedLocale): void {
    userPreference.value = next
    store(next)
    set(next)
  }

  /** Applies the tenant default only when the user has not chosen a language. Returns the effective locale. */
  function applyTenantDefault(tenantDefault: string | null | undefined): SupportedLocale {
    const effective = resolveLocale(isSupportedLocale(userPreference.value) ? userPreference.value : null, tenantDefault)
    set(effective)
    return effective
  }

  /** Server-side profile preference of the signed-in user (highest precedence). */
  function applyUserPreference(preference: string | null | undefined): void {
    if (!isSupportedLocale(preference)) return
    userPreference.value = preference
    store(preference)
    set(preference)
  }

  return { locale, setLocale, applyTenantDefault, applyUserPreference }
})
