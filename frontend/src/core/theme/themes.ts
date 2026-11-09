import { palette } from '@primeuix/themes'

/**
 * Colour themes (Phase 14). A theme is a handful of numbers; the CSS variables of the design tokens, the PrimeVue primary and surface
 * scales and the icon colour are all derived from them, so every component and icon follows the theme. `light` and `dark` are the
 * built-in token sets (no overrides); `system` follows the device.
 */
export type ThemeId = 'system' | 'light' | 'dark' | 'chocolate' | 'ocean' | 'forest' | 'rose' | 'midnight' | 'contrast'

export interface ThemeDef {
  id: ThemeId
  dark: boolean
  /** HSL hue/saturation of the tinted greys. Absent = built-in tokens. */
  hue?: number
  sat?: number
  primary?: string
  /** Fully custom colours (high contrast). */
  fixed?: { bg: string; surface: string; border: string; text: string; muted: string; icon: string }
  /** Colour swatches for the picker: background, surface, accent. */
  swatch: [string, string, string]
}

export const THEMES: readonly ThemeDef[] = [
  { id: 'system', dark: false, swatch: ['#f7f8fa', '#161c25', '#0d9488'] },
  { id: 'light', dark: false, swatch: ['#f7f8fa', '#ffffff', '#0d9488'] },
  { id: 'dark', dark: true, swatch: ['#0f141b', '#161c25', '#2dd4bf'] },
  { id: 'chocolate', dark: true, hue: 22, sat: 34, primary: '#d08c52', swatch: ['#1f1511', '#2c1f19', '#d08c52'] },
  { id: 'midnight', dark: true, hue: 228, sat: 45, primary: '#7f8fff', swatch: ['#0b1020', '#131a2f', '#7f8fff'] },
  { id: 'ocean', dark: false, hue: 205, sat: 50, primary: '#0b7fb8', swatch: ['#eaf4fa', '#ffffff', '#0b7fb8'] },
  { id: 'forest', dark: false, hue: 140, sat: 32, primary: '#2e7d4f', swatch: ['#eff6f0', '#ffffff', '#2e7d4f'] },
  { id: 'rose', dark: false, hue: 340, sat: 48, primary: '#d6336c', swatch: ['#fdf0f4', '#ffffff', '#d6336c'] },
  { id: 'contrast', dark: false, primary: '#0033cc', fixed: { bg: '#ffffff', surface: '#ffffff', border: '#000000', text: '#000000', muted: '#262626', icon: '#0033cc' }, swatch: ['#ffffff', '#ffffff', '#0033cc'] },
]

export const THEME_IDS: readonly ThemeId[] = THEMES.map((t) => t.id)
export const isThemeId = (value: unknown): value is ThemeId => typeof value === 'string' && (THEME_IDS as readonly string[]).includes(value)

const hsl = (h: number, s: number, l: number): string => {
  const a = (s / 100) * Math.min(l / 100, 1 - l / 100)
  const f = (n: number) => {
    const k = (n + h / 30) % 12
    const c = l / 100 - a * Math.max(-1, Math.min(k - 3, Math.min(9 - k, 1)))
    return Math.round(255 * c).toString(16).padStart(2, '0')
  }
  return `#${f(0)}${f(8)}${f(4)}`
}

const mix = (a: [number, number, number], b: [number, number, number], t: number): string =>
  '#' + a.map((v, i) => Math.round(v + (b[i]! - v) * t).toString(16).padStart(2, '0')).join('')

const rgb = (hex: string): [number, number, number] => [parseInt(hex.slice(1, 3), 16), parseInt(hex.slice(3, 5), 16), parseInt(hex.slice(5, 7), 16)]

/** Mix weights of the PrimeVue surface scale between white (0) and the tinted near-black (1), taken from the Aura zinc scale. */
const SURFACE_STEPS: Record<string, number> = { 50: 0.02, 100: 0.045, 200: 0.11, 300: 0.17, 400: 0.37, 500: 0.55, 600: 0.7, 700: 0.77, 800: 0.88, 900: 0.94, 950: 1 }

/** The CSS custom properties (name → value) a theme sets on the document element. Empty for the built-in light/dark/system. */
export function themeVariables(def: ThemeDef): Record<string, string> {
  if (!def.primary) return {}
  const vars: Record<string, string> = {}
  const primary = palette(def.primary) as Record<string, string>
  for (const [step, value] of Object.entries(primary)) vars[`--p-primary-${step}`] = value
  const hue = def.hue ?? 0
  const sat = def.sat ?? 0
  const darkEnd = rgb(hsl(hue, Math.min(sat, 40), def.dark ? 7 : 8))
  for (const [step, t] of Object.entries(SURFACE_STEPS)) vars[`--p-surface-${step}`] = mix([255, 255, 255], darkEnd, t)
  vars['--p-surface-0'] = '#ffffff'
  if (def.fixed) {
    Object.assign(vars, {
      '--raavi-bg': def.fixed.bg,
      '--raavi-surface': def.fixed.surface,
      '--raavi-border': def.fixed.border,
      '--raavi-text': def.fixed.text,
      '--raavi-text-muted': def.fixed.muted,
      '--raavi-icon': def.fixed.icon,
    })
  } else if (def.dark) {
    Object.assign(vars, {
      '--raavi-bg': hsl(hue, sat, 8),
      '--raavi-surface': hsl(hue, sat, 12),
      '--raavi-border': hsl(hue, sat * 0.8, 23),
      '--raavi-text': hsl(hue, 22, 92),
      '--raavi-text-muted': hsl(hue, 14, 70),
      '--raavi-icon': primary['300']!,
    })
  } else {
    Object.assign(vars, {
      '--raavi-bg': hsl(hue, sat * 0.9, 95.5),
      '--raavi-surface': hsl(hue, sat * 0.4, 99),
      '--raavi-border': hsl(hue, sat * 0.5, 85),
      '--raavi-text': hsl(hue, 40, 12),
      '--raavi-text-muted': hsl(hue, 14, 36),
      '--raavi-icon': def.primary,
    })
  }
  return vars
}

const applied = new Set<string>()

/** Applies a theme to the document: dark class, `data-theme`, colour-scheme and the derived variables. Idempotent. */
export function applyThemeToDocument(id: ThemeId, prefersDark: boolean, root: HTMLElement = document.documentElement): { dark: boolean } {
  const def = THEMES.find((t) => t.id === id) ?? THEMES[0]!
  const dark = id === 'system' ? prefersDark : def.dark
  root.classList.toggle('raavi-dark', dark)
  root.dataset.theme = id
  root.style.colorScheme = dark ? 'dark' : 'light'
  for (const name of applied) root.style.removeProperty(name)
  applied.clear()
  for (const [name, value] of Object.entries(themeVariables(def))) {
    root.style.setProperty(name, value)
    applied.add(name)
  }
  return { dark }
}
