import { palette, updatePrimaryPalette } from '@primeuix/themes'
import type { TenantBranding } from '@/core/api/hostContext'

const HEX = /^#[0-9a-f]{6}$/i

/** Branding values are validated again on the client: only #rrggbb ever reaches the theme (no CSS injection). */
export function isHexColor(value: unknown): value is string {
  return typeof value === 'string' && HEX.test(value)
}

/** Applies the tenant's primary color as a full PrimeVue palette. Returns true when a brand color was applied. */
export function applyBranding(branding: TenantBranding | null | undefined, update: typeof updatePrimaryPalette = updatePrimaryPalette): boolean {
  const color = branding?.primaryColor
  if (!isHexColor(color)) return false
  update(palette(color) as Parameters<typeof updatePrimaryPalette>[0])
  return true
}
