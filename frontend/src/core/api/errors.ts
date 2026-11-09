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

/**
 * What the user can DO about an error: numbered steps from `errorHelp.<code>.1…n`. Empty when nothing more can be said than the message itself.
 * (Shown by ErrorHelp.vue; the steps are written for people, not for developers.)
 */
export function errorSteps(error: unknown, t: Translate, te: HasKey): string[] {
  if (!(error instanceof ApiError)) return []
  const steps: string[] = []
  for (let i = 1; i <= 8; i++) {
    const key = `errorHelp.${error.code}.${i}`
    if (!te(key)) break
    steps.push(t(key))
  }
  return steps
}
