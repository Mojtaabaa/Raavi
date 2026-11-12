# Phase 2 (slice 1) — Editor AI actions (EKOS-7)

**Date:** 2026-11-12  
**Status:** Complete (summarize / rewrite selection with confirm-to-apply)

## Scope

- `POST /ai/pages/{id}/editor/summarize` and `…/rewrite`
- Body: `{ selection, instruction?, confirmed }` — selection from the client only (no draft/published body load for the prompt)
- **Edit** required via `IPageEditGate` (opaque 404 like View misses)
- `confirmed: true` required (`ai.confirm_required`)
- Features accounted as `editor_summarize` / `editor_rewrite`
- FE: selection bar on page edit + dialog (consent → generate → replace range in draft)

## Out of scope (later P2)

- Streaming, translate presets, Prompt Hub templates for editor actions
- (EKOS-8/9/10 done — see matching `P2_*_REPORT.md` files)
