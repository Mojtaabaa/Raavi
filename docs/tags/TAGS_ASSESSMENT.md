# Tags Assessment — Raavi

**Date:** 2026-11-11  
**Decision:** Evolve existing Labels; do not create a parallel Tags module.

## Existing implementation

| Area | Finding |
|------|---------|
| Domain | `Label`, `ContentLabel` in `Raavi.Wiki.Domain` → `content.Labels`, `content.ContentLabels` (RLS, unique `(TenantId, Name)`) |
| Assign | `PageService.AddLabelAsync` / `RemoveLabelAsync` — requires page **Edit** |
| Discover | `DiscoveryService.SuggestLabelsAsync`, `ContentWithLabelAsync` |
| API | `POST/DELETE /content/{id}/labels`, `GET /labels?prefix=`, `GET /labels/{name}/content` |
| UI | `PageView.vue` chips, `LabelView.vue`, route `/labels/:name` |
| Workflows | `WfField.Label` / `Has`, `WfActionType.AddLabel` |
| Migration | Confluence labels → `ContentImporter.AddLabelsAsync`; macro → `contentByLabel` |
| Search | SQL quick-find + AI smart search (`ai.Chunks`); labels **not** indexed; OpenSearch deferred (7c) |
| Audit | Wiki outbox/activity — **no** label events today |
| Unrelated | Prompt Hub `Prompt.Tags` (CSV in `ai` schema) |

## Gaps vs enterprise Tags brief

Hierarchy, aliases, DisplayName/color/description, archive/merge/reorder, admin UI, multi-label AND/OR + descendants, permission-safe analytics, suggestions, outbox events, smart-search facets.

## Reuse

- Keep tables and Confluence `Name` semantics for import/workflows/`contentByLabel`.
- Add columns + `LabelAliases`; extract `LabelService`.
- Authz: Edit for assign; TenantAdministrator for governance.
- Search: extend Discovery + optional ContentLabels filter before RAG; no second engine.
