# ADR-020: Database and Migration Strategy

**Status:** Accepted · **Date:** 2026-11-06

## Context
SQL Server 2022+ is mandated; multiple modules share one database; zero-downtime-ish upgrades on-prem.

## Decision
- EF Core 10 code-first; one DbContext + migrations assembly per module schema (`system`, `identity`, core schemas, `migration`, `search`, `ai`, `integration`); Hangfire manages its own schema.
- Migrations are applied by the `raavi-migrator` container/CLI (`dotnet Raavi.Migrator.dll migrate`), never automatically by the API in Production. Development may auto-migrate behind a flag.
- Expand/contract pattern for breaking changes across two releases.
- RLS predicates and security policy membership are part of migrations; a test asserts every tenant-owned table is covered.
- Conventions in `DATABASE_DESIGN.md` (sequential GUIDs, `TenantId`-leading indexes, composite FKs, UTC `datetime2`, rowversion, JSON with ISJSON checks).
- Collation: database default `Latin1_General_100_CI_AS_SC`; BIN2 for machine keys; locale-aware sorting handled by search/application; per-column Persian collation evaluated in Phase 3.
- Two SQL logins: `raavi_app` (DML only, no ALTER, RLS enforced) and `raavi_system` (system-scope operations, audited), plus `raavi_migrator` (DDL) used only by the migrator.
- Database integration tests run against real SQL Server (Testcontainers `mcr.microsoft.com/mssql/server:2022-latest` in CI; a local instance otherwise) — no in-memory provider for persistence tests.

## Consequences
Multiple migration histories must be applied in dependency order (system → identity → core → others); the migrator encodes the order.
