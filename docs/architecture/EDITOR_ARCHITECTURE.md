# Editor Architecture

## 1. Canonical content format

- Canonical stored form: **Raavi Document JSON** — a ProseMirror/Tiptap-compatible JSON tree wrapped in an envelope:

```json
{ "schema": "raavi.doc", "schemaVersion": 3, "doc": { "type": "doc", "content": [ … ] } }
```

- HTML is a **derived, sanitized** representation (render cache), never the source of truth.
- Plain text for search and AI is extracted per node by the extension's `searchExtractor`.
- During live collaboration the working state is a Yjs document (ADR-011); a page version is always stored as Document JSON.

## 2. Extension Registry

One manifest per extension, shared by frontend and backend, stored in `schema/editor/extensions/<id>.json` (added in Phase 4):

```json
{
  "id": "raavi.panel",
  "version": 2,
  "kind": "node",
  "group": "block",
  "content": "block+",
  "attributes": {
    "type": { "type": "string", "enum": ["info", "note", "warning", "success", "error"], "default": "info" },
    "title": { "type": "string", "maxLength": 200 }
  },
  "permissions": [],
  "migrations": [{ "from": 1, "to": 2, "description": "rename 'variant' to 'type'" }],
  "search": "text",
  "export": ["html", "markdown", "pdf", "docx"]
}
```

| Concern | Frontend (TypeScript) | Backend (.NET) |
|---|---|---|
| Schema | Tiptap `Node/Mark/Extension` created from manifest + code | `EditorSchemaRegistry` loads manifests; JSON Schema validation of attributes; content expression validation |
| Validation | Client-side for UX only | **Authoritative** on every save/publish/import: unknown node types, invalid attributes, illegal nesting, oversized documents are rejected (or, for imports, converted to `raavi.unsupported`) |
| UI | Node view, toolbar item, slash command, context menu, bubble menu | — |
| Rendering | Vue renderer for read view | `INodeRenderer` → sanitized HTML for cache/export/e-mail |
| Search | — | `ISearchExtractor` |
| Migration | — (client always receives latest schema) | `INodeMigration(from, to)`; documents are upgraded on read (lazy) and by a background re-migration job |
| Export | — | `IExportHandler` per format |
| Permissions | Hides commands the user lacks | Some extensions require permission to insert (e.g. `raavi.include` requires View on the included page; `raavi.jira` requires an enabled integration) and are re-checked when rendering for each viewer |

Registration API (frontend):

```ts
registry.register({
  manifest,            // imported JSON
  tiptap: PanelNode,   // Tiptap extension
  toolbar: { group: 'insert', icon: 'pi pi-info-circle', labelKey: 'editor.panel' },
  slash: { keywords: ['panel', 'info', 'پنل'], labelKey: 'editor.panel' },
  renderer: PanelView,
})
```

The registry rejects duplicate ids, checks the manifest version matches the backend `/api/v1/editor/schema` response and refuses to load extensions the tenant has disabled.

## 3. Initial extension catalogue

Core (Tiptap StarterKit-based): paragraph, headings 1–6, bold, italic, underline, strike, inline code, text color, highlight, text alignment (start/center/end/justify — logical, RTL-aware), bullet/ordered/task lists, blockquote, horizontal rule, hard break, link (with `rel="noopener noreferrer nofollow"`, scheme allow-list `http/https/mailto/raavi`).

Raavi extensions: `raavi.table` (Tiptap table + column widths), `raavi.image` (attachment-backed), `raavi.attachment`, `raavi.mention` (user id; display resolved at render time with permission), `raavi.emoji`, `raavi.codeBlock` (language attr; Monaco for editing large blocks), `raavi.mermaid` (source stored; rendered client-side in a sandboxed iframe/`securityLevel: 'strict'`), `raavi.toc`, `raavi.expand`, `raavi.panel`, `raavi.status`, `raavi.excerpt`, `raavi.include` (page include — renders only if the viewer can view the source; cycle detection depth ≤ 3), `raavi.smartLink`, `raavi.jira` (issue key + connection id), `raavi.chart` (data + type; no scripts), `raavi.math` (KaTeX, `trust: false`), `raavi.unsupported` (imported content placeholder; raw source kept as escaped text attribute, never rendered as HTML).

Tiptap Pro / paid extensions are **not** used; see `DEPENDENCY_AND_LICENSING.md`.

## 4. Schema evolution

- `schemaVersion` on the envelope = registry version (monotonic). Each extension has its own version and migration steps.
- Reads: if stored version < current, run migrations in memory and return the upgraded document; persisted lazily on next save.
- A background job re-migrates and re-renders in batches after an upgrade (tenant-scoped jobs).
- Removing an extension = migrate its nodes to an equivalent or to `raavi.unsupported`; never leave unknown nodes.

## 5. Security

- Server validates JSON, enforces max document size (configurable, default 5 MB JSON) and max depth.
- HTML rendering uses a whitelist renderer per node (no generic HTML passthrough). Output additionally passes through an HTML sanitizer (Ganss.Xss / HtmlSanitizer, MIT) as belt-and-braces.
- Links: `javascript:`, `data:` (except images rendered from our own attachment endpoint), `vbscript:` rejected.
- Embeds (iframes) only for an allow-list of providers configured globally; rendered with `sandbox` attributes.
- Mermaid/KaTeX run client-side with strict security settings; server never executes them.
- Read view CSP: `script-src 'self'`, no inline scripts, `object-src 'none'`.

## 5b. Implementation status (Phase 4)

- Registry version (`schemaVersion`) is **2**; v1 (Phase 3 plain paragraphs) is migrated on read.
- Default limits: 5 MB JSON, depth 48 (`schema/editor/registry.json`).
- Attachments-lite: `POST /content/{id}/attachments` (Edit), `GET /attachments/{id}` (View on the owning page, `nosniff`, `sandbox` CSP, SVG/HTML never served inline); 10 MB default (`Raavi:Attachments:MaxBytes`).
- Read view uses the editor in read-only mode (same schema); `/content/{id}/html` is the server rendering for export/e-mail.

## 6. RTL / bidi

- Each block node carries an optional `dir` attribute (`auto` by default → rendered `dir="auto"`), so mixed Persian/English documents render correctly per paragraph.
- Editor toolbar and menus follow the UI direction; text alignment uses logical values (`start`/`end`).
- Code blocks are always `dir="ltr"`.
