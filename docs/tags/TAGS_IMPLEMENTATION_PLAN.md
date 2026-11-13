# Tags Implementation Plan

## Stages

| Stage | Work |
|-------|------|
| A | Assessment docs (this pack) |
| B | Entity + migration `LabelTaxonomy` + LabelService + APIs + tests |
| C | Page chips + LabelView |
| D | Multi-tag search + URL state + smart-search filter |
| E | `/admin/labels` governance UI |
| F | Analytics + deterministic suggestions + AI seam |
| G | Stabilize, WIKI_API, user-guide fa, FINAL_REPORT |

## Migration risk

Additive only. Backfill DisplayName/Slug/NormalizedName from Name. Rollback = drop new columns/table (Down). No content JSON changes.

## Test gates

- `dotnet test --filter Label` (expand coverage)
- Wiki label/discovery/workflow regressions
- `vitest` locales + page/label tests
