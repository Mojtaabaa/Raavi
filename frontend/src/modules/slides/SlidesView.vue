<script setup lang="ts">
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRoute, useRouter } from 'vue-router'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { slidesApi, type SlideDeck, type SlideDirection } from '@/core/api/slides'
import { wikiApi, type Space } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import { useLocaleStore } from '@/stores/locale'

/** Slide decks of a space (Phase 9d): the list, and creation of a new deck with a title slide. */
const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const toast = useToast()
const locale = useLocaleStore()

const key = computed(() => String(route.params.key))
const space = ref<Space | null>(null)
const decks = ref<SlideDeck[]>([])
const loading = ref(true)
const creating = ref(false)
const title = ref('')
const direction = ref<SlideDirection>('Ltr')
const saving = ref(false)

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const canCreate = computed(() => !!space.value?.rights.canCreate)

async function load() {
  loading.value = true
  try {
    const [list, sp] = await Promise.all([slidesApi.list(key.value), wikiApi.space(key.value)])
    decks.value = list
    space.value = sp.value
  } catch (e) {
    fail(e)
  } finally {
    loading.value = false
  }
}
onMounted(load)
watch(key, load)

function openCreate() {
  direction.value = locale.locale === 'fa' ? 'Rtl' : 'Ltr'
  creating.value = true
}

async function create() {
  if (!title.value.trim()) return
  saving.value = true
  try {
    const deck = await slidesApi.create(key.value, {
      title: title.value.trim(),
      direction: direction.value,
      slides: [{ layout: 'Title', title: title.value.trim(), body: '', notes: '' }],
    })
    creating.value = false
    title.value = ''
    await router.push({ name: 'slide-deck', params: { id: deck.id } })
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <section class="decks" data-testid="slides">
    <header class="head">
      <h1>{{ t('slides.spaceTitle', { name: space?.name ?? key }) }}</h1>
      <Button v-if="canCreate" icon="pi pi-plus" :label="t('slides.new')" data-testid="new-deck" @click="openCreate" />
    </header>

    <p v-if="!loading && decks.length === 0" class="muted" data-testid="no-decks">{{ t('slides.none') }}</p>
    <ul class="list">
      <li v-for="d in decks" :key="d.id" class="item" data-testid="deck">
        <RouterLink :to="{ name: 'slide-deck', params: { id: d.id } }" class="name" dir="auto">{{ d.title }}</RouterLink>
        <span class="muted">{{ t('slides.summary', { count: d.slideCount }) }}</span>
      </li>
    </ul>

    <Dialog v-model:visible="creating" modal :header="t('slides.new')" :style="{ inlineSize: 'min(26rem, 95vw)' }">
      <form class="form" @submit.prevent="create">
        <label for="deck-title">{{ t('slides.deckTitle') }}</label>
        <InputText id="deck-title" v-model="title" maxlength="200" required data-testid="deck-title" />
        <label for="deck-direction">{{ t('slides.direction') }}</label>
        <select id="deck-direction" v-model="direction" data-testid="deck-direction">
          <option value="Ltr">{{ t('slides.ltr') }}</option>
          <option value="Rtl">{{ t('slides.rtl') }}</option>
        </select>
        <Button type="submit" :label="t('common.save')" :loading="saving" :disabled="!title.trim()" data-testid="save-deck" />
      </form>
    </Dialog>
  </section>
</template>

<style scoped>
.decks { display: grid; gap: var(--raavi-space-3); max-inline-size: 56rem; }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: center; }
h1 { margin: 0; font-size: 1.5rem; }
.list { list-style: none; margin: 0; padding: 0; display: grid; gap: var(--raavi-space-2); }
.item { display: flex; justify-content: space-between; gap: var(--raavi-space-3); padding: var(--raavi-space-3); border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); }
.name { font-weight: 600; overflow-wrap: anywhere; }
.form { display: grid; gap: var(--raavi-space-2); }
.muted { color: var(--raavi-text-muted); margin: 0; }
</style>
