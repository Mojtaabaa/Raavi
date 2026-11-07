# Tenant Isolation Contract

Goal: no code path — HTTP, background job, raw SQL, bulk operation, export, search, cache, file download, collaboration, AI — can read or modify another tenant's data unless it is an explicitly authorized and audited cross-tenant system operation.

## 1. Data ownership classes

| Class | Examples | Key rule |
|---|---|---|
| **System** | Tenants, TenantDomains, global feature flags, system audit, global provider registry state | Only system host / system principals |
| **Global outbox** | `content.Outbox` (and later module outboxes): ids + title snapshots, read only by the dispatcher, handlers always run in the row's tenant scope (ADR-023) | Dispatcher only |
| **Global identity** | Users, ExternalIdentities, AuthenticationSessions, UserPreferences (global part) | Never listed across tenants; access mediated by membership |
| **Tenant-owned** | Everything else: memberships, groups, roles, spaces, content, versions, comments, attachments, whiteboards, databases, tasks, calendars, workflows, automation, notifications, activity, AI usage, migration jobs, integration connections, outbox rows | `TenantId NOT NULL`, immutable |
| **User-owned within tenant** | Drafts, favorites, notification preferences, recently viewed | `TenantId` + `UserId` |
| **Shared by explicit authorization** | Shared links, public spaces (if enabled) | Still tenant-owned; exposure governed by ACL + tenant policy |

Every entity's class is recorded in `DATABASE_DESIGN.md`.

## 2. Defence layers

| # | Layer | Mechanism |
|---|---|---|
| 1 | Resolution | Host → tenant (`MULTI_TENANT_ARCHITECTURE.md` §4); immutable `ITenantContext` per scope. |
| 2 | Membership gate | Authenticated user must be an active member of the resolved tenant; otherwise treated as anonymous for that tenant. |
| 3 | Application services | Never accept `TenantId` from DTOs. Request DTOs have no `TenantId` property; model binding of an unexpected `tenantId` field is ignored. |
| 4 | EF Core | Global query filter `e => e.TenantId == _tenant.TenantId` on every `ITenantOwned` entity (registered by convention in `RaaviDbContextBase`, verified by a test that enumerates the model). |
| 5 | Write interceptor | `SaveChanges` interceptor sets `TenantId` on Added entities from the context, throws if an entity's TenantId differs from the context, and throws on any modification of `TenantId`. |
| 6 | SQL Server constraints | `TenantId` in composite unique keys (`UX_Spaces_TenantId_Key`), composite FKs `(TenantId, ParentId) → (TenantId, Id)` on important relationships so a child can never reference another tenant's parent. |
| 7 | SQL Server Row-Level Security | Filter + block predicates on tenant-owned tables using `SESSION_CONTEXT(N'TenantId')`, set **read-only** per connection by a `DbConnectionInterceptor` on open. Defence in depth: catches raw SQL, `ExecuteUpdate/Delete`, Dapper, reporting views. |
| 8 | Cache | Keys built only by `ITenantCacheKeys` → `t:{tenantId}:{area}:{key}`; a raw `IDistributedCache` is not injectable into feature code (analyzer/test). |
| 9 | Search | Mandatory `tenantId` term filter and `_routing=tenantId` added by the query builder; the builder is the only way to query (ADR-013). |
| 10 | Object storage | Keys `tenants/{tenantId}/…`; access only via the API after ACL check; pre-signed URLs short-lived and generated after the check. |
| 11 | Jobs | `TenantJobContext` envelope + job filter (§7). |
| 12 | Collaboration | Ticket carries tenant + document; server verifies both, internal API re-checks (ADR-011). |

EF filters are a convenience; RLS and constraints are the safety net. Neither alone is trusted.

## 3. Row-Level Security design

Implemented in Phase 1 (`Raavi.Infrastructure/Persistence/TenantIsolationMigrationExtensions.cs`, initial tenancy migration):

```sql
CREATE FUNCTION security.fn_tenant_predicate(@TenantId uniqueidentifier)
RETURNS TABLE WITH SCHEMABINDING AS
RETURN SELECT 1 AS allowed
WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
  AND @TenantId <> CAST(0x0 AS uniqueidentifier);

CREATE SECURITY POLICY security.TenantIsolation
  ADD FILTER PREDICATE security.fn_tenant_predicate(TenantId) ON [system].[TenantSettings],
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(TenantId) ON [system].[TenantSettings] AFTER INSERT,
  ADD BLOCK  PREDICATE security.fn_tenant_predicate(TenantId) ON [system].[TenantSettings] AFTER UPDATE
  -- one triple per tenant-owned table, added by each module's migrations (AddTenantIsolation)
WITH (STATE = ON);
```

- **There is no bypass.** (The Phase 0 draft had a `SystemScope` flag; it was removed in Phase 1.) System operations that must touch a tenant's data — provisioning, configuration by a system administrator, tenant jobs — open an explicit, audited **tenant scope** for that one tenant (`ITenantScopeFactory`). Work across tenants fans out into one scope per tenant.
- `TenantSessionContextInterceptor` sets `SESSION_CONTEXT(N'TenantId')` with `@read_only = 1` on **every** physical connection open; without a resolved tenant it sets the empty GUID, which matches no row. A later `sp_set_session_context` on the same session fails (tested).
- **Verified in Phase 1** (`RowLevelSecurityTests.Pooled_connections_never_carry_a_previous_tenant`): with pooling enabled, alternating tenant A / tenant B / unscoped contexts 25× over the same pool always shows exactly the current scope's rows and the correct `SESSION_CONTEXT` value — `sp_reset_connection` clears the read-only key on reuse. Verified on SQL Server 2016 (LocalDB 13.0); CI runs the same test on SQL Server 2022.
- Also verified: raw SQL and `IgnoreQueryFilters()` see only the scoped tenant; raw `INSERT`/`UPDATE` into another tenant fail with a block-predicate error; `DELETE` targeting another tenant affects 0 rows.
- The application login must not be `db_owner` in production and cannot alter the policy (login separation is deployed with the Phase 2 installer scripts; development/test use one login).
- RLS predicates must be index-friendly: every tenant-owned table has `TenantId` as the leading column of its clustered or a covering index (enforced by `TenantIsolationModelTests`).

## 4. Raw SQL, bulk operations, exports

- Raw SQL goes through `ITenantSqlExecutor`, which runs on a tenant-scoped connection (RLS active) and requires a `TenantId` parameter in tenant-owned statements (code review checklist + test helpers).
- `ExecuteUpdate/ExecuteDelete` honor EF filters and RLS.
- Bulk copy (`SqlBulkCopy`, used by Confluence import) runs on a tenant-scoped connection; RLS block predicates reject rows with a wrong `TenantId`.
- Exports and analytics run as tenant jobs (§7) and therefore inherit tenant scope.

## 5. Identifiers

- All ids are `uniqueidentifier` generated by the application with a SQL-Server-ordered sequential generator (avoids page splits).
- IDs are not secrets; every lookup by id is filtered by tenant **and** checked by `IPermissionEvaluator`. An id from another tenant behaves exactly like a non-existent id (`404`), never `403`, to avoid existence disclosure.

## 6. Cross-tenant (system) operations

Only on the system host, by users with a system role, with MFA, through dedicated system endpoints. Each operation:

1. Is a dedicated system endpoint/service (Phase 1: tenant lifecycle, domains, settings, branding) and writes a `SystemAuditLogs` row **in the same transaction** as the change.
2. Touches system tables (`Tenants`, `TenantDomains`, `SystemAuditLogs`) directly, and tenant-owned tables only through an explicit tenant scope for the target tenant (`ITenantScopeFactory.CreateScope`) — never through an RLS bypass.
3. Support access to a tenant's *content* requires a time-boxed grant approved by a tenant administrator ("break-glass" is possible for a second system admin and is flagged to tenant admins).
4. Aggregate analytics returns counts only, never content.

## 7. Background jobs

- Tenant jobs implement `ITenantJob<TArgs>` and are enqueued only through `ITenantJobScheduler`, which captures the envelope from the current `ITenantContext` and throws without one.
- Hangfire executes `TenantJobRunner.RunAsync<TJob, TArgs>(TenantJobContext, TArgs)`: it rejects a missing/empty envelope permanently, throws `TenantUnavailableException` (→ Hangfire retry with back-off) when the tenant is missing or not `Active`, then opens a tenant scope (`TenantResolutionSource.BackgroundJob`), adds tenant id to the logging scope/trace and only then creates the job from that scope. (A runner was chosen over a Hangfire server filter because filters run before Hangfire creates the job's DI scope.)
- Recurring jobs that span tenants (e.g. retention sweep) are system jobs that enumerate active tenants and enqueue one tenant job per tenant; they never touch tenant tables directly.
- Outbox rows carry `TenantId`; dispatchers create a tenant scope per row.

## 8. Testing obligations

Every module ships tenant isolation tests (see `docs/development/TESTING_GUIDE.md` §4): two tenants seeded with lookalike data; tenant A's principal attempts every read/write/search/download/collaboration/notification/export path with tenant B's ids; all must return 404/empty. A model test asserts every `ITenantOwned` entity has a query filter, a `TenantId` index and an RLS policy entry.
