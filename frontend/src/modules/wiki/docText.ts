import type { RaaviDoc } from '@/core/api/wiki'

/**
 * Phase 3 body editing: one paragraph per line of plain text. This is the documented placeholder until the Tiptap
 * editor (Phase 4); the server validates and canonicalizes whatever is sent.
 */
export function textToDoc(text: string): RaaviDoc {
  const lines = text.replace(/\r\n/g, '\n').split('\n')
  const content = lines.every((l) => l.length === 0)
    ? []
    : lines.map((line) => (line.length === 0 ? { type: 'paragraph' } : { type: 'paragraph', content: [{ type: 'text', text: line }] }))
  return { schema: 'raavi.doc', schemaVersion: 1, doc: { type: 'doc', content } }
}

/** Paragraph texts of a document (hard breaks become line breaks inside a paragraph). */
export function docParagraphs(doc: RaaviDoc | null | undefined): { text: string; dir: 'auto' | 'ltr' | 'rtl' }[] {
  if (!doc?.doc?.content) return []
  return doc.doc.content
    .filter((b) => b.type === 'paragraph')
    .map((b) => ({
      text: (b.content ?? []).map((n) => (n.type === 'text' ? (n.text ?? '') : n.type === 'hardBreak' ? '\n' : '')).join(''),
      dir: b.attrs?.dir ?? 'auto',
    }))
}

export function docToText(doc: RaaviDoc | null | undefined): string {
  return docParagraphs(doc)
    .map((p) => p.text)
    .join('\n')
}
