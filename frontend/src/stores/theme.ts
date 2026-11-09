import { defineStore } from 'pinia'
import { ref } from 'vue'
import { applyThemeToDocument, isThemeId, type ThemeId } from '@/core/theme/themes'
import { identityApi } from '@/core/api/identity'

const KEY = 'raavi.theme'

function stored(): ThemeId {
  try {
    const v = localStorage.getItem(KEY)
    return isThemeId(v) ? v : 'system'
  } catch {
    return 'system'
  }
}

const media = (): MediaQueryList | null => (typeof window !== 'undefined' && typeof window.matchMedia === 'function' ? window.matchMedia('(prefers-color-scheme: dark)') : null)

/**
 * The user's colour theme. The choice is kept in the browser (so the first paint after a reload is right, also before sign-in) and, for a
 * signed-in user, in their profile in the database, so it follows them to other devices.
 */
export const useThemeStore = defineStore('theme', () => {
  const theme = ref<ThemeId>(stored())
  const dark = ref(false)

  function apply() {
    dark.value = applyThemeToDocument(theme.value, media()?.matches ?? false).dark
  }

  function remember(id: ThemeId) {
    try {
      localStorage.setItem(KEY, id)
    } catch {
      /* private mode: the theme just lasts for this page */
    }
  }

  /** The user picks a theme: applied at once, remembered, and saved to the profile when signed in. */
  async function choose(id: ThemeId, signedIn: boolean) {
    theme.value = id
    remember(id)
    apply()
    if (signedIn) await identityApi.setTheme(id === 'system' ? null : id).catch(() => undefined)
  }

  /** After sign-in: the profile's theme wins over the one remembered in this browser. */
  function adopt(fromProfile: string | null | undefined) {
    if (!isThemeId(fromProfile) || fromProfile === theme.value) return
    theme.value = fromProfile
    remember(fromProfile)
    apply()
  }

  function start() {
    apply()
    media()?.addEventListener?.('change', () => theme.value === 'system' && apply())
  }

  return { theme, dark, choose, adopt, apply, start }
})
