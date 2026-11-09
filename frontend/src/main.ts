import { createApp } from 'vue'
import { createPinia } from 'pinia'
import PrimeVue from 'primevue/config'
import ToastService from 'primevue/toastservice'
import ConfirmationService from 'primevue/confirmationservice'
import 'vazirmatn/Vazirmatn-Variable-font-face.css'
import '@fontsource-variable/inter'
import 'primeicons/primeicons.css'
import '@/core/theme/tokens.css'
import App from '@/app/App.vue'
import { createRaaviRouter } from '@/router'
import { createRaaviI18n } from '@/core/i18n'
import { applyDocumentLocale } from '@/core/i18n/locales'
import { RaaviPreset } from '@/core/theme/preset'
import { useLocaleStore } from '@/stores/locale'
import { useThemeStore } from '@/stores/theme'

const app = createApp(App)
const pinia = createPinia()
app.use(pinia)

const locale = useLocaleStore(pinia).locale
applyDocumentLocale(locale)

useThemeStore(pinia).start()
app.use(createRaaviI18n(locale))
app.use(createRaaviRouter())
app.use(PrimeVue, {
  theme: { preset: RaaviPreset, options: { darkModeSelector: '.raavi-dark', cssLayer: false } },
  ripple: false,
})
app.use(ToastService)
app.use(ConfirmationService)

app.mount('#app')
