# Phase 2 (slice 3) — Maintenance scanners (EKOS-8)

**Date:** 2026-11-12  
**Status:** Complete (on-demand suggestions → optional tasks)

## Scope

- Application: `IKnowledgeMaintenanceScanner` / `KnowledgeMaintenanceSuggestion`
- Wiki: `KnowledgeMaintenanceService` — `review_due`, `stale_content` (`Raavi:KnowledgeMaintenance:StaleAfterDays`), `near_duplicate` (title Jaccard / fold equality in the same space)
- `GET /admin/knowledge-maintenance?take=` — tenant administrators; View-filtered
- FE: Administration → Knowledge maintenance; **Create task** via existing tasks API (no auto-edit/publish)
- Config in `sandbox/config` and `sandbox-defaults/config` (`KnowledgeMaintenance`)

## Out of scope (later)

- Background Hangfire job / dismissals / content-body embeddings for duplicates
- Contradiction scanner
