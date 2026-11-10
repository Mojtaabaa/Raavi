<script setup lang="ts">
import { useLocaleStore } from '@/stores/locale'
import { formatDate } from '@/core/i18n/locales'
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import { aiApi, type AiSettings, type AiUsage, type IndexStatus } from '@/core/api/ai'
import { errorMessage } from '@/core/api/errors'

/** AI provider of the workspace (Phase 11a): OpenAI-compatible endpoint, model, key (write-only), limits and this month's usage. */
const { t, te } = useI18n()
const localeStore = useLocaleStore()
const toast = useToast()

const current = ref<AiSettings | null>(null)
const usage = ref<AiUsage | null>(null)
const saving = ref(false)
const index = ref<IndexStatus | null>(null)
const indexing = ref(false)
const form = ref({ enabled: false, baseUrl: '', model: '', embeddingModel: '', apiKey: '', removeKey: false, monthlyTokenCap: 0, maxOutputTokens: 800 })

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const hasKey = computed(() => !!current.value?.hasApiKey)
const percent = computed(() => (current.value && usage.value && usage.value.monthlyTokenCap > 0 ? Math.min(100, Math.round((usage.value.tokensThisMonth / usage.value.monthlyTokenCap) * 100)) : null))
const valid = computed(() => form.value.baseUrl.trim() !== '' && form.value.model.trim() !== '' && (!form.value.enabled || form.value.removeKey === false) && (!form.value.enabled || form.value.apiKey.trim() !== '' || !!current.value?.hasApiKey))

function apply(s: AiSettings) {
  current.value = s
  form.value = { enabled: s.enabled, baseUrl: s.baseUrl, model: s.model, embeddingModel: s.embeddingModel ?? '', apiKey: '', removeKey: false, monthlyTokenCap: s.monthlyTokenCap, maxOutputTokens: s.maxOutputTokens }
}

async function load() {
  try {
    const [s, u, ix] = await Promise.all([aiApi.settings(), aiApi.usage(), aiApi.indexStatus().catch(() => null)])
    apply(s)
    usage.value = u
    index.value = ix
  } catch (e) {
    fail(e)
  }
}
onMounted(load)

async function runIndex(rebuild: boolean) {
  indexing.value = true
  try {
    index.value = await (rebuild ? aiApi.rebuildIndex() : aiApi.runIndex())
    toast.add({ severity: 'info', summary: t('ai.index.started'), life: 4000 })
  } catch (e) {
    fail(e)
  } finally {
    indexing.value = false
  }
}

async function save() {
  if (!current.value || !valid.value) return
  saving.value = true
  try {
    const key = form.value.removeKey ? '' : form.value.apiKey.trim() === '' ? undefined : form.value.apiKey.trim()
    apply(
      await aiApi.saveSettings(current.value, {
        enabled: form.value.enabled,
        baseUrl: form.value.baseUrl.trim(),
        model: form.value.model.trim(),
        embeddingModel: form.value.embeddingModel.trim(),
        apiKey: key,
        monthlyTokenCap: Number(form.value.monthlyTokenCap) || 0,
        maxOutputTokens: Number(form.value.maxOutputTokens) || 800,
      }),
    )
    usage.value = await aiApi.usage()
    index.value = await aiApi.indexStatus().catch(() => index.value)
    toast.add({ severity: 'success', summary: t('common.saved'), life: 2000 })
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <section v-if="current" class="ai" data-testid="ai-settings">
    <h1>{{ t('ai.settingsTitle') }}</h1>
    <p v-if="!current.hostPolicyConfigured" class="warn" role="alert" data-testid="no-hosts">{{ t('ai.noHosts') }}</p>
    <p class="muted">{{ t('ai.privacy') }}</p>

    <form class="form" @submit.prevent="save">
      <label class="check"><input v-model="form.enabled" type="checkbox" data-testid="ai-enabled" /> {{ t('ai.enable') }}</label>

      <label for="ai-url">{{ t('ai.baseUrl') }}</label>
      <InputText id="ai-url" v-model="form.baseUrl" maxlength="300" dir="ltr" placeholder="https://api.example.com/v1" data-testid="ai-url" />

      <label for="ai-model">{{ t('ai.model') }}</label>
      <InputText id="ai-model" v-model="form.model" maxlength="100" dir="ltr" data-testid="ai-model" />

      <label for="ai-embed">{{ t('ai.embeddingModel') }}</label>
      <InputText id="ai-embed" v-model="form.embeddingModel" maxlength="100" dir="ltr" placeholder="text-embedding-3-small" data-testid="ai-embedding-model" />
      <small class="muted">{{ t('ai.embeddingHelp') }}</small>

      <label for="ai-key">{{ t('ai.apiKey') }}</label>
      <input id="ai-key" v-model="form.apiKey" type="password" autocomplete="off" maxlength="500" dir="ltr" :placeholder="hasKey ? t('ai.keyStored') : ''" :disabled="form.removeKey" data-testid="ai-key" />
      <label v-if="hasKey" class="check"><input v-model="form.removeKey" type="checkbox" data-testid="ai-remove-key" /> {{ t('ai.removeKey') }}</label>

      <label for="ai-cap">{{ t('ai.cap') }}</label>
      <input id="ai-cap" v-model.number="form.monthlyTokenCap" type="number" min="0" max="1000000000" data-testid="ai-cap" />
      <small class="muted">{{ t('ai.capHint') }}</small>

      <label for="ai-max">{{ t('ai.maxOutput') }}</label>
      <input id="ai-max" v-model.number="form.maxOutputTokens" type="number" min="64" max="4096" data-testid="ai-max" />

      <Button type="submit" :label="t('common.save')" :loading="saving" :disabled="!valid" data-testid="ai-save" />
    </form>

    <section v-if="index" class="usage" :aria-label="t('ai.index.title')" data-testid="ai-index">
      <h2>{{ t('ai.index.title') }}</h2>
      <p>{{ t('ai.index.summary', { pages: index.pages, chunks: index.chunks, embedded: index.embeddedChunks }) }}</p>
      <p class="muted">{{ index.embeddingsOn ? t('ai.index.hybrid', { model: index.embeddingModel }) : t('ai.index.keywordOnly') }}<template v-if="index.pagesToIndex > 0"> · {{ t('ai.index.pending', { n: index.pagesToIndex }) }}</template></p>
      <p v-if="index.lastIndexedAt" class="muted small">{{ t('ai.index.last', { time: formatDate(index.lastIndexedAt, localeStore.locale, { dateStyle: 'medium', timeStyle: 'short' }) }) }}</p>
      <div class="idx-actions">
        <Button size="small" outlined icon="pi pi-refresh" :label="t('ai.index.run')" :loading="indexing" data-testid="index-run" @click="runIndex(false)" />
        <Button size="small" text severity="secondary" icon="pi pi-replay" :label="t('ai.index.rebuild')" :loading="indexing" data-testid="index-rebuild" @click="runIndex(true)" />
      </div>
      <small class="muted">{{ t('ai.index.help') }}</small>
    </section>

    <section v-if="usage" class="usage" :aria-label="t('ai.usage')" data-testid="ai-usage">
      <h2>{{ t('ai.usage') }}</h2>
      <p>
        {{ t('ai.tokens', { n: usage.tokensThisMonth }) }}<template v-if="usage.monthlyTokenCap > 0"> / {{ usage.monthlyTokenCap }} ({{ percent }}%)</template>
        · {{ t('ai.requests', { n: usage.requestsThisMonth, failed: usage.failedThisMonth }) }}
      </p>
      <ul v-if="usage.topUsers.length" class="top">
        <li v-for="u in usage.topUsers" :key="u.userId">{{ u.name ?? t('wiki.unknownUser') }} — {{ t('ai.tokens', { n: u.tokens }) }}, {{ t('ai.requestsShort', { n: u.requests }) }}</li>
      </ul>
    </section>
  </section>
</template>

<style scoped>
.ai { display: grid; gap: var(--raavi-space-3); max-inline-size: 40rem; }
h1 { margin: 0; font-size: 1.5rem; }
h2 { margin: 0; font-size: 1.1rem; }
.form { display: grid; gap: var(--raavi-space-2); }
.form input:not([type='checkbox']) { font: inherit; padding: 0.4rem; }
.check { display: flex; gap: 0.5rem; align-items: center; }
.warn { color: var(--raavi-danger, #b91c1c); margin: 0; }
.muted { color: var(--raavi-text-muted); margin: 0; }
.top { margin: 0; padding-inline-start: 1.2rem; }
.idx-actions { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; }
.small { font-size: 0.82rem; }
</style>
