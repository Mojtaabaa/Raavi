# ADR-039 — Performance baseline and load tests (Phase 12c)

**Status:** Accepted (2026-11-08)

## Context
The test guide promised k6 scripts and targets (500 concurrent users, 100k pages) but nothing measured the API. We needed a repeatable baseline and an N+1 check without new infrastructure.

## Decisions
1. **In-process load tests** (`Raavi.Tests/Performance/LoadTests.cs`, `Category=Load`) reuse the existing API test host, SQL fixture and users. They are no-ops unless `RAAVI_LOAD=1`, so the normal suite stays fast and deterministic (full suite: 425 passed).
2. **Closed-loop weighted mix** (page, tree, quick-find, comments, unread-count, spaces, new comment) with a warm-up that is excluded, per-endpoint p50/p95/p99/max, JSON report; asserts only "no 5xx" — latency thresholds are not asserted because they depend on the machine.
3. **N+1 check** via SQL Server plan-cache `execution_count` deltas per request (1–13 statements for hot reads, none growing with item count).
4. **No optimisation made**: nothing wrong was found. Candidates if needed: per-tenant tree caching, merging statements in page read.
5. **k6 not written.** It needs a separate server and generator to be meaningful; the in-process results are explicitly not a capacity claim.

## Consequences / limits
Measured on an i5-3470 (4 cores), LocalDB, client and server sharing the CPU, up to 80 users / 4 000 pages (198 req/s, 0 errors, p99 ≤ ~1.1 s). The 500-user / 100k-page / attachment / WebSocket / OpenSearch targets are not demonstrated. See `docs/development/PERFORMANCE_BASELINE.md`.
