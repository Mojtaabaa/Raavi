/**
 * Supported UI locales (ADR-022). Adding a language = add an entry here + a message file; no component changes.
 * Direction is a property of the locale, never inferred by mirroring CSS.
 */
import { display } from '@/core/runtime/display'

export type TextDirection = 'rtl' | 'ltr'

export interface LocaleDefinition {
  code: SupportedLocale
  /** BCP 47 tag used for Intl formatting. The Persian calendar is selected through the -u-ca- extension. */
  intlTag: string
  direction: TextDirection
  nativeName: string
  defaultCalendar: 'persian' | 'gregory'
}

export const SUPPORTED_LOCALES = ['fa', 'en'] as const
export type SupportedLocale = (typeof SUPPORTED_LOCALES)[number]

export const LOCALES: Record<SupportedLocale, LocaleDefinition> = {
  fa: { code: 'fa', intlTag: 'fa-IR', direction: 'rtl', nativeName: 'فارسی', defaultCalendar: 'persian' },
  en: { code: 'en', intlTag: 'en-US', direction: 'ltr', nativeName: 'English', defaultCalendar: 'gregory' },
}

export const SYSTEM_DEFAULT_LOCALE: SupportedLocale = 'fa'

export function isSupportedLocale(value: unknown): value is SupportedLocale {
  return typeof value === 'string' && (SUPPORTED_LOCALES as readonly string[]).includes(value)
}

/**
 * Preference precedence (ADR-022 §3): user preference → tenant default → system default.
 * Browser language is only a hint for anonymous pages and is passed as `tenantDefault` by the login page when no tenant default exists.
 */
export function resolveLocale(user?: string | null, tenantDefault?: string | null): SupportedLocale {
  if (isSupportedLocale(user)) return user
  if (isSupportedLocale(tenantDefault)) return tenantDefault
  return SYSTEM_DEFAULT_LOCALE
}

/** Applies lang/dir to the document root so layout logical properties and PrimeVue follow the active locale. */
export function applyDocumentLocale(locale: SupportedLocale, root: HTMLElement = document.documentElement): void {
  const def = LOCALES[locale]
  root.lang = def.intlTag
  root.dir = def.direction
}

/** Formats a UTC instant for display in the given locale and calendar. Storage is always UTC (ISO 8601). */
export function formatDate(
  isoUtc: string,
  locale: SupportedLocale,
  options: Intl.DateTimeFormatOptions & { calendar?: 'persian' | 'gregory'; timeZone?: string } = {},
): string {
  const def = LOCALES[locale]
  const calendar = options.calendar ?? (display.calendar === 'persian' ? 'persian' : display.calendar === 'gregory' ? 'gregory' : def.defaultCalendar)
  const { calendar: _ignored, ...rest } = options
  // the workspace's time zone applies to every instant shown (date-only values are passed with timeZone: 'UTC' by their callers)
  return new Intl.DateTimeFormat(`${def.intlTag}-u-ca-${calendar}`, { dateStyle: 'medium', ...(display.timeZone ? { timeZone: display.timeZone } : {}), ...rest }).format(new Date(isoUtc))
}

/** Date and time in the workspace's time zone and calendar. */
export function formatDateTime(isoUtc: string, locale: SupportedLocale): string {
  return formatDate(isoUtc, locale, { dateStyle: 'medium', timeStyle: 'short' })
}
