<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { onBeforeRouteLeave, useRoute, useRouter } from 'vue-router'
import { useConfirm } from 'primevue/useconfirm'
import { useToast } from 'primevue/usetoast'
import Button from 'primevue/button'
import InputText from 'primevue/inputtext'
import { slidesApi, type SlideDeck, type SlideDirection, type SlideLayout } from '@/core/api/slides'
import { errorMessage } from '@/core/api/errors'
import SlideCanvas from './SlideCanvas.vue'

/**
 * One slide deck (Phase 9d). The whole deck is edited locally and saved as one document (one ETag); slides can be added, reordered and
 * removed, and each has a layout, text and speaker notes. Present mode runs full-screen with the keyboard; export gives a PPTX (server)
 * and a print page for PDF (browser).
 */
interface Draft {
  key: string
  id?: string
  layout: SlideLayout
  title: string
  body: string
  notes: string
}

const { t, te } = useI18n()
const route = useRoute()
const router = useRouter()
const toast = useToast()
const confirm = useConfirm()

const layouts: SlideLayout[] = ['Title', 'Bullets', 'Text']
const id = computed(() => String(route.params.id))
const deck = ref<SlideDeck | null>(null)
const title = ref('')
const direction = ref<SlideDirection>('Ltr')
const slides = ref<Draft[]>([])
const selected = ref(0)
const saved = ref('')
const saving = ref(false)
const presenting = ref(false)
const presentIndex = ref(0)
let counter = 0

const fail = (e: unknown) => toast.add({ severity: 'error', summary: errorMessage(e, t, te), life: 6000 })
const canEdit = computed(() => !!deck.value?.canEdit)
const current = computed(() => slides.value[selected.value] ?? null)
const snapshot = () => JSON.stringify({ title: title.value, direction: direction.value, slides: slides.value.map(({ id: i, layout, title: ti, body, notes }) => ({ id: i, layout, title: ti, body, notes })) })
const dirty = computed(() => canEdit.value && snapshot() !== saved.value)
const printHref = computed(() => router.resolve({ name: 'slide-print', params: { id: id.value }, query: { autoprint: '1' } }).href)
const presented = computed(() => slides.value[presentIndex.value] ?? null)

function apply(d: SlideDeck) {
  deck.value = d
  title.value = d.title
  direction.value = d.direction
  slides.value = d.slides.map((s) => ({ key: s.id, id: s.id, layout: s.layout, title: s.title, body: s.body, notes: s.notes }))
  selected.value = Math.min(selected.value, Math.max(0, slides.value.length - 1))
  saved.value = snapshot()
}

async function load() {
  try {
    apply(await slidesApi.get(id.value))
  } catch (e) {
    fail(e)
  }
}
onMounted(load)
watch(id, load)

function addSlide() {
  slides.value.splice(selected.value + 1, 0, { key: `new-${++counter}`, layout: 'Bullets', title: '', body: '', notes: '' })
  selected.value += 1
}

function move(delta: -1 | 1) {
  const from = selected.value
  const to = from + delta
  if (to < 0 || to >= slides.value.length) return
  const [item] = slides.value.splice(from, 1)
  slides.value.splice(to, 0, item!)
  selected.value = to
}

function removeSlide() {
  if (!current.value) return
  slides.value.splice(selected.value, 1)
  selected.value = Math.min(selected.value, Math.max(0, slides.value.length - 1))
}

async function save() {
  if (!deck.value || !dirty.value) return
  saving.value = true
  try {
    const updated = await slidesApi.update(deck.value, {
      title: title.value.trim(),
      direction: direction.value,
      slides: slides.value.map((s) => ({ id: s.id, layout: s.layout, title: s.title, body: s.body, notes: s.notes })),
    })
    apply(updated)
    toast.add({ severity: 'success', summary: t('common.saved'), life: 2000 })
  } catch (e) {
    fail(e)
  } finally {
    saving.value = false
  }
}

function removeDeck() {
  const d = deck.value
  if (!d) return
  confirm.require({
    header: t('common.confirmTitle'),
    message: t('slides.confirmDelete', { name: d.title }),
    acceptLabel: t('common.yes'),
    rejectLabel: t('common.no'),
    accept: async () => {
      try {
        await slidesApi.remove(d.id)
        saved.value = snapshot() // nothing left to lose
        await router.push({ name: 'space-slides', params: { key: d.spaceKey } })
      } catch (e) {
        fail(e)
      }
    },
  })
}

// ── Present mode ────────────────────────────────────────────────────────────────────────────

function present() {
  if (slides.value.length === 0) return
  presentIndex.value = selected.value
  presenting.value = true
}

function step(delta: -1 | 1) {
  presentIndex.value = Math.min(slides.value.length - 1, Math.max(0, presentIndex.value + delta))
}

function onKey(event: KeyboardEvent) {
  if (!presenting.value) return
  const rtl = direction.value === 'Rtl'
  const forward = event.key === 'ArrowRight' ? !rtl : event.key === 'ArrowLeft' ? rtl : ['ArrowDown', 'PageDown', ' ', 'Enter'].includes(event.key)
  const back = event.key === 'ArrowRight' ? rtl : event.key === 'ArrowLeft' ? !rtl : ['ArrowUp', 'PageUp', 'Backspace'].includes(event.key)
  if (event.key === 'Escape') presenting.value = false
  else if (forward) step(1)
  else if (back) step(-1)
  else return
  event.preventDefault()
}
onMounted(() => window.addEventListener('keydown', onKey))
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))

onBeforeRouteLeave(() => !dirty.value || window.confirm(t('slides.unsaved')))
</script>

<template>
  <section v-if="deck" class="deck" data-testid="deck">
    <header class="head">
      <div class="titles">
        <RouterLink :to="{ name: 'space-slides', params: { key: deck.spaceKey } }" class="muted">{{ deck.spaceName }}</RouterLink>
        <InputText v-if="canEdit" v-model="title" maxlength="200" class="deck-title" dir="auto" :aria-label="t('slides.deckTitle')" data-testid="deck-title-input" />
        <h1 v-else dir="auto">{{ deck.title }}</h1>
      </div>
      <div class="tools">
        <select v-if="canEdit" v-model="direction" :aria-label="t('slides.direction')" data-testid="deck-direction-select">
          <option value="Ltr">{{ t('slides.ltr') }}</option>
          <option value="Rtl">{{ t('slides.rtl') }}</option>
        </select>
        <Button v-if="canEdit" icon="pi pi-save" :label="t('common.save')" :loading="saving" :disabled="!dirty || !title.trim()" data-testid="save-deck" @click="save" />
        <Button icon="pi pi-play" severity="secondary" :label="t('slides.present')" :disabled="slides.length === 0" data-testid="present" @click="present" />
        <Button as="a" :href="slidesApi.exportUrl(deck.id)" icon="pi pi-download" text :label="t('slides.exportPptx')" :disabled="dirty" data-testid="export-pptx" />
        <Button as="a" :href="printHref" target="_blank" rel="noopener" icon="pi pi-file-pdf" text :label="t('slides.exportPdf')" :disabled="dirty" data-testid="export-pdf" />
      </div>
    </header>
    <p v-if="dirty" class="muted" role="status" data-testid="unsaved">{{ t('slides.exportAfterSave') }}</p>

    <div class="workspace">
      <nav class="strip" :aria-label="t('slides.slides')">
        <ol>
          <li v-for="(s, i) in slides" :key="s.key">
            <button type="button" class="thumb" :class="{ on: i === selected }" :aria-current="i === selected" data-testid="thumb" @click="selected = i">
              <span class="num">{{ i + 1 }}</span>
              <span class="label" dir="auto">{{ s.title || t('slides.untitled') }}</span>
            </button>
          </li>
        </ol>
        <Button v-if="canEdit" icon="pi pi-plus" severity="secondary" outlined size="small" :label="t('slides.addSlide')" data-testid="add-slide" @click="addSlide" />
      </nav>

      <div v-if="current" class="stage">
        <SlideCanvas :layout="current.layout" :title="current.title" :body="current.body" :direction="direction" />

        <form v-if="canEdit" class="form" @submit.prevent>
          <div class="row">
            <label for="slide-layout">{{ t('slides.layout') }}</label>
            <select id="slide-layout" v-model="current.layout" data-testid="slide-layout">
              <option v-for="l in layouts" :key="l" :value="l">{{ t(`slides.layouts.${l}`) }}</option>
            </select>
            <span class="spacer" />
            <Button type="button" icon="pi pi-arrow-up" text rounded :disabled="selected === 0" :aria-label="t('slides.moveUp')" data-testid="move-up" @click="move(-1)" />
            <Button type="button" icon="pi pi-arrow-down" text rounded :disabled="selected >= slides.length - 1" :aria-label="t('slides.moveDown')" data-testid="move-down" @click="move(1)" />
            <Button type="button" icon="pi pi-trash" text rounded severity="danger" :aria-label="t('slides.removeSlide')" data-testid="remove-slide" @click="removeSlide" />
          </div>
          <label for="slide-title">{{ t('slides.slideTitle') }}</label>
          <InputText id="slide-title" v-model="current.title" maxlength="200" dir="auto" data-testid="slide-title" />
          <label for="slide-body">{{ current.layout === 'Title' ? t('slides.subtitle') : t('slides.body') }}</label>
          <textarea id="slide-body" v-model="current.body" rows="7" maxlength="4000" dir="auto" :placeholder="t('slides.bodyHint')" data-testid="slide-body" />
          <label for="slide-notes">{{ t('slides.notes') }}</label>
          <textarea id="slide-notes" v-model="current.notes" rows="3" maxlength="2000" dir="auto" data-testid="slide-notes" />
          <Button type="button" severity="danger" text size="small" :label="t('slides.deleteDeck')" class="delete-deck" data-testid="delete-deck" @click="removeDeck" />
        </form>
        <p v-else-if="current.notes" class="notes" dir="auto"><strong>{{ t('slides.notes') }}:</strong> {{ current.notes }}</p>
      </div>
      <p v-else class="muted" data-testid="no-slides">{{ t('slides.noSlides') }}</p>
    </div>

    <div v-if="presenting && presented" class="present" role="dialog" aria-modal="true" :aria-label="t('slides.present')" data-testid="presenting">
      <div class="present-slide">
        <SlideCanvas :layout="presented.layout" :title="presented.title" :body="presented.body" :direction="direction" />
      </div>
      <p v-if="presented.notes" class="present-notes" dir="auto" data-testid="present-notes">{{ presented.notes }}</p>
      <div class="present-bar">
        <Button icon="pi pi-angle-left" text rounded severity="contrast" :disabled="presentIndex === 0" :aria-label="t('slides.prev')" @click="step(direction === 'Rtl' ? 1 : -1)" />
        <span data-testid="present-position">{{ presentIndex + 1 }} / {{ slides.length }}</span>
        <Button icon="pi pi-angle-right" text rounded severity="contrast" :disabled="presentIndex >= slides.length - 1" :aria-label="t('slides.next')" @click="step(direction === 'Rtl' ? -1 : 1)" />
        <Button icon="pi pi-times" text rounded severity="contrast" :aria-label="t('slides.exitPresent')" data-testid="exit-present" @click="presenting = false" />
      </div>
    </div>
  </section>
</template>

<style scoped>
.deck { display: grid; gap: var(--raavi-space-3); }
.head { display: flex; justify-content: space-between; gap: var(--raavi-space-3); flex-wrap: wrap; align-items: end; }
.titles { display: grid; gap: 0.25rem; min-inline-size: 0; flex: 1 1 18rem; }
h1 { margin: 0; font-size: 1.5rem; }
.deck-title { font-size: 1.25rem; font-weight: 600; }
.tools { display: flex; gap: var(--raavi-space-2); flex-wrap: wrap; align-items: center; }
.workspace { display: grid; grid-template-columns: minmax(11rem, 14rem) 1fr; gap: var(--raavi-space-3); align-items: start; }
@media (max-width: 48rem) { .workspace { grid-template-columns: 1fr; } }
.strip { display: grid; gap: var(--raavi-space-2); }
.strip ol { list-style: none; margin: 0; padding: 0; display: grid; gap: 0.25rem; max-block-size: 60vh; overflow: auto; }
.thumb { all: unset; box-sizing: border-box; inline-size: 100%; display: flex; gap: 0.5rem; align-items: center; padding: 0.4rem 0.5rem; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); cursor: pointer; }
.thumb:focus-visible { outline: 2px solid var(--raavi-primary, #2563eb); }
.thumb.on { border-color: var(--raavi-primary, #2563eb); background: var(--raavi-surface-muted, transparent); }
.num { font-weight: 700; min-inline-size: 1.5rem; text-align: center; }
.label { overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.stage { display: grid; gap: var(--raavi-space-3); max-inline-size: 60rem; }
.stage > :first-child { border: 1px solid var(--raavi-border); border-radius: 6px; }
.form { display: grid; gap: var(--raavi-space-2); }
.form textarea { font: inherit; padding: 0.5rem; }
.row { display: flex; gap: var(--raavi-space-2); align-items: center; flex-wrap: wrap; }
.spacer { flex: 1; }
.notes { margin: 0; }
.delete-deck { justify-self: start; }
.muted { color: var(--raavi-text-muted); margin: 0; }
.present { position: fixed; inset: 0; z-index: 2000; background: #0b0f14; display: grid; grid-template-rows: 1fr auto auto; place-items: center; padding: 1rem; gap: 0.5rem; }
.present-slide { inline-size: min(100%, calc((100vh - 8rem) * 16 / 9)); }
.present-notes { color: #cbd5e1; max-inline-size: 60rem; margin: 0; font-size: 0.95rem; }
.present-bar { display: flex; gap: 1rem; align-items: center; color: #e2e8f0; }
</style>
