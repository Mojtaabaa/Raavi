import { describe, expect, it } from 'vitest'
import { applyDocumentLocale, formatDate, isSupportedLocale, resolveLocale } from '@/core/i18n/locales'
import fa from '@/core/i18n/messages/fa.json'
import en from '@/core/i18n/messages/en.json'

function keys(obj: object, prefix = ''): string[] {
  return Object.entries(obj).flatMap(([k, v]) =>
    v !== null && typeof v === 'object' ? keys(v as object, `${prefix}${k}.`) : [`${prefix}${k}`],
  )
}

describe('locale resolution', () => {
  it('prefers user, then tenant, then system default', () => {
    expect(resolveLocale('en', 'fa')).toBe('en')
    expect(resolveLocale(null, 'en')).toBe('en')
    expect(resolveLocale('de', 'xx')).toBe('fa')
    expect(resolveLocale(undefined, undefined)).toBe('fa')
  })

  it('rejects unsupported values', () => {
    expect(isSupportedLocale('fa')).toBe(true)
    expect(isSupportedLocale('ar')).toBe(false)
    expect(isSupportedLocale(42)).toBe(false)
  })
})

describe('document direction', () => {
  it('sets rtl for Persian and ltr for English', () => {
    const root = document.createElement('html')
    applyDocumentLocale('fa', root)
    expect(root.dir).toBe('rtl')
    expect(root.lang).toBe('fa-IR')
    applyDocumentLocale('en', root)
    expect(root.dir).toBe('ltr')
    expect(root.lang).toBe('en-US')
  })
})

describe('date formatting', () => {
  it('uses the Jalali calendar for Persian by default', () => {
    // 2024-03-20 is 1403-01-01 in the Solar Hijri calendar (Nowruz).
    const text = formatDate('2024-03-20T12:00:00Z', 'fa', { timeZone: 'UTC', dateStyle: 'short' })
    expect(text).toContain('۱۴۰۳')
  })

  it('uses the Gregorian calendar for English by default', () => {
    expect(formatDate('2024-03-20T12:00:00Z', 'en', { timeZone: 'UTC', dateStyle: 'short' })).toContain('24')
  })
})

describe('message catalogs', () => {
  it('Persian and English define the same keys', () => {
    expect(keys(fa).sort()).toEqual(keys(en).sort())
  })
})
