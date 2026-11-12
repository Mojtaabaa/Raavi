# B1 Report — Knowledge Governance Foundation

**Date:** 2026-11-11  
**Status:** Complete

## Capabilities completed

- Side table `content.ContentGovernance` (1:1 with Content): Owner, ReviewOwner, ReviewAt, LastReviewedAt, Classification, LifecycleStatus, UpdatedBy/At  
- Defaults on create (pages, space home, import); backfill for existing rows  
- `PUT /api/v1/content/{id}/governance` (requires CanEdit; Owner ≠ ACL)  
- `GET /api/v1/content/review-due` (View-filtered; optional owner filter)  
- Page DTO includes `governance`; review-due list items carry owner/classification/lifecycle  
- Outbox: `content.owner_changed`, `content.review_scheduled`, `content.reviewed`, `content.classification_changed`, `content.governance_updated`  
- Restricted classification excluded from Ask/RAG **model** context (`FilterForExternalModelAsync`)  
- UI: governance panel on page; home “pages needing review”; fa/en + RTL/LTR  
- ADR-054; migration plan executed (additive only)

## Modules / files (main)

- Domain: `ContentGovernance.cs`  
- Persistence: `WikiDbContext`, migration `20261111164721_ContentGovernance`  
- Application: `PageService`, `SpaceService`, `ContentImporter`, `ContentIndexSource`/`ViewableContentFilter`, DTOs, `WikiEvents`  
- AI: `Rag.AskAsync` model-safe filter; `IViewableContentFilter` extension  
- API: `WikiEndpoints`  
- Frontend: `GovernancePanel.vue`, `PageView`, `HomeView`, `wiki.ts`, i18n  
- Tests: `ContentGovernanceTests`  
- Docs: ADR-054, B1_MIGRATION_PLAN, KNOWLEDGE_GOVERNANCE, IMPLEMENTATION_STATUS, user-guide fa  

## Database

Additive table + RLS + Tenant FK + backfill. No drops of Content data. Rollback = drop table/isolation.

## APIs

| Method | Path | Notes |
|--------|------|--------|
| PUT | `/api/v1/content/{id}/governance` | Auth user; CanEdit |
| GET | `/api/v1/content/review-due` | Auth user; View filter |

## Tests run

| Suite | Result |
|-------|--------|
| `ContentGovernanceTests` | 3 passed |
| `SpacePageTests` + `TenantIsolationModelTests` | 9 passed |
| Frontend `tests/wiki.test.ts` | 9 passed |

## Remaining (later stages)

- B2 Knowledge relationships  
- B3 discovery filters (owner, classification, lifecycle, review status, …)  
- B4 broaden outbox/notification coverage if needed  
- B5 full regression  
- Retention / Legal Hold (design only until PO)  
- Decision/Incident as templates (not separate entities)  
