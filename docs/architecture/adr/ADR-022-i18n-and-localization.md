# ADR-022: Internationalization and Localization

**Status:** Accepted · **Date:** 2026-11-06

## 1. Context
Persian (RTL) and English (LTR) from day one; more languages later; Jalali and Gregorian calendars; multilingual content optional.

## 2. Decision — UI
- **vue-i18n** (composition mode) with one message catalog per locale; a test asserts all catalogs define the same keys.
- Locale registry (`frontend/src/core/i18n/locales.ts`, implemented): code, Intl tag, direction, native name, default calendar. Adding a language = new registry entry + catalog.
- Direction set on `<html dir lang>`; layout uses **CSS logical properties** (`margin-inline-start`, `inset-inline-end`, …) throughout — no mirrored stylesheets. Directional icons (arrows, chevrons) are chosen per direction; non-directional icons are not flipped. PrimeVue components receive the document direction.
- Dates/numbers via `Intl` (`fa-IR-u-ca-persian`, `en-US-u-ca-gregory`), Persian digits in Persian UI; user may choose calendar independently of language. Jalali date picker: PrimeVue DatePicker lacks a Persian calendar → wrap a Jalali-capable picker component (evaluated in Phase 3; candidates must be MIT/BSD).
- Fonts self-hosted: Vazirmatn (fa), Inter (en), both OFL.
- Locale-aware sorting via `Intl.Collator`.

## 3. Decision — preference precedence
**User preference → tenant default → system default (`Raavi:Defaults:Culture`)**. Browser `Accept-Language` is used only on anonymous pages when the tenant has no default. Implemented in `resolveLocale()`; the API applies the same precedence for localized error details, e-mails and notifications.

## 4. Decision — backend
- Error codes are stable keys; messages localized server-side from resource files (`.resx` per module) for e-mails/notifications/problem details.
- All timestamps UTC; schedules store IANA time zones; `System.Globalization.PersianCalendar` for server-side Jalali formatting (e-mails, exports).

## 5. Decision — multilingual content
Independent of UI language. A content item may have **translations**: `ContentTranslations(SourceContentId, TranslatedContentId, Language)` linking full content items (each with its own versions and ACL inheriting from the source). Content carries a `Language` field (auto-detected, editable) used by search analyzers. No page is forced to have translations.

## 6. Consequences
RTL testing is part of the frontend test suite (direction tests, visual checks in Phase 12). Editor blocks default to `dir="auto"` for mixed-direction content.
