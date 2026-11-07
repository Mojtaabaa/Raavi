# ADR-008: Confluence Import Architecture

**Status:** Accepted, pending verification against the source Confluence (Q2) · **Date:** 2026-11-06

## Context
Customers migrate from self-hosted Confluence and require full page history, attachments, links, authors and timestamps. Sources vary by version and access method.

## Decision
- Dedicated module `Raavi.Migration` with contracts in code: `IConfluenceSourceProvider` (per source kind and version range), `IConfluenceContentConverter`, `IMacroConverter` registry; to be added: `IConfluenceIdentityMapper`, `IConfluencePermissionMapper`, `IConfluenceMigrationExecutor`, `IConfluenceMigrationValidator`.
- Source adapters (REST API, site XML backup, space XML export) produce a **neutral model** into tenant-scoped **staging tables**; mapping, dry run, execution and validation operate on staging. Full details: `docs/migration/CONFLUENCE_MIGRATION.md`.
- Streaming everywhere (`XmlReader`, chunked HTTP, streamed attachment copy); no full-archive load.
- Every page version imported as its own `PageVersion` with source identifiers and provenance.
- Conversion parses storage format as XML into an AST, emits validated Raavi Document JSON; unsupported macros → `raavi.unsupported` placeholder with escaped source + warning (never dropped).
- Idempotency via `MigrationSourceMappings` unique key; checkpoints per space/phase; Hangfire jobs with `TenantJobContext` fixed at job creation.
- Permissions never widened; unrepresentable restrictions → restricted to space admins + warning.

## Alternatives considered
- Importing only the HTML export — rejected: loses history, macros, metadata.
- Direct DB-to-DB copy from Confluence's database — rejected: undocumented schema, version-fragile, needs DB access.
- Rendering storage format to HTML and importing HTML — rejected: XSS risk, lossy.

## Consequences
Version-specific adapters must be maintained; adapter detection fails loudly on unknown versions. Large imports need object-storage capacity planning (dry-run estimate).
