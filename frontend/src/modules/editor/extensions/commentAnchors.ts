import { Extension } from '@tiptap/core'
import type { Node as PMNode } from '@tiptap/pm/model'
import { Plugin, PluginKey, type EditorState } from '@tiptap/pm/state'
import { Decoration, DecorationSet } from '@tiptap/pm/view'

/**
 * Inline comment anchors (ADR-024): a thread stores a text-quote selector {exact, prefix, suffix} over the page's plain
 * text. The read view resolves selectors to ranges and decorates them; the document itself is never changed, so
 * commenting needs no Edit right and works on any published version. A selector whose text no longer occurs is
 * reported as orphaned.
 */
export interface TextQuote {
  exact: string
  prefix: string
  suffix: string
}
export interface AnchorHighlight extends TextQuote {
  id: string
}

export const CONTEXT_LENGTH = 64
export const MAX_QUOTE_LENGTH = 500

/** Plain text of the document with a document position for every character (block boundaries become "\n"). */
export interface FlatText {
  text: string
  positions: number[]
}

export function flatten(doc: PMNode): FlatText {
  let text = ''
  const positions: number[] = []
  doc.descendants((node, pos) => {
    if (node.isBlock && text.length > 0 && !text.endsWith('\n')) {
      text += '\n'
      positions.push(-1)
    }

    if (node.isText) {
      const value = node.text ?? ''
      for (let i = 0; i < value.length; i++) {
        text += value[i]
        positions.push(pos + i)
      }
    } else if (node.isAtom && node.isInline) {
      // Mentions, inline math, status: one opaque character each so offsets stay stable.
      text += '￼'
      positions.push(pos)
    }

    return true
  })
  return { text, positions }
}

function commonSuffix(a: string, b: string): number {
  let n = 0
  while (n < a.length && n < b.length && a[a.length - 1 - n] === b[b.length - 1 - n]) n++
  return n
}

function commonPrefix(a: string, b: string): number {
  let n = 0
  while (n < a.length && n < b.length && a[n] === b[n]) n++
  return n
}

/** Best occurrence of the quote (exact text, then the closest prefix/suffix context), as a document range. */
export function locate(flat: FlatText, quote: TextQuote): { from: number; to: number } | null {
  if (!quote.exact) return null
  let best = -1
  let bestScore = -1
  for (let i = flat.text.indexOf(quote.exact); i >= 0; i = flat.text.indexOf(quote.exact, i + 1)) {
    const score = commonSuffix(flat.text.slice(Math.max(0, i - quote.prefix.length), i), quote.prefix)
      + commonPrefix(flat.text.slice(i + quote.exact.length, i + quote.exact.length + quote.suffix.length), quote.suffix)
    if (score > bestScore) {
      best = i
      bestScore = score
    }
  }

  if (best < 0) return null
  const start = flat.positions.slice(best, best + quote.exact.length).find((p) => p >= 0)
  const endChars = flat.positions.slice(best, best + quote.exact.length).filter((p) => p >= 0)
  if (start === undefined || endChars.length === 0) return null
  return { from: start, to: endChars[endChars.length - 1]! + 1 }
}

/** Text-quote selector of a document range; null when the range holds no text or is too long. */
export function quoteRange(flat: FlatText, from: number, to: number): TextQuote | null {
  const startIndex = flat.positions.findIndex((p) => p >= from)
  let endIndex = -1
  for (let i = flat.positions.length - 1; i >= 0; i--) {
    const p = flat.positions[i]!
    if (p >= 0 && p < to) {
      endIndex = i + 1
      break
    }
  }

  if (startIndex < 0 || endIndex <= startIndex) return null
  let s = startIndex
  let e = endIndex
  while (s < e && /\s/.test(flat.text[s]!)) s++
  while (e > s && /\s/.test(flat.text[e - 1]!)) e--
  const exact = flat.text.slice(s, e)
  if (!exact || exact.length > MAX_QUOTE_LENGTH) return null
  return { exact, prefix: flat.text.slice(Math.max(0, s - CONTEXT_LENGTH), s), suffix: flat.text.slice(e, e + CONTEXT_LENGTH) }
}

export function quoteSelection(state: EditorState): TextQuote | null {
  const { from, to, empty } = state.selection
  return empty ? null : quoteRange(flatten(state.doc), from, to)
}

interface AnchorState {
  anchors: AnchorHighlight[]
  active: string | null
  decorations: DecorationSet
  located: string[]
}

export interface CommentAnchorOptions {
  /** Called with the ids of the anchors found in the current document (the others are orphaned). */
  onLocated: (ids: string[]) => void
}

export const commentAnchorsKey = new PluginKey<AnchorState>('raavi-comment-anchors')

function build(doc: PMNode, anchors: AnchorHighlight[], active: string | null): { decorations: DecorationSet; located: string[] } {
  const flat = flatten(doc)
  const decorations: Decoration[] = []
  const located: string[] = []
  for (const a of anchors) {
    const range = locate(flat, a)
    if (!range) continue
    located.push(a.id)
    decorations.push(Decoration.inline(range.from, range.to, {
      class: a.id === active ? 'raavi-comment-anchor active' : 'raavi-comment-anchor',
      'data-thread-id': a.id,
    }))
  }

  return { decorations: DecorationSet.create(doc, decorations), located }
}

export const CommentAnchors = Extension.create<CommentAnchorOptions>({
  name: 'commentAnchors',

  addOptions() {
    return { onLocated: () => undefined }
  },

  addProseMirrorPlugins() {
    const options = this.options
    return [
      new Plugin<AnchorState>({
        key: commentAnchorsKey,
        state: {
          init: () => ({ anchors: [], active: null, decorations: DecorationSet.empty, located: [] }),
          apply(tr, value, _old, newState) {
            const meta = tr.getMeta(commentAnchorsKey) as { anchors: AnchorHighlight[]; active: string | null } | undefined
            if (!meta && !tr.docChanged) return value
            const anchors = meta?.anchors ?? value.anchors
            const active = meta ? meta.active : value.active
            const built = build(newState.doc, anchors, active)
            return { anchors, active, ...built }
          },
        },
        view: () => {
          let last = ''
          return {
            update: (view) => {
              const located = commentAnchorsKey.getState(view.state)?.located ?? []
              const key = located.join(',')
              if (key !== last) {
                last = key
                options.onLocated(located)
              }
            },
          }
        },
        props: {
          decorations: (state) => commentAnchorsKey.getState(state)?.decorations,
        },
      }),
    ]
  },
})
