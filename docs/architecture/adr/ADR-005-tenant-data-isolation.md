# ADR-005: Tenant Data Isolation

**Status:** Accepted · **Date:** 2026-11-06

## Context
A single missed `WHERE TenantId = …` must not leak data.

## Decision
Layered enforcement, documented in `TENANT_ISOLATION.md`:
1. Immutable `ITenantContext` from trusted resolution only.
2. No `TenantId` in request DTOs.
3. EF Core global query filters on all `ITenantOwned` entities (by convention, verified by a model test).
4. SaveChanges interceptor: stamps TenantId on insert, forbids mismatches and modifications.
5. Composite unique keys and composite FKs including `TenantId`.
6. **SQL Server Row-Level Security** with `SESSION_CONTEXT('TenantId')` set read-only on every connection open; no bypass predicate.
7. Tenant-prefixed cache keys, object keys, search routing + filter, job envelopes.
8. Foreign ids behave as not found (404).
9. System operations touch tenant data only inside an explicit, audited tenant scope (`ITenantScopeFactory`), one tenant at a time.

**Phase 1 outcome:** RLS behaviour with pooled connections was verified by `RowLevelSecurityTests` (read-only `SESSION_CONTEXT` is cleared by `sp_reset_connection`; no cross-tenant carry-over across 75 alternating scope reuses), so no pool partitioning is needed. The draft `SystemScope` bypass was dropped: system operations on tenant data use explicit, audited tenant scopes.

## Alternatives considered
- EF filters only — rejected (raw SQL / bulk / reporting bypass).
- RLS only — rejected (late failure, poor diagnostics, no protection in caches/search/storage).

## Consequences
Small per-query overhead from RLS predicates (mitigated by `TenantId`-leading indexes). Migrations must register every tenant table with the security policy; a test enforces it.
