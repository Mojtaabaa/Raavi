import { createI18n } from 'vue-i18n'
import fa from './messages/fa.json'
import en from './messages/en.json'
import { SYSTEM_DEFAULT_LOCALE, type SupportedLocale } from './locales'

export type MessageSchema = typeof en

export function createRaaviI18n(locale: SupportedLocale = SYSTEM_DEFAULT_LOCALE) {
  return createI18n<[MessageSchema], SupportedLocale>({
    legacy: false,
    locale,
    fallbackLocale: 'en',
    messages: { fa, en },
  })
}
