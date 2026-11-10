<script setup lang="ts">
import { computed, nextTick, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import { contentRoute, wikiApi, type ContentListItem, type SpaceSummary } from '@/core/api/wiki'

/** Quick find (Ctrl/⌘+K): spaces and pages by title, already permission-filtered by the API. */
const visible = defineModel<boolean>('visible', { required: true })
const { t } = useI18n()
const router = useRouter()

const q = ref('')
const spaces = ref<SpaceSummary[]>([])
const content = ref<ContentListItem[]>([])
const active = ref(0)
const input = ref<{ $el: HTMLInputElement } | null>(null)
let timer: ReturnType<typeof setTimeout> | undefined
let seq = 0

type Item = { kind: 'space'; space: SpaceSummary } | { kind: 'content'; item: ContentListItem }
const items = computed<Item[]>(() => [
  ...spaces.value.map((space) => ({ kind: 'space' as const, space })),
  ...content.value.map((item) => ({ kind: 'content' as const, item })),
])

watch(visible, async (open) => {
  if (!open) return
  q.value = ''
  spaces.value = []
  content.value = []
  active.value = 0
  await nextTick()
  input.value?.$el?.focus()
})

watch(q, (value) => {
  clearTimeout(timer)
  timer = setTimeout(async () => {
    const mine = ++seq
    if (!value.trim()) {
      spaces.value = []
      content.value = []
      return
    }
    const r = await wikiApi.quickFind(value.trim()).catch(() => ({ spaces: [], content: [] }))
    if (mine !== seq) return
    spaces.value = r.spaces
    content.value = r.content
    active.value = 0
  }, 200)
})

async function textSearch() {
  const text = q.value.trim()
  visible.value = false
  await router.push({ name: 'search', query: text ? { q: text } : {} })
}

async function open(item: Item | undefined) {
  if (!item) {
    // Enter with no title match: look in the page TEXT instead
    if (q.value.trim().length >= 2) await textSearch()
    return
  }
  visible.value = false
  if (item.kind === 'space') await router.push({ name: 'space', params: { key: item.space.key } })
  else await router.push(contentRoute(item.item))
}

function onKey(e: KeyboardEvent) {
  if (e.key === 'ArrowDown') {
    active.value = Math.min(items.value.length - 1, active.value + 1)
    e.preventDefault()
  } else if (e.key === 'ArrowUp') {
    active.value = Math.max(0, active.value - 1)
    e.preventDefault()
  } else if (e.key === 'Enter') {
    void open(items.value[active.value])
    e.preventDefault()
  }
}
</script>

<template>
  <Dialog v-model:visible="visible" modal dismissable-mask :show-header="false" position="top" :style="{ inlineSize: 'min(36rem, 95vw)' }" content-class="palette">
    <label for="quick-find" class="sr-only">{{ t('wiki.quickFind.label') }}</label>
    <InputText
      id="quick-find"
      ref="input"
      v-model="q"
      :placeholder="t('wiki.quickFind.placeholder')"
      autocomplete="off"
      role="combobox"
      aria-controls="quick-find-results"
      :aria-expanded="items.length > 0"
      :aria-activedescendant="items.length ? `qf-${active}` : undefined"
      fluid
      @keydown="onKey"
    />
    <ul id="quick-find-results" class="results" role="listbox" :aria-label="t('wiki.quickFind.label')">
      <li
        v-for="(it, i) in items"
        :id="`qf-${i}`"
        :key="it.kind === 'space' ? `s-${it.space.id}` : `c-${it.item.id}`"
        role="option"
        :aria-selected="i === active"
        :class="{ active: i === active }"
        @mousedown.prevent="open(it)"
        @mousemove="active = i"
      >
        <template v-if="it.kind === 'space'">
          <i class="pi pi-th-large" aria-hidden="true" />
          <span class="title">{{ it.space.name }}</span>
          <small class="muted">{{ t('wiki.quickFind.space') }} · {{ it.space.key }}</small>
        </template>
        <template v-else>
          <i :class="it.item.contentType === 'folder' ? 'pi pi-folder' : 'pi pi-file'" aria-hidden="true" />
          <span class="title">{{ it.item.title }}</span>
          <small class="muted">{{ it.item.spaceName }}</small>
        </template>
      </li>
      <li v-if="q.trim() && items.length === 0" class="muted empty">{{ t('wiki.quickFind.none') }}</li>
      <li v-if="q.trim().length >= 2" class="text-search" role="option" aria-selected="false" data-testid="palette-text-search" @mousedown.prevent="textSearch">
        <i class="pi pi-sparkles" aria-hidden="true" />
        <span class="title">{{ t('wiki.quickFind.textSearch', { q: q.trim() }) }}</span>
        <small class="muted">{{ t('smartSearch.nav') }}</small>
      </li>
    </ul>
    <p class="hint muted">{{ t('wiki.quickFind.hint') }}</p>
  </Dialog>
</template>

<style scoped>
.results { list-style: none; margin: var(--raavi-space-2) 0 0; padding: 0; max-block-size: 22rem; overflow: auto; }
.results li { display: flex; align-items: center; gap: var(--raavi-space-2); padding: var(--raavi-space-2); border-radius: var(--raavi-radius-sm); cursor: pointer; }
.results li.active { background: var(--p-primary-50, rgba(0, 0, 0, 0.05)); }
.title { flex: 1; overflow-wrap: anywhere; }
.muted { color: var(--raavi-text-muted); }
.empty { cursor: default; }
.text-search { border-block-start: 1px solid var(--raavi-border); margin-block-start: var(--raavi-space-1); }
.hint { margin: var(--raavi-space-2) 0 0; font-size: 0.8rem; }
.sr-only { position: absolute; inline-size: 1px; block-size: 1px; overflow: hidden; clip-path: inset(50%); white-space: nowrap; }
</style>
