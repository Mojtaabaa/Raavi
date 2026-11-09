import { api } from '@/core/api/http'
import { connection } from '@/core/api/connection'

/**
 * Sends errors that happen in the browser (script errors, unhandled promise rejections, errors inside components) to the installation's
 * error log, so they can be seen without asking the user for a screenshot of the console. Careful by design: at most 5 reports per page
 * load, the same error only once, nothing while the server is unreachable, text is cut short (the server cleans it again), and a failing
 * report is silently dropped — reporting must never cause an error of its own.
 */
const MAX = 5
const seen = new Set<string>()
let sent = 0

export function reportClientError(error: unknown, context = ''): void {
  try {
    if (sent >= MAX || connection.down.value) return
    const e = error instanceof Error ? error : new Error(typeof error === 'string' ? error : JSON.stringify(error)?.slice(0, 300) ?? 'unknown')
    const key = `${e.name}|${e.message}|${(e.stack ?? '').split('\n')[1] ?? ''}`
    if (seen.has(key)) return
    seen.add(key)
    sent++
    void api('POST', '/errors/client', {
      message: `${context ? context + ': ' : ''}${e.message}`.slice(0, 800),
      stack: (e.stack ?? '').slice(0, 5000),
      url: window.location.pathname,
      type: e.name,
    }).catch(() => undefined)
  } catch {
    /* never throw from the reporter */
  }
}

export function installErrorReporter(): void {
  window.addEventListener('error', (ev) => {
    // resource load errors (images, scripts) have no .error and are not application errors
    if (ev.error) reportClientError(ev.error)
  })
  window.addEventListener('unhandledrejection', (ev) => reportClientError(ev.reason, 'unhandled promise'))
}
