import { ApiError } from './http'

type Translate = (key: string) => string
type HasKey = (key: string) => boolean

/** Localized message for an API error code (`errors.<code>`), falling back to a generic message. Never shows raw server text. */
export function errorMessage(error: unknown, t: Translate, te: HasKey): string {
  if (error instanceof ApiError) {
    const key = `errors.${error.code}`
    if (te(key)) return t(key)
  }
  return t('errors.generic')
}
