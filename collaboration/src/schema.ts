import { readFileSync, readdirSync } from 'node:fs'
import { join } from 'node:path'
import { Schema, type MarkSpec, type NodeSpec, type AttributeSpec } from 'prosemirror-model'

/**
 * ProseMirror schema built from the shared extension manifests (`schema/editor/extensions`), so the collaboration
 * server converts Yjs ⇄ Document JSON with exactly the structure the editor and the API validator use (ADR-010).
 */
interface Manifest {
  id: string
  name: string
  kind: 'node' | 'mark'
  group?: string
  content?: string
  marks?: string
  inline?: boolean
  atom?: boolean
  attributes: Record<string, { default?: unknown; nullable?: boolean }>
}

export function loadManifests(dir: string): Manifest[] {
  return readdirSync(dir)
    .filter((f) => f.endsWith('.json'))
    .sort()
    .map((f) => JSON.parse(readFileSync(join(dir, f), 'utf8')) as Manifest)
}

function attrs(m: Manifest): Record<string, AttributeSpec> {
  return Object.fromEntries(
    Object.entries(m.attributes).map(([name, spec]) => [
      name,
      spec.default !== undefined ? { default: spec.default } : spec.nullable ? { default: null } : {},
    ]),
  )
}

export function buildSchema(manifests: Manifest[]): Schema {
  const nodes: Record<string, NodeSpec> = {}
  // ProseMirror uses the first node of a group as the default; keep doc, paragraph and text first.
  const order = (m: Manifest) => (m.name === 'doc' ? 0 : m.name === 'paragraph' ? 1 : m.name === 'text' ? 2 : 3)
  for (const m of manifests.filter((x) => x.kind === 'node').sort((a, b) => order(a) - order(b) || a.id.localeCompare(b.id))) {
    nodes[m.name] = {
      ...(m.content ? { content: m.content } : {}),
      ...(m.group ? { group: m.group } : {}),
      ...(m.marks !== undefined ? { marks: m.marks } : {}),
      ...(m.inline ? { inline: true } : {}),
      ...(m.atom ? { atom: true } : {}),
      attrs: attrs(m),
    }
  }
  const marks: Record<string, MarkSpec> = {}
  for (const m of manifests.filter((x) => x.kind === 'mark')) marks[m.name] = { attrs: attrs(m) }
  return new Schema({ nodes, marks, topNode: 'doc' })
}
