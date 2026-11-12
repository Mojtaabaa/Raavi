# Phase 2 (slice 4) — Ask citations UX (EKOS-10)

**Date:** 2026-11-12  
**Status:** Complete

## Scope

- Server (`CitationRefs` + `RagService.AskAsync`): strip out-of-range `[n]` markers from the answer text; set `AskSourceDto.Cited` from markers that match real sources
- FE smart-search Ask: only link valid `[n]`; jump/highlight source on cite click; Cited / Not cited badges; note when nothing was cited
- Guardrails unchanged: retrieval + `FilterForExternalModelAsync` before any model call; sources are only pages already sent to the model

## Tests

- `CitationRefsTests`
- `RagTests` ask path asserts sanitised text + `cited`
- `smartsearch.test.ts` cite linking
