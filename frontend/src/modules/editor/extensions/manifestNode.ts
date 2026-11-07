import { Node, mergeAttributes, type NodeConfig } from '@tiptap/core'
import { VueNodeViewRenderer } from '@tiptap/vue-3'
import type { Component } from 'vue'
import type { ExtensionManifest } from '../registry'

/**
 * Builds a Tiptap node whose schema (group, content, inline/atom, attributes + defaults) comes from the shared
 * manifest, so the client and the server validator cannot drift. Attributes round-trip through `data-*` attributes
 * of a single wrapper element; the HTML is only used for clipboard/drag and drop inside the editor.
 */
export function manifestNode(manifest: ExtensionManifest, options: { view?: Component; tag?: string; extra?: Partial<NodeConfig> } = {}) {
  const tag = options.tag ?? (manifest.inline ? 'span' : 'div')
  const marker = `data-raavi-${manifest.name.toLowerCase()}`
  const build = (withView: boolean) =>
    Node.create({
      name: manifest.name,
      group: manifest.group,
      content: manifest.content,
      inline: !!manifest.inline,
      atom: !!manifest.atom,
      selectable: true,
      draggable: !manifest.inline,
      defining: !!manifest.content,
      addAttributes() {
        return Object.fromEntries(
          Object.entries(manifest.attributes).map(([name, spec]) => [
            name,
            {
              default: spec.default ?? null,
              parseHTML: (el: HTMLElement) => {
                const raw = el.getAttribute(`data-${name.toLowerCase()}`)
                if (raw === null) return spec.default ?? null
                if (spec.type === 'integer') return Number.parseInt(raw, 10)
                if (spec.type === 'boolean') return raw === 'true'
                if (spec.type === 'array') return JSON.parse(raw) as unknown
                return raw
              },
              renderHTML: (attrs: Record<string, unknown>) => {
                const v = attrs[name]
                return v === null || v === undefined ? {} : { [`data-${name.toLowerCase()}`]: typeof v === 'string' ? v : JSON.stringify(v) }
              },
            },
          ]),
        )
      },
      parseHTML() {
        return [{ tag: `${tag}[${marker}]` }]
      },
      renderHTML({ HTMLAttributes }) {
        const attrs = mergeAttributes({ [marker]: '', class: `raavi-${manifest.name.toLowerCase()}` }, HTMLAttributes)
        return manifest.content ? [tag, attrs, 0] : [tag, attrs]
      },
      ...(withView && options.view ? { addNodeView: () => VueNodeViewRenderer(options.view!) } : {}),
      ...(options.extra ?? {}),
    })
  return { tiptap: build(true), headless: build(false) }
}
