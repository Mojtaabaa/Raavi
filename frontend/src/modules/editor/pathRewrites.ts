import { wikiApi } from '@/core/api/wiki'

export interface PathRewrite {
  prefix: string
  baseUrl: string
}

let rules: PathRewrite[] = []
let loaded: Promise<void> | null = null

/** Loads configured path-prefix → base URL rewrites (non-secret) once per session. */
export function ensurePathRewrites(): Promise<void> {
  if (!loaded) {
    loaded = wikiApi.linkRewrites()
      .then((list) => {
        rules = [...list]
          .map((r) => ({ prefix: r.prefix.replace(/\/+$/, ''), baseUrl: r.baseUrl.replace(/\/+$/, '') }))
          .filter((r) => r.prefix && r.baseUrl)
          .sort((a, b) => b.prefix.length - a.prefix.length)
      })
      .catch(() => {
        rules = []
      })
  }
  return loaded
}

export function configuredPathPrefixes(): string[] {
  return rules.map((r) => r.prefix)
}

/** Maps `vianna/UI/...` → `https://svn.../UI/...` when a rewrite rule matches. */
export function rewritePathHref(href: string): string | null {
  if (!href || href !== href.trim() || href.includes('\\')) return null
  for (const rule of rules) {
    if (href === rule.prefix) return `${rule.baseUrl}/`
    if (href.startsWith(`${rule.prefix}/`)) return rule.baseUrl + href.slice(rule.prefix.length)
  }
  return null
}

export function isConfiguredPathHref(href: string): boolean {
  return rewritePathHref(href) !== null
}

/** Test helper: replace rules without hitting the API. */
export function setPathRewritesForTests(list: PathRewrite[]): void {
  rules = [...list]
  loaded = Promise.resolve()
}
