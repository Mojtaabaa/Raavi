# Testing Guide

## 1. Commands

```bash
cd backend && dotnet test Raavi.slnx
```

```bash
cd frontend && npm test
```

## 2. Backend test categories (`backend/Raavi.Tests`)

| Folder | Kind | Notes |
|---|---|---|
| `Architecture/` | Dependency rules | Fails when a project references a forbidden layer/module |
| `Configuration/`, `Tenancy/`, `Search/` | Unit | Pure logic |
| `Api/` | API integration | `WebApplicationFactory<Program>` with the repository sandbox config |
| `Infrastructure/` | `SqlServerFixture` (creates a unique database, applies real migrations incl. RLS, drops it afterwards), `ApiFactory` (WebApplicationFactory with test hosts `admin.raavi.test` / `*.raavi.test`, test system API key, fake DNS), host composition test | Server from `RAAVI_TEST_SQL`, else LocalDB `(localdb)\MSSQLLocalDB`. Never EF in-memory. CI uses a SQL Server 2022 service container. |
| `Isolation/` | Tenant isolation regression | RLS, EF filters, stamping, pooling, cache, jobs — see §4 |
| `Tenancy/` | Tenancy unit + HTTP tests | Resolver, domain model, full API flows |

Assertions use xUnit `Assert` (FluentAssertions ≥ 8 is avoided for licensing).

## 3. Frontend tests (`frontend/tests`)

Vitest + jsdom + @vue/test-utils: components, stores, composables, i18n catalog parity, direction switching, editor extensions (Phase 4: JSON round-trip per extension). E2E with Playwright from Phase 2 (login flows, tenant hosts via `*.raavi.localhost`).

## 4. Tenant isolation regression suite

Seeds tenants **A** and **B** with look-alike data (same space keys, page titles, user e-mails, attachment names). For each module, as a member of A, using B's identifiers, assert 404/empty for:

- read/update/delete pages, versions, comments, attachments (download + pre-signed URL issuance)
- search, suggestions, aggregations, snippets
- collaboration ticket issuance for B's document; WebSocket connect with A's ticket to B's document name
- notifications, activity, analytics
- migration jobs, database records, tasks, calendars, whiteboards
- request bodies containing `tenantId: B` (must be ignored/rejected)
- raw SQL via `ITenantSqlExecutor` and direct SQL under the app login (RLS must return zero rows)
- cache: keys of B never readable from A's scope
- jobs: a job enqueued for A can never write B's rows

Plus model-level checks: every `ITenantOwned` entity has a query filter, `TenantId`-leading index and RLS policy entry.

The suite runs on every CI build and is extended in every phase (a module is not "Tested" without its isolation tests).

## 5. End-to-end scenarios

The 30 scenarios of the product specification (§35) are tracked as Playwright specs per phase; each scenario lists its phase in `docs/IMPLEMENTATION_STATUS.md` when implemented.

## 6. Performance tests

k6 (HTTP, WebSocket) scripts under `deployment/scripts/perf` from Phase 6: 500 concurrent users, 100 editors on one document, 100k pages / 1M attachment metadata data sets. Targets are test targets, not guarantees.
