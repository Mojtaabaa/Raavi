<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, shallowRef, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { Editor, EditorContent } from '@tiptap/vue-3'
import Button from 'primevue/button'
import Select from 'primevue/select'
import Dialog from 'primevue/dialog'
import InputText from 'primevue/inputtext'
import Message from 'primevue/message'
import { useToast } from 'primevue/usetoast'
import { contentHref, wikiApi, type RaaviDoc, type SpaceSummary, type TreeNode } from '@/core/api/wiki'
import { errorMessage } from '@/core/api/errors'
import PageTree from '@/modules/wiki/components/PageTree.vue'
import { SCHEMA_VERSION } from '../registry'
import { createEditorRegistry, isSafeHref, setUploadHook } from '../setup'
import { ensurePathRewrites, rewritePathHref } from '../pathRewrites'
import { startLiveSession, type CollaborationTarget, type LiveSession } from '../collaboration'
import { CommentAnchors, commentAnchorsKey, flatten, quoteRange, type AnchorHighlight, type TextQuote } from '../extensions/commentAnchors'
import { PromptVariables, refreshVariableMarks } from '../extensions/promptVariables'
import '../content.css'

/**
 * Raavi rich-text editor (Tiptap 3, open-source extensions only) and read view. The document is the canonical
 * Raavi Document JSON envelope; the server re-validates it on save. Read mode uses the same schema, so what is saved
 * is exactly what is shown.
 */
const props = withDefaults(
  defineProps<{
    modelValue: RaaviDoc | null
    editable?: boolean
    contentId?: string | null
    placeholder?: string
    collaboration?: CollaborationTarget | null
    /** Small editor without the toolbar (comments): formatting via shortcuts, @mentions, links. */
    compact?: boolean
    /** Read mode: inline comment anchors to highlight (ADR-024). */
    anchors?: AnchorHighlight[] | null
    /** Prompt Hub: draw {{variables}} as chips; the function returns the names that are known (others are marked as unknown). */
    variableNames?: (() => Set<string>) | null
    activeAnchor?: string | null
    label?: string
  }>(),
  { editable: false, contentId: null, placeholder: undefined, collaboration: null, compact: false, anchors: null, activeAnchor: null, label: undefined },
)
export type EditorTextSelection = { text: string; from: number; to: number }
const emit = defineEmits<{
  'update:modelValue': [doc: RaaviDoc]
  reset: []
  revoked: []
  quote: [quote: TextQuote | null]
  activate: [id: string]
  located: [ids: string[]]
  selectionText: [sel: EditorTextSelection | null]
}>()
const { t, te, locale } = useI18n()
const toast = useToast()
const router = useRouter()

const editor = shallowRef<Editor | null>(null)
const incompatible = ref<string[]>([])
const linkOpen = ref(false)
const linkHref = ref('')
const linkSpaces = ref<SpaceSummary[]>([])
const linkSpaceKey = ref<string | null>(null)
const linkPage = ref<TreeNode | null>(null)
const fileInput = ref<HTMLInputElement | null>(null)
const pendingKind = ref<'image' | 'file'>('image')
const uploading = ref(false)
const tick = ref(0) // re-evaluates toolbar state on selection changes
const colorOpen = ref(false)
const bgOpen = ref(false)

/** Safe hex palette for text and background (schema allows only #RRGGBB). */
const TEXT_COLORS = ['#111827', '#dc2626', '#ea580c', '#ca8a04', '#16a34a', '#0891b2', '#2563eb', '#7c3aed', '#db2777']
const BG_COLORS = ['#fef08a', '#fecaca', '#bbf7d0', '#bae6fd', '#e9d5ff', '#fce7f3', '#e5e7eb', '#ffedd5', '#ffffff']
const FONT_FAMILIES = [
  { value: '', labelKey: 'editor.toolbar.fontDefault' },
  { value: 'Vazirmatn', labelKey: 'editor.toolbar.fontVazirmatn' },
  { value: 'Inter', labelKey: 'editor.toolbar.fontInter' },
  { value: 'Georgia', labelKey: 'editor.toolbar.fontGeorgia' },
  { value: 'Tahoma', labelKey: 'editor.toolbar.fontTahoma' },
  { value: 'ui-monospace', labelKey: 'editor.toolbar.fontMono' },
]
const FONT_SIZES = ['', '12px', '14px', '16px', '18px', '20px', '24px', '32px'] as const

// The toolbar stays in view while a long document scrolls (sticky). Its real height (it wraps on narrow screens) is the offset of the table tools below it.
const toolbarEl = ref<HTMLElement | null>(null)
const toolbarHeight = ref(0)
let toolbarObserver: ResizeObserver | null = null
watch(toolbarEl, (el) => {
  toolbarObserver?.disconnect()
  toolbarObserver = null
  if (!el) return
  toolbarHeight.value = Math.round(el.getBoundingClientRect().height)
  if (typeof ResizeObserver !== 'undefined') {
    toolbarObserver = new ResizeObserver(() => (toolbarHeight.value = Math.round(el.getBoundingClientRect().height)))
    toolbarObserver.observe(el)
  }
})
onBeforeUnmount(() => toolbarObserver?.disconnect())

const live: LiveSession | null = props.collaboration ? startLiveSession(props.collaboration) : null
watch(
  () => live?.status.value,
  (status) => {
    if (status === 'reset') emit('reset')
    if (status === 'revoked') {
      editor.value?.setEditable(false)
      emit('revoked')
    }
  },
)

const registry = createEditorRegistry({
  t: (k) => t(k),
  locale: String(locale.value),
  collaborative: !!live,
  placeholder: props.placeholder,
  searchUsers: async (q) => (await wikiApi.directory(q).catch(() => [])).filter((d) => d.kind === 'User').map((d) => ({ id: d.id, label: d.displayName, detail: d.detail })),
})

const envelope = (json: Record<string, unknown>): RaaviDoc => ({ schema: 'raavi.doc', schemaVersion: SCHEMA_VERSION, doc: json as RaaviDoc['doc'] })

onMounted(async () => {
  void ensurePathRewrites()
  editor.value = new Editor({
    // Live mode: content comes from the shared Yjs document (never from props), history from Yjs.
    extensions: [
      ...registry.extensions({ views: true }),
      ...(live?.extensions ?? []),
      ...(props.anchors ? [CommentAnchors.configure({ onLocated: (ids) => emit('located', ids) })] : []),
      ...(props.variableNames ? [PromptVariables.configure({ known: props.variableNames })] : []),
    ],
    ...(live ? {} : { content: props.modelValue?.doc ?? null }),
    editable: props.editable,
    editorProps: { attributes: { class: props.compact ? 'raavi-doc compact' : 'raavi-doc', dir: 'auto', ...(props.editable ? { 'aria-label': props.label ?? t('editor.label'), 'aria-multiline': 'true' } : {}) } },
    onUpdate: ({ editor: e }) => emit('update:modelValue', envelope(e.getJSON())),
    onSelectionUpdate: () => {
      tick.value++
      emitEditableSelection()
    },
    onTransaction: () => tick.value++,
  })
  if (props.editable) {
    setUploadHook(props.contentId ? (kind) => pickFile(kind) : undefined)
    // The registry must match the server's; otherwise saving could lose content, so editing is refused.
    try {
      incompatible.value = registry.verify(await wikiApi.editorSchema())
    } catch {
      incompatible.value = []
    }
    if (incompatible.value.length) editor.value?.setEditable(false)
  }
  pushAnchors()
})

// ── Inline comment anchors (read mode) ───────────────────────────────────────────────────────
function pushAnchors() {
  const e = editor.value
  if (!e || !props.anchors) return
  e.view.dispatch(e.state.tr.setMeta(commentAnchorsKey, { anchors: props.anchors, active: props.activeAnchor }))
}
watch(() => [props.anchors, props.activeAnchor], pushAnchors, { deep: true })

/** Clicking a highlighted passage focuses its thread; clicking a link navigates (openOnClick is off). */
function onContentClick(e: MouseEvent) {
  const el = e.target as HTMLElement | null
  const id = el?.closest?.('[data-thread-id]')?.getAttribute('data-thread-id')
  if (id && props.anchors) emit('activate', id)
  const anchor = el?.closest?.('a[href]') as HTMLAnchorElement | null
  if (!anchor || !editor.value?.view.dom.contains(anchor)) return
  const href = anchor.getAttribute('href')
  if (!href || !isSafeHref(href)) return
  e.preventDefault()
  e.stopPropagation()
  const rewritten = rewritePathHref(href)
  if (rewritten) {
    window.open(rewritten, '_blank', 'noopener,noreferrer')
    return
  }
  if (href.startsWith('/') && !href.startsWith('//')) {
    void router.push(href)
    return
  }
  if (href.startsWith('#')) {
    document.getElementById(href.slice(1))?.scrollIntoView?.({ block: 'start' })
    return
  }
  window.open(href, '_blank', 'noopener,noreferrer')
}

/** A read-only view never receives ProseMirror selection updates, so the DOM selection is mapped by hand. */
function emitQuote() {
  const e = editor.value
  if (!e || props.editable || !props.anchors) return
  const sel = window.getSelection()
  if (!sel || sel.isCollapsed || !sel.anchorNode || !sel.focusNode || !e.view.dom.contains(sel.anchorNode) || !e.view.dom.contains(sel.focusNode)) {
    emit('quote', null)
    return
  }
  try {
    const a = e.view.posAtDOM(sel.anchorNode, sel.anchorOffset)
    const b = e.view.posAtDOM(sel.focusNode, sel.focusOffset)
    emit('quote', quoteRange(flatten(e.state.doc), Math.min(a, b), Math.max(a, b)))
  } catch {
    emit('quote', null)
  }
}

function emitEditableSelection() {
  const e = editor.value
  if (!e || !props.editable) return
  const { from, to, empty } = e.state.selection
  if (empty || to - from > 8000) {
    emit('selectionText', null)
    return
  }
  const text = e.state.doc.textBetween(from, to, '\n').trim()
  emit('selectionText', text.length > 0 ? { text, from, to } : null)
}

/** Replace a stored range with plain text (editor AI apply → draft autosave). */
function replaceRange(from: number, to: number, text: string) {
  const e = editor.value
  if (!e || !props.editable) return
  e.chain().focus().insertContentAt({ from, to }, text).run()
}

onBeforeUnmount(() => {
  if (props.editable) setUploadHook(undefined)
  editor.value?.destroy()
  live?.destroy()
})

// External replacement (e.g. template chosen) — never echo our own updates back into the editor.
watch(
  () => props.modelValue,
  (doc) => {
    const e = editor.value
    if (!e || !doc || live) return
    if (JSON.stringify(e.getJSON()) !== JSON.stringify(doc.doc)) e.commands.setContent(doc.doc, { emitUpdate: false })
  },
)

const active = (name: string, attrs?: Record<string, unknown>) => {
  void tick.value
  return editor.value?.isActive(name, attrs) ?? false
}

const blockOptions = computed(() => [
  { value: 'p', label: t('editor.toolbar.paragraph') },
  ...[1, 2, 3, 4].map((l) => ({ value: `h${l}`, label: t('editor.toolbar.heading', { level: l }) })),
])
const blockType = computed({
  get: () => {
    void tick.value
    for (const l of [1, 2, 3, 4]) if (editor.value?.isActive('heading', { level: l })) return `h${l}`
    return 'p'
  },
  set: (v: string) => {
    const chain = editor.value?.chain().focus()
    if (!chain) return
    if (v === 'p') chain.setParagraph().run()
    else chain.setNode('heading', { level: Number(v.slice(1)) }).run()
  },
})

/** History commands come from UndoRedo (local) or Collaboration (live); either may be absent. */
function can(command: 'undo' | 'redo'): boolean {
  void tick.value
  const checks = editor.value?.can() as unknown as Record<string, (() => boolean) | undefined> | undefined
  return !!checks?.[command]?.()
}

function run(fn: (e: Editor) => void) {
  if (editor.value) fn(editor.value)
}

function setDirection(dir: 'ltr' | 'rtl' | null) {
  run((e) => {
    const type = e.isActive('heading') ? 'heading' : 'paragraph'
    e.chain().focus().updateAttributes(type, { dir }).run()
    // When inside a list item, set dir on every paragraph so CSS can flip bullet markers for RTL.
    const $from = e.state.selection.$from
    for (let depth = $from.depth; depth > 0; depth--) {
      const node = $from.node(depth)
      if (node.type.name !== 'listItem' && node.type.name !== 'taskItem') continue
      const pos = $from.before(depth)
      let tr = e.state.tr
      node.forEach((child, offset) => {
        if (child.type.name === 'paragraph' || child.type.name === 'heading') {
          tr = tr.setNodeMarkup(pos + 1 + offset, undefined, { ...child.attrs, dir })
        }
      })
      if (tr.docChanged) e.view.dispatch(tr)
      break
    }
  })
}

const currentDir = computed(() => {
  void tick.value
  const e = editor.value
  if (!e) return null as 'ltr' | 'rtl' | null
  const type = e.isActive('heading') ? 'heading' : 'paragraph'
  const dir = e.getAttributes(type).dir as string | null | undefined
  return dir === 'ltr' || dir === 'rtl' ? dir : null
})

function toggleList(kind: 'bulletList' | 'orderedList' | 'taskList') {
  run((e) => {
    const type = e.isActive('heading') ? 'heading' : 'paragraph'
    const existing = e.getAttributes(type).dir as string | null | undefined
    const preferRtl = String(locale.value).toLowerCase().startsWith('fa')
    const dir = existing === 'ltr' || existing === 'rtl' ? existing : preferRtl ? 'rtl' : null
    const chain = e.chain().focus()
    if (kind === 'bulletList') chain.toggleBulletList()
    else if (kind === 'orderedList') chain.toggleOrderedList()
    else chain.toggleTaskList()
    chain.run()
    if (dir) setDirection(dir)
  })
}

function sinkOrLift(sink: boolean) {
  run((e) => {
    if (sink) {
      if (e.can().sinkListItem('listItem')) e.chain().focus().sinkListItem('listItem').run()
      else if (e.can().sinkListItem('taskItem')) e.chain().focus().sinkListItem('taskItem').run()
      return
    }
    if (e.can().liftListItem('listItem')) e.chain().focus().liftListItem('listItem').run()
    else if (e.can().liftListItem('taskItem')) e.chain().focus().liftListItem('taskItem').run()
  })
}

function insertZwnj() {
  run((e) => e.chain().focus().insertContent('\u200C').run())
}

const currentColor = computed(() => {
  void tick.value
  return (editor.value?.getAttributes('textStyle').color as string | undefined) ?? null
})
const currentBg = computed(() => {
  void tick.value
  return (editor.value?.getAttributes('textStyle').backgroundColor as string | undefined)
    ?? (editor.value?.getAttributes('highlight').color as string | undefined)
    ?? null
})
const currentFont = computed({
  get: () => {
    void tick.value
    return (editor.value?.getAttributes('textStyle').fontFamily as string | undefined) ?? ''
  },
  set: (v: string) => {
    run((e) => {
      const chain = e.chain().focus()
      if (v) chain.setFontFamily(v).run()
      else chain.unsetFontFamily().run()
    })
  },
})
const currentSize = computed({
  get: () => {
    void tick.value
    return (editor.value?.getAttributes('textStyle').fontSize as string | undefined) ?? ''
  },
  set: (v: string) => {
    run((e) => {
      const chain = e.chain().focus()
      if (v) chain.setFontSize(v).run()
      else chain.unsetFontSize().run()
    })
  },
})

function applyTextColor(hex: string | null) {
  run((e) => {
    if (hex) e.chain().focus().setColor(hex).run()
    else e.chain().focus().unsetColor().run()
  })
  colorOpen.value = false
}

function applyBackgroundColor(hex: string | null) {
  run((e) => {
    if (hex) e.chain().focus().setBackgroundColor(hex).setHighlight({ color: hex }).run()
    else e.chain().focus().unsetBackgroundColor().unsetHighlight().run()
  })
  bgOpen.value = false
}

const fontOptions = computed(() => FONT_FAMILIES.map((f) => ({ value: f.value, label: t(f.labelKey) })))
const sizeOptions = computed(() =>
  FONT_SIZES.map((s) => ({ value: s, label: s ?? t('editor.toolbar.sizeDefault') })),
)

const charCount = computed(() => {
  void tick.value
  return editor.value?.storage.characterCount?.characters?.() ?? 0
})

async function openLink() {
  linkHref.value = (editor.value?.getAttributes('link').href as string | undefined) ?? ''
  linkPage.value = null
  linkOpen.value = true
  void ensurePathRewrites()
  if (!linkSpaces.value.length) {
    linkSpaces.value = await wikiApi.spaces().catch(() => [])
  }
  if (!linkSpaceKey.value && linkSpaces.value[0]) linkSpaceKey.value = linkSpaces.value[0].key
}

watch(linkSpaceKey, () => (linkPage.value = null))

function pickLinkPage(node: TreeNode) {
  if (node.contentType !== 'page' || !linkSpaceKey.value) return
  linkPage.value = node
  linkHref.value = contentHref({ id: node.id, spaceKey: linkSpaceKey.value })
}

const linkValid = computed(() => linkHref.value === '' || isSafeHref(linkHref.value))
function applyLink() {
  run((e) => {
    if (!linkHref.value) e.chain().focus().extendMarkRange('link').unsetLink().run()
    else if (isSafeHref(linkHref.value)) e.chain().focus().extendMarkRange('link').setLink({ href: linkHref.value }).run()
  })
  linkOpen.value = false
}

function pickFile(kind: 'image' | 'file') {
  pendingKind.value = kind
  if (fileInput.value) {
    fileInput.value.accept = kind === 'image' ? 'image/png,image/jpeg,image/gif,image/webp' : ''
    fileInput.value.click()
  }
}

async function onFile(e: Event) {
  const input = e.target as HTMLInputElement
  const file = input.files?.[0]
  input.value = ''
  if (!file || !props.contentId) return
  uploading.value = true
  try {
    const a = await wikiApi.upload(props.contentId, file)
    const node = a.isImage && pendingKind.value === 'image'
      ? { type: 'image', attrs: { attachmentId: a.id, alt: a.fileName, width: null } }
      : { type: 'attachment', attrs: { attachmentId: a.id, fileName: a.fileName, size: a.size, mediaType: a.mediaType } }
    editor.value?.chain().focus().insertContent(node).run()
  } catch (err) {
    toast.add({ severity: 'error', summary: errorMessage(err, t, te), life: 6000 })
  } finally {
    uploading.value = false
  }
}

/** Inserts text at the cursor (used by the prompt variable menu). */
function insertText(text: string) {
  editor.value?.chain().focus().insertContent(text).run()
}
defineExpose({
  editor,
  insertText,
  replaceRange,
  refreshVariables: () => editor.value && refreshVariableMarks(editor.value as never),
})
</script>

<template>
  <div :class="['raavi-editor', { editing: editable, compact }]" :style="{ '--raavi-toolbar-h': `${toolbarHeight}px` }">
    <Message v-if="incompatible.length" severity="error" :closable="false" data-testid="editor-incompatible">{{ t('editor.incompatible') }}</Message>
    <Message v-if="live?.status.value === 'revoked'" severity="error" :closable="false" data-testid="live-revoked">{{ t('editor.live.revokedNotice') }}</Message>
    <Message v-else-if="live?.status.value === 'offline'" severity="warn" :closable="false">{{ t('editor.live.offlineNotice') }}</Message>

    <div v-if="editable && !compact && editor && !incompatible.length" ref="toolbarEl" class="toolbar" role="toolbar" :aria-label="t('editor.toolbar.label')">
      <div class="group">
        <Button text size="small" icon="pi pi-undo" class="flip" :aria-label="t('editor.toolbar.undo')" :disabled="!can('undo')" @click="run((e) => e.chain().focus().undo().run())" />
        <Button text size="small" icon="pi pi-refresh" :aria-label="t('editor.toolbar.redo')" :disabled="!can('redo')" @click="run((e) => e.chain().focus().redo().run())" />
      </div>
      <Select v-model="blockType" :options="blockOptions" option-label="label" option-value="value" size="small" class="block-type" :aria-label="t('editor.toolbar.blockType')" />
      <div class="group">
        <Button text size="small" :class="{ on: active('bold') }" :aria-pressed="active('bold')" :aria-label="t('editor.toolbar.bold')" @click="run((e) => e.chain().focus().toggleBold().run())"><strong>B</strong></Button>
        <Button text size="small" :class="{ on: active('italic') }" :aria-pressed="active('italic')" :aria-label="t('editor.toolbar.italic')" @click="run((e) => e.chain().focus().toggleItalic().run())"><em>I</em></Button>
        <Button text size="small" :class="{ on: active('underline') }" :aria-pressed="active('underline')" :aria-label="t('editor.toolbar.underline')" @click="run((e) => e.chain().focus().toggleUnderline().run())"><u>U</u></Button>
        <Button text size="small" :class="{ on: active('strike') }" :aria-pressed="active('strike')" :aria-label="t('editor.toolbar.strike')" @click="run((e) => e.chain().focus().toggleStrike().run())"><s>S</s></Button>
        <Button text size="small" icon="pi pi-code" :class="{ on: active('code') }" :aria-pressed="active('code')" :aria-label="t('editor.toolbar.code')" @click="run((e) => e.chain().focus().toggleCode().run())" />
        <Button text size="small" icon="pi pi-link" :class="{ on: active('link') }" :aria-label="t('editor.toolbar.link')" @click="openLink" />
        <Button text size="small" :class="{ on: active('subscript') }" :aria-label="t('editor.toolbar.subscript')" @click="run((e) => e.chain().focus().toggleSubscript().run())"><span class="subsup">X<sub>2</sub></span></Button>
        <Button text size="small" :class="{ on: active('superscript') }" :aria-label="t('editor.toolbar.superscript')" @click="run((e) => e.chain().focus().toggleSuperscript().run())"><span class="subsup">X<sup>2</sup></span></Button>
        <div class="swatch-wrap">
          <Button text size="small" icon="pi pi-palette" :class="{ on: !!currentColor }" :aria-label="t('editor.toolbar.textColor')" :aria-expanded="colorOpen" @click="colorOpen = !colorOpen; bgOpen = false" />
          <div v-if="colorOpen" class="swatches" role="listbox" :aria-label="t('editor.toolbar.textColor')">
            <button type="button" class="swatch clear" :aria-label="t('editor.toolbar.colorClear')" @click="applyTextColor(null)">×</button>
            <button v-for="c in TEXT_COLORS" :key="c" type="button" class="swatch" :style="{ background: c }" :aria-label="c" :aria-pressed="currentColor === c" @click="applyTextColor(c)" />
          </div>
        </div>
        <div class="swatch-wrap">
          <Button text size="small" icon="pi pi-pencil" :class="{ on: !!currentBg || active('highlight') }" :aria-label="t('editor.toolbar.backgroundColor')" :aria-expanded="bgOpen" @click="bgOpen = !bgOpen; colorOpen = false" />
          <div v-if="bgOpen" class="swatches" role="listbox" :aria-label="t('editor.toolbar.backgroundColor')">
            <button type="button" class="swatch clear" :aria-label="t('editor.toolbar.colorClear')" @click="applyBackgroundColor(null)">×</button>
            <button v-for="c in BG_COLORS" :key="c" type="button" class="swatch" :style="{ background: c }" :aria-label="c" :aria-pressed="currentBg === c" @click="applyBackgroundColor(c)" />
          </div>
        </div>
      </div>
      <Select v-model="currentFont" :options="fontOptions" option-label="label" option-value="value" size="small" class="font-family" :aria-label="t('editor.toolbar.fontFamily')" />
      <Select v-model="currentSize" :options="sizeOptions" option-label="label" option-value="value" size="small" class="font-size" :aria-label="t('editor.toolbar.fontSize')" />
      <div class="group">
        <Button text size="small" icon="pi pi-align-left" class="logical-start" :class="{ on: active('paragraph', { textAlign: 'start' }) }" :aria-label="t('editor.toolbar.alignStart')" @click="run((e) => e.chain().focus().setTextAlign('start').run())" />
        <Button text size="small" icon="pi pi-align-center" :class="{ on: active('paragraph', { textAlign: 'center' }) }" :aria-label="t('editor.toolbar.alignCenter')" @click="run((e) => e.chain().focus().setTextAlign('center').run())" />
        <Button text size="small" icon="pi pi-align-right" class="logical-end" :class="{ on: active('paragraph', { textAlign: 'end' }) }" :aria-label="t('editor.toolbar.alignEnd')" @click="run((e) => e.chain().focus().setTextAlign('end').run())" />
        <Button text size="small" icon="pi pi-align-justify" :class="{ on: active('paragraph', { textAlign: 'justify' }) }" :aria-label="t('editor.toolbar.alignJustify')" @click="run((e) => e.chain().focus().setTextAlign('justify').run())" />
        <Button text size="small" :class="{ on: currentDir === null }" :aria-pressed="currentDir === null" :aria-label="t('editor.toolbar.auto')" @click="setDirection(null)">{{ t('editor.toolbar.dirAutoShort') }}</Button>
        <Button text size="small" :class="{ on: currentDir === 'ltr' }" :aria-pressed="currentDir === 'ltr'" :aria-label="t('editor.toolbar.ltr')" @click="setDirection('ltr')">{{ t('editor.toolbar.dirLtrShort') }}</Button>
        <Button text size="small" :class="{ on: currentDir === 'rtl' }" :aria-pressed="currentDir === 'rtl'" :aria-label="t('editor.toolbar.rtl')" @click="setDirection('rtl')">{{ t('editor.toolbar.dirRtlShort') }}</Button>
        <Button text size="small" :aria-label="t('editor.toolbar.zwnj')" :title="t('editor.toolbar.zwnjHint')" data-testid="insert-zwnj" @click="insertZwnj">{{ t('editor.toolbar.zwnjShort') }}</Button>
      </div>
      <div class="group">
        <Button text size="small" icon="pi pi-list" :class="{ on: active('bulletList') }" :aria-label="t('editor.slash.bulletList')" @click="toggleList('bulletList')" />
        <Button text size="small" icon="pi pi-sort-numeric-down" :class="{ on: active('orderedList') }" :aria-label="t('editor.slash.orderedList')" @click="toggleList('orderedList')" />
        <Button text size="small" icon="pi pi-check-square" :class="{ on: active('taskList') }" :aria-label="t('editor.slash.taskList')" @click="toggleList('taskList')" />
        <Button text size="small" icon="pi pi-indent" :aria-label="t('editor.toolbar.indent')" @click="sinkOrLift(true)" />
        <Button text size="small" icon="pi pi-indent" class="outdent" :aria-label="t('editor.toolbar.outdent')" @click="sinkOrLift(false)" />
        <Button text size="small" icon="pi pi-comment" :class="{ on: active('blockquote') }" :aria-label="t('editor.slash.quote')" @click="run((e) => e.chain().focus().toggleBlockquote().run())" />
        <Button text size="small" icon="pi pi-desktop" :class="{ on: active('codeBlock') }" :aria-label="t('editor.slash.codeBlock')" @click="run((e) => e.chain().focus().toggleCodeBlock().run())" />
        <Button text size="small" icon="pi pi-table" :aria-label="t('editor.slash.table')" @click="run((e) => e.chain().focus().insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run())" />
        <template v-if="contentId">
          <Button text size="small" icon="pi pi-image" :loading="uploading && pendingKind === 'image'" :aria-label="t('editor.slash.image')" @click="pickFile('image')" />
          <Button text size="small" icon="pi pi-paperclip" :loading="uploading && pendingKind === 'file'" :aria-label="t('editor.slash.file')" @click="pickFile('file')" />
        </template>
      </div>
      <span v-if="live" class="live" role="status" data-testid="live-status">
        <span :class="['dot', live.status.value]" aria-hidden="true" />
        {{ t(`editor.live.${live.status.value}`) }}
        <span v-for="p in live.peers.value" :key="p.clientId" class="peer" :style="{ borderColor: p.color }" :title="p.name">{{ p.name.slice(0, 1) }}</span>
      </span>
      <span class="hint">{{ t('editor.slashHint') }}</span>
      <span class="chars" aria-live="polite">{{ t('editor.toolbar.characters', { n: charCount }) }}</span>
    </div>

    <div v-if="editable && editor && active('table')" class="table-tools" role="toolbar" :aria-label="t('editor.table.label')">
      <Button text size="small" :label="t('editor.table.addRow')" @click="run((e) => e.chain().focus().addRowAfter().run())" />
      <Button text size="small" :label="t('editor.table.addColumn')" @click="run((e) => e.chain().focus().addColumnAfter().run())" />
      <Button text size="small" :label="t('editor.table.deleteRow')" @click="run((e) => e.chain().focus().deleteRow().run())" />
      <Button text size="small" :label="t('editor.table.deleteColumn')" @click="run((e) => e.chain().focus().deleteColumn().run())" />
      <Button text size="small" :label="t('editor.table.toggleHeader')" @click="run((e) => e.chain().focus().toggleHeaderRow().run())" />
      <Button text size="small" severity="danger" :label="t('editor.table.delete')" @click="run((e) => e.chain().focus().deleteTable().run())" />
    </div>

    <EditorContent v-if="editor" :editor="editor" class="content" :data-testid="compact ? 'comment-editor' : 'document-body'" @mouseup="emitQuote" @keyup="emitQuote" @click="onContentClick" />
    <input ref="fileInput" type="file" class="file-input" tabindex="-1" aria-hidden="true" @change="onFile" />

    <Dialog v-model:visible="linkOpen" modal :header="t('editor.link.title')" :style="{ inlineSize: 'min(36rem, 95vw)' }">
      <form class="link-form" @submit.prevent="applyLink">
        <label for="link-space">{{ t('editor.link.page') }}</label>
        <Select
          id="link-space"
          v-model="linkSpaceKey"
          :options="linkSpaces"
          option-label="name"
          option-value="key"
          filter
          :placeholder="t('editor.link.space')"
        />
        <div v-if="linkSpaceKey" class="tree-box" :aria-label="t('editor.link.pickPage')">
          <PageTree :space-key="linkSpaceKey" :selected-id="linkPage?.id" @select="pickLinkPage" />
        </div>
        <label for="link-href">{{ t('editor.link.url') }}</label>
        <InputText id="link-href" v-model="linkHref" dir="ltr" :invalid="!linkValid" placeholder="https://" />
        <small v-if="!linkValid" class="error">{{ t('editor.link.invalid') }}</small>
        <div class="row">
          <Button type="submit" :label="t('common.save')" :disabled="!linkValid" />
          <Button v-if="active('link')" type="button" text severity="danger" :label="t('editor.link.remove')" @click="(linkHref = ''), applyLink()" />
        </div>
      </form>
    </Dialog>
  </div>
</template>

<style scoped>
.raavi-editor.editing { border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); background: var(--raavi-surface, #fff); }
.table-tools { inset-block-start: calc(var(--raavi-editor-top, var(--raavi-header-offset, var(--raavi-header-height))) + var(--raavi-toolbar-h, 0px)) !important; z-index: 4 !important; }
.toolbar, .table-tools { position: sticky; inset-block-start: var(--raavi-editor-top, var(--raavi-header-offset, var(--raavi-header-height))); z-index: 5; display: flex; flex-wrap: wrap; align-items: center; gap: var(--raavi-space-1) var(--raavi-space-2); padding: var(--raavi-space-1) var(--raavi-space-2); background: var(--raavi-surface, #fff); border-block-end: 1px solid var(--raavi-border); }
.table-tools { inset-block-start: calc(var(--raavi-header-height) + 2.6rem); background: var(--p-primary-50, #f5f3ff); }
.group { display: flex; gap: 0; border-inline-end: 1px solid var(--raavi-border); padding-inline-end: var(--raavi-space-1); }
.toolbar :deep(.on) { background: var(--p-primary-100, rgba(0, 0, 0, 0.08)); }
.block-type { min-inline-size: 9rem; }
.font-family { min-inline-size: 8rem; }
.font-size { min-inline-size: 5.5rem; }
.hint, .chars { color: var(--raavi-text-muted); font-size: 0.8rem; }
.chars { margin-inline-start: auto; }
.subsup { font-size: 0.85em; line-height: 1; }
.swatch-wrap { position: relative; }
.swatches {
  position: absolute; inset-block-start: 100%; inset-inline-start: 0; z-index: 6;
  display: grid; grid-template-columns: repeat(5, 1.4rem); gap: 4px;
  padding: var(--raavi-space-2); background: var(--raavi-surface, #fff);
  border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm);
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.08);
}
.swatch {
  inline-size: 1.4rem; block-size: 1.4rem; border-radius: 3px; border: 1px solid var(--raavi-border);
  padding: 0; cursor: pointer;
}
.swatch.clear { display: grid; place-items: center; background: var(--raavi-surface, #fff); font-size: 0.85rem; line-height: 1; }
.swatch[aria-pressed='true'] { outline: 2px solid var(--p-primary-color); outline-offset: 1px; }
.live { display: inline-flex; align-items: center; gap: 6px; font-size: 0.8rem; color: var(--raavi-text-muted); margin-inline-start: auto; }
.dot { inline-size: 8px; block-size: 8px; border-radius: 50%; background: #9ca3af; }
.dot.live { background: #16a34a; }
.dot.offline, .dot.revoked { background: #dc2626; }
.peer { display: inline-grid; place-items: center; inline-size: 1.4rem; block-size: 1.4rem; border: 2px solid; border-radius: 50%; font-size: 0.7rem; font-weight: 700; color: var(--raavi-text); }
.editing .content { padding: var(--raavi-space-3) var(--raavi-space-4); min-block-size: 20rem; }
.editing.compact .content { padding: var(--raavi-space-1) var(--raavi-space-2); min-block-size: 0; }
.file-input { display: none; }
.link-form { display: grid; gap: var(--raavi-space-2); }
.tree-box { max-block-size: 14rem; overflow: auto; border: 1px solid var(--raavi-border); border-radius: var(--raavi-radius-sm); padding: var(--raavi-space-2); }
.row { display: flex; gap: var(--raavi-space-2); }
.error { color: #b91c1c; }
:dir(rtl) .flip :deep(.pi) { transform: scaleX(-1); }
:dir(rtl) .logical-start :deep(.pi),
:dir(rtl) .logical-end :deep(.pi) { transform: scaleX(-1); }
.outdent :deep(.pi) { transform: scaleX(-1); }
</style>
