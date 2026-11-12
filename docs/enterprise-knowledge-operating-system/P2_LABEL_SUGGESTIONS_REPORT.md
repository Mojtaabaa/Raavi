# Phase 2 (slice 2) — AI label suggestions (EKOS-9)

**Date:** 2026-11-12  
**Status:** Complete

## Scope

- Application: `ILabelSuggestionProvider`, `ILabelCatalog`, `SuggestedLabel`
- Wiki: `HeuristicLabelSuggestionProvider` (default DI) wrapping `LabelService` heuristics / assignable catalog
- `GET /content/{id}/label-suggestions` → heuristic rows (`source: heuristic`)
- `GET /ai/pages/{id}/label-suggestions` → View + tenant AI; model may only pick slugs from `ILabelCatalog` (inventions dropped); feature `label_suggest`
- FE page view: merge heuristic + AI (AI wins on slug clash); AI badge via i18n

## Guardrails

- No invented labels applied from the model
- Catalog list is View-filtered and excludes already-assigned labels
- AI off / errors → UI keeps heuristic list only
