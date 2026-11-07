# ADR-023: Wiki Core Module and Transactional Outbox

**Status:** Accepted · **Date:** 2026-11-07

## Context
MODULE_BOUNDARIES.md placed the core wiki domain in `Raavi.Domain`/`Raavi.Application` with persistence in `Raavi.Infrastructure`. `Raavi.Application` must not reference EF Core, so use cases needing queries would have to go through repository abstractions, and `Raavi.Infrastructure` would then own business persistence. Tenancy and Identity proved the feature-module pattern (domain + persistence + services + endpoints in one assembly, own schema and migrations, isolation conventions from `RaaviDbContextBase`). Phase 3 also needs the outbox moved from Phase 1 and group/membership data owned by Identity.

## Decision
- The wiki core is the feature module **`Raavi.Wiki`**: spaces (`spaces` schema), content tree, versions, labels, templates, activity and outbox (`content`), permission grants and restrictions (`security`). It follows the module rules (no references to other feature modules; enforced by `DependencyRulesTests`).
- Identity data reaches the wiki only through `IPrincipalDirectory` (Application abstraction, implemented by Identity): membership, tenant-admin role and group ids of the current tenant, names for display, and a tenant-scoped directory search. Group membership is read per request (no cross-request cache) so revocations take effect immediately.
- `WikiAuthorizer` implements `IPermissionEvaluator` (v1 of ADR-012).
- **Outbox** (generic, `Raavi.Infrastructure.Messaging`): each module maps an `OutboxMessage` table in its own schema and writes it in the same `SaveChanges` as the change. The table is **global (no RLS)** by design — only the dispatcher reads it, payloads carry ids and title snapshots only — and every handler runs in a scope bound to the row's tenant, so RLS applies to everything the handler touches. Dispatch claims rows with `UPDLOCK, READPAST` (several workers can run), defers rows of non-active tenants, retries with exponential back-off and marks rows failed after 10 attempts. Delivery is at-least-once; handlers are idempotent (activity entry id = event id).
- Concurrency tokens are explicit revisions (`Revision`, exposed as `ETag: "r{n}"`) rather than `rowversion`, so sibling re-numbering during reorder does not invalidate concurrent editors.

## Consequences
MODULE_BOUNDARIES.md lists `Raavi.Wiki`. Later modules (Search, AI, Notifications) consume wiki events through outbox handlers or Application abstractions, never through the wiki tables. The Workers host must run for activity (and later indexing/notifications) to be produced.
