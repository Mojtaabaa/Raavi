# Current State Assessment

**Source of truth:** repository as of 2026-11-11 (`docs/IMPLEMENTATION_STATUS.md`, ADRs, modules).  
**Note:** `docs/product/FEATURE_MATRIX.md` is partially stale; prefer IMPLEMENTATION_STATUS + code.

## Architecture snapshot

| Layer | Reality |
|-------|---------|
| Shape | Modular monolith (ADR-001/002): hosts `Raavi.Api` / Workers / Migrator / Identity / SPA / `collaboration/` |
| Boundaries | Wiki ← Application ← Infrastructure; AI, Integrations, Migration, Search, Tenancy, Identity are separate projects; feature modules do not reference each other (`DependencyRulesTests`) |
| Data | SQL Server + RLS (`ITenantOwned`, `AddTenantIsolation`); schemas `system`, `identity`, `spaces`, `content`, `security`, `ai`, `integrations`, `migration`, `audit` |
| Config | `sandbox/{config,data,secrets}` + `RAAVI_*` (ADR-040) |
| Authz | `WikiAuthorizer` implements `IPermissionEvaluator`; invisible → 404 |
| Events | Module outbox same SaveChanges (ADR-023); activity projection |
| Search | SQL quick-find + AI hybrid RAG (`ai.Chunks`); OpenSearch deferred (7c); `Raavi.Search` ≈ stub + Persian normalizer |
| Editor | Tiptap + shared schema manifests; HTML derived/sanitized |
| Collab | Hocuspocus + Yjs; pages + live whiteboards; Redis multi-node not wired |

## What is already strong (EKOS foundation)

- Full wiki lifecycle: spaces, tree, drafts/publish, history/diff/restore, trash, restrictions, templates  
- Collaboration: live editing, comments/watches/notifications, SignalR  
- Taxonomy: enterprise Labels (hierarchy, aliases, merge, admin, AND/OR search)  
- Work surfaces: tasks, calendar V1, databases, slides, whiteboards  
- Governance seeds: workflows (outbox rules), page approvals, reminders, stale-page trigger  
- AI: page assistant, Prompt Hub, hybrid RAG with query-time ACL  
- Integrations: issue connectors (fake-verified), Jira↔page links, webhooks; Templar contracts/stubs  
- Ops: health/status, activity log, export/backup/release, tenant deletion grace  

## Major gaps vs EKOS vision

| Gap | Nature |
|-----|--------|
| Unified Knowledge Asset abstraction | Missing — content types exist but no shared metadata/relationship layer across pages/meetings/decisions |
| Knowledge graph + visualization | Missing — only ad-hoc links (page links, Jira links, label co-occurrence, calendar linked page/board) |
| OpenSearch + attachment/comment indexing | Deferred / Missing |
| S3/MinIO media, quotas, previews | Deferred (7b) |
| Decision / Incident / System catalog record types | Missing |
| Deep governance (classification, retention, legal hold, review schedules as first-class) | Partial — approvals/stale/workflows exist; classification/retention/hold missing |
| Extensible connector framework (beyond issue + webhooks) | Partial |
| External calendar OAuth / CalDAV | Missing |
| Templar live | Blocked on API contract / enablement |
| AI agents, maintenance (dupes/contradictions), editor writing suite | Mostly missing |
| Knowledge analytics dashboard | Partial — activity stats, label analytics, prompt analytics; no EKOS dashboard |
| Real external host verification | Blocked (Jira/AD/OIDC/connectors — fakes only) |

## Risks if we “build EKOS naively”

- Parallel Tags / Calendar / Search / Authz modules → duplicate SoT and ACL bugs  
- Graph DB or microservices without need → ops cost on-prem  
- AI without ACL → data leakage  
- One universal table for all asset types → unmaintainable migrations  

## Recommendation

Treat EKOS as a **product overlay and disciplined extension** of the modular monolith: introduce shared relationship + lifecycle abstractions inside Wiki (and contracts in Application), deepen Search/AI/Integrations, add Decision/Incident/Catalog as content providers or sibling entities — **Stage B only after PO approval**.