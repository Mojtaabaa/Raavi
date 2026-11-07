import type { AnyExtension, Editor, Range } from '@tiptap/core'
import registryInfo from '@schema/editor/registry.json'

/** Attribute definition shared with the backend (`schema/editor/extensions/*.json`). */
export interface AttributeSpec {
  type: 'string' | 'integer' | 'boolean' | 'array'
  nullable?: boolean
  default?: unknown
  enum?: unknown[]
  maxLength?: number
  minimum?: number
  maximum?: number
  pattern?: string
  format?: 'guid' | 'href'
}

export interface ExtensionManifest {
  id: string
  name: string
  version: number
  kind: 'node' | 'mark'
  group?: string
  content?: string
  marks?: string
  inline?: boolean
  atom?: boolean
  attributes: Record<string, AttributeSpec>
  search?: string
  insertable?: boolean
}

export interface SlashItem {
  /** i18n key of the label. */
  labelKey: string
  icon: string
  /** Extra search words (both languages). */
  keywords: string[]
  /** Needs an existing page (attachments are stored per page). */
  needsContent?: boolean
  run: (ctx: { editor: Editor; range: Range; upload?: (kind: 'image' | 'file') => void }) => void
}

export interface Registration {
  manifest: ExtensionManifest
  /** Tiptap implementation; omitted when another registration provides it (e.g. StarterKit nodes). */
  tiptap?: AnyExtension
  /** Variant without Vue node views, for headless use (tests, schema checks). */
  headless?: AnyExtension
  slash?: SlashItem[]
}

const loaded = import.meta.glob<ExtensionManifest>('@schema/editor/extensions/*.json', { eager: true, import: 'default' })

export const manifests: ExtensionManifest[] = Object.values(loaded).sort((a, b) => a.id.localeCompare(b.id))
export const SCHEMA_VERSION: number = registryInfo.schemaVersion

export function manifest(id: string): ExtensionManifest {
  const m = manifests.find((x) => x.id === id)
  if (!m) throw new Error(`Unknown editor extension manifest '${id}'`)
  return m
}

/**
 * Frontend Extension Registry (ADR-010). One registration per manifest; duplicates are rejected and every manifest
 * must be registered before the editor is created. Versions are checked against the server's `/editor/schema`.
 */
export class EditorRegistry {
  private readonly entries = new Map<string, Registration>()
  private readonly shared: AnyExtension[] = []

  register(entry: Registration): this {
    if (this.entries.has(entry.manifest.id)) throw new Error(`Duplicate editor extension '${entry.manifest.id}'`)
    this.entries.set(entry.manifest.id, entry)
    return this
  }

  /** Extensions that implement several manifests at once (StarterKit, TableKit, …) or add behaviour only. */
  use(...extensions: AnyExtension[]): this {
    this.shared.push(...extensions)
    return this
  }

  get registrations(): Registration[] {
    return [...this.entries.values()]
  }

  missing(): string[] {
    return manifests.filter((m) => !this.entries.has(m.id)).map((m) => m.id)
  }

  extensions(options: { views: boolean }): AnyExtension[] {
    const missing = this.missing()
    if (missing.length) throw new Error(`Editor extensions without registration: ${missing.join(', ')}`)
    const own = this.registrations.map((r) => (options.views ? r.tiptap : (r.headless ?? r.tiptap))).filter((x): x is AnyExtension => !!x)
    return [...this.shared, ...own]
  }

  slashItems(): SlashItem[] {
    return this.registrations.filter((r) => r.manifest.insertable !== false).flatMap((r) => r.slash ?? [])
  }

  /** Compares local manifests with the server registry; returns a list of differences (empty = compatible). */
  verify(server: { schemaVersion: number; extensions: { id: string; version: number }[] }): string[] {
    const problems: string[] = []
    if (server.schemaVersion !== SCHEMA_VERSION) problems.push(`schemaVersion ${SCHEMA_VERSION} ≠ server ${server.schemaVersion}`)
    for (const m of manifests) {
      const s = server.extensions.find((x) => x.id === m.id)
      if (!s) problems.push(`${m.id} unknown to the server`)
      else if (s.version !== m.version) problems.push(`${m.id} v${m.version} ≠ server v${s.version}`)
    }
    return problems
  }
}
