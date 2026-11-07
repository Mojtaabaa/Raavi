# ADR-003: Multi-Tenant Architecture

**Status:** Accepted · **Date:** 2026-11-06

## Context
Multiple organizations share one installation; each needs its own users, spaces, content, configuration, branding and domains. Multi-tenancy cannot be retrofitted.

## Decision
- **Shared database, shared schema, `TenantId` discriminator** as default (details in ADR-005).
- **Global users + tenant memberships**: identities are global (one person, many tenants), access is per-tenant membership; tenant admins see only their members.
- Tenant identified by **host** (subdomain or verified custom domain); tenant code only for discovery (ADR-004).
- **Configuration split**: global settings in `sandbox/config` files + environment/secret store; tenant settings in SQL Server typed tables (`SYSTEM_ARCHITECTURE.md` §6). Tenant settings may only narrow global security policy.
- **Global identity provider definitions** (config) vs **tenant provider assignments/policies** (DB) — ADR-006.
- **System host** (`Raavi:Hosting:SystemHost`) for system administration; never resolves to a tenant.
- Data model keeps tenant-owned data self-contained (no cross-tenant FKs; tenant-owned rows reference only system rows and same-tenant rows) so a future **database-per-tenant** mode only needs a connection resolver per tenant, not a domain rewrite.

## Alternatives considered
- Database per tenant by default — rejected: heavy operations (migrations × N, connection pools) for typical on-prem installations with few-to-dozens tenants; kept as a future option.
- Schema per tenant — rejected: EF Core model caching complexity, no real isolation gain over RLS.
- Per-tenant users (same e-mail = different accounts per tenant) — rejected: poor UX for people in multiple tenants and duplicated external identities; privacy preserved by membership scoping instead.

## Consequences
Every query path must be tenant-aware; mitigated by the layered isolation in ADR-005 and the isolation regression suite.
