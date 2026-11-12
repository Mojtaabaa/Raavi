/** Formats stored minutes for display (effort, not calendar duration). */
export function formatDuration(minutes: number, t: (key: string, values?: Record<string, unknown>) => string): string {
  const m = Math.max(0, Math.floor(minutes))
  const h = Math.floor(m / 60)
  const rem = m % 60
  if (h === 0) return t('tasks.durationMinutes', { n: rem })
  if (rem === 0) return t('tasks.durationHours', { n: h })
  return t('tasks.durationHoursMinutes', { h, m: rem })
}
