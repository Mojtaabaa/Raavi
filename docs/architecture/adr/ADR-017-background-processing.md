# ADR-017: Background Processing

**Status:** Accepted · **Date:** 2026-11-06

## Context
Indexing, notifications, imports, automation, scheduled publishing, retention and exports must run reliably, tenant-aware, across multiple nodes.

## Decision
- **Hangfire** with SQL Server storage (schema `hangfire`), server in `Raavi.Workers` (API can enqueue only). Named queues: `critical`, `default`, `indexing`, `import`, `ai`, `maintenance`.
- **Transactional outbox** (`messaging.Outbox`, `search.IndexingOutbox`) written in the same transaction as domain changes; dispatcher polls (or uses SQL change notifications later) and enqueues/handles messages. Consumers are idempotent (dedup by message id).
- **Tenant envelope:** `TenantJobContext(TenantId, InitiatedByUserId, CorrelationId)` is the first argument of every tenant job; a Hangfire server filter validates the tenant and establishes `ITenantContext` + logging scope; missing envelope on a tenant job type → job fails permanently (and alerts). System jobs fan out per tenant.
- Retries with exponential back-off and per-job max attempts; poison jobs go to a dead-letter state visible in admin UI.
- Long jobs (Confluence import) checkpoint and are resumable; cancellation via a persisted flag checked between batches.
- Hangfire dashboard only on the system host, system admins only.

## Alternatives considered
- Quartz.NET — fewer built-in ops features; Hangfire's dashboard and retries fit better.
- Message broker (RabbitMQ/Kafka) — rejected for v1: additional on-prem infrastructure; outbox + Hangfire suffices at target scale.

## Consequences
Hangfire is LGPL-3.0 (used unmodified, see licensing doc). SQL Server carries job load; monitor and size accordingly.
