import { Extension } from '@tiptap/core'
import { Plugin, PluginKey } from '@tiptap/pm/state'
import { Decoration, DecorationSet } from '@tiptap/pm/view'
import type { Node as PMNode } from '@tiptap/pm/model'

/**
 * Prompt Hub: marks template tags in the text — {{page.title}}, {{#if input.x}}, {{/if}} — so variables stand out from ordinary text and an unknown
 * variable is visibly different. Purely visual (decorations): the document is never changed and nothing is evaluated.
 */
export interface PromptVariableOptions {
  /** Names the server knows (built-ins) plus the author's input variables; anything else is drawn as unknown. */
  known: () => Set<string>
}

const TAG = /\{\{\s*([^{}]*?)\s*\}\}/g
const key = new PluginKey('promptVariables')

export function tagName(inner: string): string | null {
  if (inner.startsWith('/')) return null
  const body = inner.replace(/^#(if|unless)\s+/, '')
  return body.split('|')[0]!.trim()
}

function decorate(doc: PMNode, known: Set<string>): DecorationSet {
  const out: Decoration[] = []
  doc.descendants((node, pos) => {
    if (!node.isText || !node.text) return
    for (const m of node.text.matchAll(TAG)) {
      const from = pos + (m.index ?? 0)
      const name = tagName(m[1] ?? '')
      const control = (m[1] ?? '').startsWith('#') || (m[1] ?? '').startsWith('/')
      const unknown = !!name && !known.has(name)
      out.push(Decoration.inline(from, from + m[0].length, { class: `raavi-variable${control ? ' control' : ''}${unknown ? ' unknown' : ''}`, 'data-variable': name ?? '' }))
    }
  })
  return DecorationSet.create(doc, out)
}

export const PromptVariables = Extension.create<PromptVariableOptions>({
  name: 'promptVariables',
  addOptions() {
    return { known: () => new Set<string>() }
  },
  addProseMirrorPlugins() {
    const known = this.options.known
    return [
      new Plugin({
        key,
        state: {
          init: (_, { doc }) => decorate(doc, known()),
          apply: (tr, old) => (tr.docChanged || tr.getMeta(key) ? decorate(tr.doc, known()) : old),
        },
        props: { decorations: (state) => key.getState(state) },
      }),
    ]
  },
})

/** Ask the plugin to redraw (the set of known names changed). */
export const refreshVariableMarks = (editor: { view: { dispatch: (tr: unknown) => void; state: { tr: { setMeta: (k: PluginKey, v: boolean) => unknown } } } }) =>
  editor.view.dispatch(editor.view.state.tr.setMeta(key, true))
