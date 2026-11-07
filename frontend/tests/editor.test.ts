import { describe, expect, it } from 'vitest'
import { Editor, getSchema } from '@tiptap/core'
import { EditorRegistry, manifest, manifests, SCHEMA_VERSION } from '@/modules/editor/registry'
import { createEditorRegistry, headlessExtensions, isSafeHref } from '@/modules/editor/setup'

const fixtures = import.meta.glob<{ schema: string; schemaVersion: number; doc: Record<string, unknown> }>('@schema/editor/fixtures/*.json', { eager: true, import: 'default' })
const fixtureFor = (id: string) => Object.entries(fixtures).find(([path]) => path.endsWith(`/${id}.json`))?.[1]

describe('editor registry', () => {
  it('registers every shared manifest exactly once', () => {
    expect(manifests.length).toBeGreaterThanOrEqual(36)
    const registry = createEditorRegistry()
    expect(registry.missing()).toEqual([])
    expect(() => registry.register({ manifest: manifest('raavi.panel') })).toThrow(/Duplicate/)
    expect(() => new EditorRegistry().extensions({ views: false })).toThrow(/without registration/)
  })

  it('matches the manifests: names, content expressions, attributes and defaults', () => {
    const schema = getSchema(headlessExtensions())
    for (const m of manifests) {
      const type = m.kind === 'node' ? schema.nodes[m.name] : schema.marks[m.name]
      expect(type, m.id).toBeDefined()
      const spec = type!.spec as { attrs?: Record<string, { default?: unknown }>; content?: string; group?: string }
      const attrs = Object.fromEntries(Object.entries(spec.attrs ?? {}).map(([k, v]) => [k, v.default ?? null]))
      const expected = Object.fromEntries(Object.entries(m.attributes).map(([k, v]) => [k, v.default ?? null]))
      expect(attrs, `${m.id} attributes`).toEqual(expected)
      if (m.kind === 'node') {
        expect(spec.content ?? undefined, `${m.id} content`).toBe(m.content)
        expect(spec.group ?? undefined, `${m.id} group`).toBe(m.group)
      }
    }
  })

  it.each(manifests.map((m) => m.id))('round-trips the shared fixture of %s unchanged', (id) => {
    const fixture = fixtureFor(id)
    expect(fixture, `fixture for ${id}`).toBeDefined()
    expect(fixture!.schemaVersion).toBe(SCHEMA_VERSION)
    const editor = new Editor({ extensions: headlessExtensions(), content: fixture!.doc })
    try {
      expect(editor.getJSON()).toEqual(fixture!.doc)
    } finally {
      editor.destroy()
    }
  })

  it('uses the same link allow-list as the server', () => {
    for (const ok of ['https://example.com', 'mailto:a@b.c', '/spaces/ENG', '#section', 'raavi:content/1']) expect(isSafeHref(ok), ok).toBe(true)
    for (const bad of ['javascript:alert(1)', 'JAVASCRIPT:alert(1)', 'vbscript:x', 'data:image/png;base64,AA', '//evil.example', '/\\evil.example', 'ftp://x', ' javascript:x'])
      expect(isSafeHref(bad), bad).toBe(false)
  })

  it('reports version differences against the server schema', () => {
    const registry = createEditorRegistry()
    const server = { schemaVersion: SCHEMA_VERSION, extensions: manifests.map((m) => ({ id: m.id, version: m.version })) }
    expect(registry.verify(server)).toEqual([])
    expect(registry.verify({ ...server, extensions: server.extensions.map((e) => (e.id === 'raavi.panel' ? { ...e, version: 9 } : e)) })).toHaveLength(1)
    expect(registry.verify({ ...server, schemaVersion: SCHEMA_VERSION + 1 })).toHaveLength(1)
  })

  it('offers slash commands in both languages', () => {
    const t = (k: string) => k
    const registry = createEditorRegistry({ t, searchUsers: async () => [] })
    const items = registry.slashItems()
    expect(items.some((i) => i.keywords.includes('جدول'))).toBe(true)
    expect(items.some((i) => i.labelKey === 'editor.slash.mermaid')).toBe(true)
    expect(items.some((i) => i.labelKey.includes('unsupported'))).toBe(false) // not insertable
  })
})
