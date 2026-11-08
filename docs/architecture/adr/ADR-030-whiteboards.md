# ADR-030 — Whiteboards (Phase 9e)

**Status:** Accepted (2026-11-08). Live co-editing (Yjs) is **not** part of this step — see "Deferred".

## Context

Last step of Phase 9: a free canvas per space (shapes, text, sticky notes, arrows, freehand) with persistence, tenant ownership, permissions, navigation and export. The roadmap asked for a Konva-vs-Fabric evaluation and Yjs.

## Konva vs Fabric (measured 2026-11-08)

Both were bundled with esbuild (minified, ESM) with a comparable minimal editor (stage/canvas, rect, text, ellipse, arrow/line, transformer or free drawing):

| | Konva 10.9.1 | Fabric 7.4.0 |
|---|---|---|
| Licence | MIT | MIT |
| Minimal editor bundle | 200 KB raw / **62 KB gzip** | 287 KB raw / 90 KB gzip |
| npm unpacked size | 1.9 MB | 22 MB |
| Model | Scene graph; state lives in our own JSON, nodes are rebuilt from it | Objects hold their own state; `toJSON`/`loadFromJSON` serialise the canvas |
| RTL text | `direction: 'rtl'` on `Text` | `direction` on `Text` |
| Transform/resize/rotate | `Transformer` | built-in controls |
| Browser rendering | verified (see below) | not tried |
| Runtime dependencies | none (optional `canvas`/`skia-canvas` only for Node) | none |

**Decision: Konva.** Smaller, and its "draw what the model says" style keeps the single source of truth in plain JSON (`BoardElement[]`) that the server validates and that a future Yjs map can mirror one-to-one. Fabric's object-owned state would need a second mapping layer for that. Neither library works in jsdom without a native canvas package, so the design keeps all logic (`boardModel.ts`: factories, edits, history, geometry) pure and tests the drawing layer in a real browser instead.

## Decisions

1. **A board is one document.** `content.Whiteboards` stores title and the element list as validated JSON (`ElementsJson`), saved as a whole with one ETag (`"r{n}"`, `If-Match` required → 428/412) like decks (ADR-029). Tenant-owned, RLS, composite FK to the space. Cap: 2 000 elements.
2. **Elements** (`Rect`, `Ellipse`, `Text`, `Sticky`, `Arrow`, `Pen`) have client-generated ids (`[A-Za-z0-9_-]{1,40}`, unique per board) — they are local names, never tenant identifiers. The server validates and normalises everything: finite coordinates within ±100 000, sizes ≥ 0 (shapes and notes > 0), `#rrggbb` colours only, line width 1–24, font size 8–200, text ≤ 2 000 characters without control characters, arrows exactly 2 points, pen strokes 2–2 000 points; fields a type does not use are dropped. List order is stacking order. Position is the top-left corner, rotation turns around it.
3. **Permissions come from the space**, per request (View to read/export, Edit on an active space to change). A board of an invisible space is a 404.
4. **Text direction** is stored per text element (`rtl`, detected from the first strong character like `dir="auto"`) so canvas, SVG and PNG agree.
5. **Export.** PNG is taken from the canvas (exact, 2×, white background, independent of pan/zoom). SVG is generated on the server (`BoardSvgWriter`): colours and numbers come from validated values and all text is XML-escaped, so a board cannot inject markup; wrapped text (sticky notes, text with a width) uses an *estimated* glyph width, so line breaks can differ slightly from the canvas.
6. **Accessibility.** A canvas is opaque to assistive technology, so every canvas action has an HTML equivalent: the element list (select), the inspector (edit text, colours, line width, duplicate, order, delete), keyboard shortcuts (Delete, Ctrl+Z/Y/D, Esc) and toolbar buttons with labels. Arrows and pen strokes can be moved/recoloured but not re-shaped.
7. **Undo/redo** keeps whole-board snapshots (cap 100) in the editor; saving clears nothing but loading a board does. The editor warns before leaving with unsaved changes.

## Deferred: live co-editing (Yjs)

The collaboration server (ADR-011) is built around page drafts: document names, tickets, state/update tables and draft conversion are page-specific. Adding a "board" document kind means a new ticket/permission path, persistence tables for updates/snapshots and a server-side conversion/validation step — a change of its own with its own security review. Until then two people editing the same board get a 412 on the second save and must reload (last-save-wins is deliberately not offered). The model is ready for it: elements are an id-keyed list, so a Yjs `Y.Map<id, element>` plus an order array maps directly.

## Not in 9e

Live co-editing and cursors, images/files (needs 7b media storage), connectors that stay attached to shapes, re-shaping arrows/strokes, grouping, snapping/alignment, infinite-canvas minimap, board templates, PDF export, board history.
