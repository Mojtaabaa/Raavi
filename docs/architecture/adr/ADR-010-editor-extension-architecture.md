# ADR-010: Editor and Extension Architecture

**Status:** Accepted · **Date:** 2026-11-06

## Context
The editor must support a large, growing set of nodes/macros, be validated on the server, survive schema evolution and render safely.

## Decision
- **Tiptap 3** (open-source extensions only) on ProseMirror; canonical **Raavi Document JSON** with envelope `{schema, schemaVersion, doc}`.
- **Extension Registry** with one JSON manifest per extension shared by frontend and backend (`schema/editor/extensions/*.json`): id, version, kind, content expression, attribute JSON Schemas, permissions, migrations, search/export capabilities. Frontend adds Tiptap implementation + UI (toolbar, slash command, context menu, renderer); backend adds validator, renderer, search extractor, migrations, export handlers. Details: `EDITOR_ARCHITECTURE.md`.
- Server-side validation is authoritative on save, publish and import.
- Lazy read-time migration + background re-migration jobs.
- Whitelist HTML renderer per node + HtmlSanitizer as defence in depth.
- `raavi.unsupported` node for imported/unknown constructs.
- Mermaid and KaTeX render client-side in strict mode; Monaco for code editing.

## Alternatives considered
- Lexical, BlockNote, Editor.js — rejected (spec mandates Tiptap; Tiptap has the strongest Yjs integration).
- Markdown as canonical — rejected: cannot represent panels, mentions, includes, comments anchors faithfully.
- Tiptap Pro extensions — rejected for licensing/on-prem constraints.

## Phase 4 outcome (2026-11-07)

Implemented with 36 manifests (`schema/editor/extensions`) and one shared fixture per manifest (`schema/editor/fixtures`) used by both test suites. Manifest fields: `id`, `name` (the JSON node/mark type), `version`, `kind`, `group`, `content`, `marks`, `inline`, `atom`, `attributes` (subset of JSON Schema: type, nullable, default, enum, maxLength, minimum, maximum, pattern, format guid/href, items, maxItems), `search` (text / none / newline / attr:<name>), `insertable`. The backend compiles content expressions into regular expressions over one token per node type. Raavi nodes are generated from their manifests on the frontend (`manifestNode`), so attribute names and defaults cannot drift. Deferred: include, excerpt, smart link, Jira, chart, emoji, Monaco. Mermaid is pinned to 11.x (12.x depends on EPL/GPL `elkjs`).

## Consequences
Manifests are a shared contract that needs CI validation (manifest JSON Schema + parity test that every manifest has both a frontend registration and a backend validator).
