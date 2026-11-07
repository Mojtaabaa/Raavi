import type { Component } from 'vue'
import { Extension, type AnyExtension, type Editor, type Range } from '@tiptap/core'
import StarterKit from '@tiptap/starter-kit'
import Link from '@tiptap/extension-link'
import TextAlign from '@tiptap/extension-text-align'
import { Color, TextStyle } from '@tiptap/extension-text-style'
import Highlight from '@tiptap/extension-highlight'
import { TaskItem, TaskList } from '@tiptap/extension-list'
import { TableKit } from '@tiptap/extension-table'
import Mention from '@tiptap/extension-mention'
import { Placeholder } from '@tiptap/extensions'
import { EditorRegistry, manifest, type Registration, type SlashItem } from './registry'
import { manifestNode } from './extensions/manifestNode'
import { floatingRenderer, SlashCommand } from './suggestions'
import type { SuggestionEntry } from './types'
import PanelView from './views/PanelView.vue'
import ExpandView from './views/ExpandView.vue'
import StatusView from './views/StatusView.vue'
import TocView from './views/TocView.vue'
import SourceRenderView from './views/SourceRenderView.vue'
import ImageView from './views/ImageView.vue'
import AttachmentView from './views/AttachmentView.vue'
import UnsupportedView from './views/UnsupportedView.vue'
import ChildrenView from './views/ChildrenView.vue'
import ContentByLabelView from './views/ContentByLabelView.vue'

/** Same allow-list as the server (`EditorSchema.IsSafeHref`). */
export function isSafeHref(href: string): boolean {
  if (!href || href !== href.trim() || /[\u0000-\u001f\u007f\\]/.test(href)) return false
  if (href.startsWith('/')) return !href.startsWith('//')
  if (href.startsWith('#')) return true
  try {
    return ['http:', 'https:', 'mailto:', 'raavi:'].includes(new URL(href).protocol)
  } catch {
    return false
  }
}

/** Per-paragraph/heading text direction (`dir`), rendered as `dir="auto"` by default for mixed Persian/English text. */
export const TextDirection = Extension.create({
  name: 'raaviTextDirection',
  addGlobalAttributes() {
    return [
      {
        types: ['paragraph', 'heading'],
        attributes: {
          dir: {
            default: null,
            parseHTML: (el: HTMLElement) => (['ltr', 'rtl'].includes(el.getAttribute('dir') ?? '') ? el.getAttribute('dir') : null),
            renderHTML: (attrs: Record<string, unknown>) => ({ dir: (attrs.dir as string | null) ?? 'auto' }),
          },
        },
      },
    ]
  },
})

/** Links keep only `href` in the document; rel/target are presentation (same as the server renderer). */
const SafeLink = Link.extend({
  addAttributes() {
    return { href: { default: null } }
  },
}).configure({
  openOnClick: false,
  autolink: true,
  defaultProtocol: 'https',
  HTMLAttributes: { rel: 'noopener noreferrer nofollow', target: '_blank' },
  isAllowedUri: (url) => isSafeHref(url),
  shouldAutoLink: (url) => isSafeHref(url),
})

export interface EditorEnvironment {
  t: (key: string) => string
  /** Users of the current workspace for @mentions. */
  searchUsers: (query: string) => Promise<{ id: string; label: string; detail: string | null }[]>
  placeholder?: string
  /** Real-time mode: Yjs owns history, so the local undo/redo extension is disabled (Collaboration provides it). */
  collaborative?: boolean
}

const del = (editor: Editor, range: Range) => editor.chain().focus().deleteRange(range)

function core(id: string, slash?: SlashItem[]): Registration {
  return { manifest: manifest(id), ...(slash ? { slash } : {}) }
}

function custom(id: string, view?: Component, slash?: SlashItem[], tag?: string): Registration {
  const built = manifestNode(manifest(id), { ...(view ? { view } : {}), ...(tag ? { tag } : {}) })
  return { manifest: manifest(id), tiptap: built.tiptap, headless: built.headless, ...(slash ? { slash } : {}) }
}

/** Builds the full registry: every manifest has exactly one registration (verified by tests and at runtime). */
export function createEditorRegistry(env?: EditorEnvironment): EditorRegistry {
  const r = new EditorRegistry()
  r.use(
    StarterKit.configure({ link: false, ...(env?.collaborative ? { undoRedo: false as const } : {}) }),
    SafeLink,
    TextDirection,
    TextAlign.configure({ types: ['heading', 'paragraph'], alignments: ['start', 'center', 'end', 'justify'] }),
    TextStyle,
    Color,
    Highlight.configure({ multicolor: true }),
    TaskList,
    TaskItem.configure({ nested: true }),
    TableKit.configure({ table: { resizable: true } }),
  )

  for (const id of ['core.doc', 'core.text', 'core.paragraph', 'core.listItem', 'core.taskItem', 'core.hardBreak', 'core.bold', 'core.italic', 'core.underline', 'core.strike', 'core.code', 'core.link', 'core.textStyle', 'core.highlight', 'raavi.tableRow', 'raavi.tableCell', 'raavi.tableHeader']) {
    r.register(core(id))
  }

  r.register(
    core('core.heading', [1, 2, 3].map((level) => ({
      labelKey: `editor.slash.heading${level}`,
      icon: 'pi pi-hashtag',
      keywords: ['heading', `h${level}`, 'عنوان', 'تیتر'],
      run: ({ editor, range }) => del(editor, range).setNode('heading', { level }).run(),
    }))),
  )
  r.register(core('core.bulletList', [{ labelKey: 'editor.slash.bulletList', icon: 'pi pi-list', keywords: ['bullet', 'list', 'فهرست', 'لیست'], run: ({ editor, range }) => del(editor, range).toggleBulletList().run() }]))
  r.register(core('core.orderedList', [{ labelKey: 'editor.slash.orderedList', icon: 'pi pi-sort-numeric-down', keywords: ['number', 'ordered', 'شماره'], run: ({ editor, range }) => del(editor, range).toggleOrderedList().run() }]))
  r.register(core('core.taskList', [{ labelKey: 'editor.slash.taskList', icon: 'pi pi-check-square', keywords: ['task', 'todo', 'check', 'کار', 'وظیفه'], run: ({ editor, range }) => del(editor, range).toggleTaskList().run() }]))
  r.register(core('core.blockquote', [{ labelKey: 'editor.slash.quote', icon: 'pi pi-comment', keywords: ['quote', 'نقل'], run: ({ editor, range }) => del(editor, range).toggleBlockquote().run() }]))
  r.register(core('core.codeBlock', [{ labelKey: 'editor.slash.codeBlock', icon: 'pi pi-code', keywords: ['code', 'کد'], run: ({ editor, range }) => del(editor, range).toggleCodeBlock().run() }]))
  r.register(core('core.horizontalRule', [{ labelKey: 'editor.slash.divider', icon: 'pi pi-minus', keywords: ['divider', 'rule', 'hr', 'جداکننده'], run: ({ editor, range }) => del(editor, range).setHorizontalRule().run() }]))
  r.register(core('raavi.table', [{ labelKey: 'editor.slash.table', icon: 'pi pi-table', keywords: ['table', 'جدول'], run: ({ editor, range }) => del(editor, range).insertTable({ rows: 3, cols: 3, withHeaderRow: true }).run() }]))

  const insert = (type: string, attrs: Record<string, unknown> = {}, withParagraph = false) => ({ editor, range }: { editor: Editor; range: Range }) =>
    del(editor, range).insertContent(withParagraph ? { type, attrs, content: [{ type: 'paragraph' }] } : { type, attrs }).run()

  r.register(custom('raavi.panel', PanelView, (['info', 'note', 'warning', 'success', 'error'] as const).map((type) => ({
    labelKey: `editor.slash.panel.${type}`,
    icon: 'pi pi-info-circle',
    keywords: ['panel', type, 'پنل', 'کادر'],
    run: insert('panel', { type }, true),
  }))))
  r.register(custom('raavi.expand', ExpandView, [{ labelKey: 'editor.slash.expand', icon: 'pi pi-angle-down', keywords: ['expand', 'collapse', 'details', 'بازشو'], run: insert('expand', { title: '' }, true) }]))
  r.register(custom('raavi.status', StatusView, [{ labelKey: 'editor.slash.status', icon: 'pi pi-tag', keywords: ['status', 'lozenge', 'وضعیت'], run: insert('status', { text: 'TODO', color: 'neutral' }) }], 'span'))
  r.register(custom('raavi.toc', TocView, [{ labelKey: 'editor.slash.toc', icon: 'pi pi-list', keywords: ['toc', 'contents', 'فهرست مطالب'], run: insert('toc', { maxLevel: 3 }) }]))
  r.register(custom('raavi.children', ChildrenView, [{ labelKey: 'editor.slash.children', icon: 'pi pi-sitemap', keywords: ['children', 'child', 'pages', 'زیرصفحه', 'فرزند'], run: insert('children', { depth: 1 }) }]))
  r.register(custom('raavi.contentByLabel', ContentByLabelView, [{ labelKey: 'editor.slash.contentByLabel', icon: 'pi pi-tags', keywords: ['label', 'labels', 'pages by label', 'برچسب'], run: insert('contentByLabel', { labels: '', maxItems: 20, currentSpaceOnly: false }) }]))
  r.register(custom('raavi.mermaid', SourceRenderView, [{ labelKey: 'editor.slash.mermaid', icon: 'pi pi-sitemap', keywords: ['mermaid', 'diagram', 'نمودار'], run: insert('mermaid', { source: '' }) }]))
  r.register(custom('raavi.mathInline', SourceRenderView, [{ labelKey: 'editor.slash.mathInline', icon: 'pi pi-percentage', keywords: ['math', 'latex', 'formula', 'فرمول'], run: insert('mathInline', { latex: '' }) }], 'span'))
  r.register(custom('raavi.mathBlock', SourceRenderView, [{ labelKey: 'editor.slash.mathBlock', icon: 'pi pi-percentage', keywords: ['math', 'latex', 'equation', 'معادله'], run: insert('mathBlock', { latex: '' }) }]))
  r.register(custom('raavi.image', ImageView, [{ labelKey: 'editor.slash.image', icon: 'pi pi-image', keywords: ['image', 'picture', 'تصویر', 'عکس'], needsContent: true, run: ({ editor, range, upload }) => { del(editor, range).run(); upload?.('image') } }]))
  r.register(custom('raavi.attachment', AttachmentView, [{ labelKey: 'editor.slash.file', icon: 'pi pi-paperclip', keywords: ['file', 'attachment', 'پیوست', 'فایل'], needsContent: true, run: ({ editor, range, upload }) => { del(editor, range).run(); upload?.('file') } }]))
  r.register(custom('raavi.unsupported', UnsupportedView))

  // Mentions: atom node from Tiptap with the user picker (tenant-scoped directory search).
  const mentionBase = Mention.configure({
    HTMLAttributes: { class: 'raavi-mention' },
    renderText: ({ node }) => `@${node.attrs.label ?? ''}`,
    renderHTML: ({ node }) => ['span', { class: 'raavi-mention', 'data-user-id': node.attrs.id }, `@${node.attrs.label ?? ''}`],
    suggestion: {
      char: '@',
      items: async ({ query }) => (env && query.length > 0 ? (await env.searchUsers(query)).slice(0, 8) : []).map((u) => ({ key: u.id, label: u.label, icon: 'pi pi-user', detail: u.detail })),
      command: ({ editor, range, props }) => {
        const item = props as unknown as SuggestionEntry
        editor.chain().focus().insertContentAt(range, [{ type: 'mention', attrs: { id: item.key, label: item.label, mentionSuggestionChar: '@' } }, { type: 'text', text: ' ' }]).run()
      },
      render: floatingRenderer(() => ({ empty: env?.t('editor.mention.none') ?? '', aria: env?.t('editor.mention.label') ?? '' })) as never,
    },
  })
  r.register({ manifest: manifest('raavi.mention'), tiptap: mentionBase, headless: mentionBase })

  if (env) {
    r.use(
      SlashCommand.configure({
        items: (query: string) => {
          const q = query.trim().toLowerCase()
          return r.slashItems()
            .map((item, i) => ({ item, label: env.t(item.labelKey), i }))
            .filter(({ item }) => !item.needsContent || uploadHook !== undefined)
            .filter(({ item, label }) => !q || label.toLowerCase().includes(q) || item.keywords.some((k) => k.toLowerCase().includes(q)))
            .map(({ item, label, i }) => ({ key: `${item.labelKey}-${i}`, label, icon: item.icon, run: (editor: Editor, range: Range) => item.run({ editor, range, upload: uploadHook }) }))
        },
        labels: () => ({ empty: env.t('editor.slash.none'), aria: env.t('editor.slash.label') }),
      }),
      Placeholder.configure({ placeholder: env.placeholder ?? env.t('editor.placeholder') }),
    )
  }
  return r
}

/** Set by the editor component so slash items can open the file picker. */
let uploadHook: ((kind: 'image' | 'file') => void) | undefined
export function setUploadHook(hook: ((kind: 'image' | 'file') => void) | undefined): void {
  uploadHook = hook
}

/** Extensions for headless use (schema checks, round-trip tests). */
export function headlessExtensions(): AnyExtension[] {
  return createEditorRegistry().extensions({ views: false })
}
