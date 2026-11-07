import { definePreset } from '@primeuix/themes'
import Aura from '@primeuix/themes/aura'

/**
 * Raavi design preset on top of PrimeVue Aura (ADR-001 §UI). Brand tokens live here and in tokens.css;
 * tenant branding overrides only the primary palette at runtime and can never inject CSS or scripts.
 */
export const RaaviPreset = definePreset(Aura, {
  semantic: {
    primary: {
      50: '{teal.50}',
      100: '{teal.100}',
      200: '{teal.200}',
      300: '{teal.300}',
      400: '{teal.400}',
      500: '{teal.500}',
      600: '{teal.600}',
      700: '{teal.700}',
      800: '{teal.800}',
      900: '{teal.900}',
      950: '{teal.950}',
    },
  },
})
