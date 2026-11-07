# ADR-011: Real-Time Collaboration

**Status:** Accepted · **Date:** 2026-11-06

## Context
Up to 100 simultaneous editors per document, presence, offline tolerance, permission revocation, durable persistence.

## Decision
- **Yjs** CRDT + **Hocuspocus** server (Node.js LTS) with Redis extension for multi-node; Tiptap collaboration + caret extensions on the client; `y-indexeddb` for offline cache.
- **Short-lived signed tickets** (ES256 JWS, ≤ 60 s, single-use `jti`) issued by the API after tenant, membership and View/Edit checks; claims bind user, tenant, document, permission and session. Document names are addresses, not credentials.
- Hocuspocus has **no direct database access**: it loads/stores through authenticated internal API endpoints, which re-validate tenant/document and write `YjsUpdates`/`CollaborationSnapshots`. Batched, debounced incremental updates; periodic snapshots and compaction.
- Revocation via Redis pub/sub + periodic re-checks; connections closed or downgraded to read-only.
- Collaboration history is separate from published version history (ADR-019). Publishing converts Y state to Document JSON (y-prosemirror on the Node side) and the API validates and stores it.
- General (non-document) real-time events use **SignalR** with a Redis backplane.
Full design: `COLLABORATION_ARCHITECTURE.md`.

## Alternatives considered
- Direct SQL access from Hocuspocus — rejected: duplicates tenant isolation logic in a second runtime.
- OT (ShareDB) — rejected: Yjs is mandated and better suited to offline merge.
- Long-lived collaboration JWTs — rejected: revocation latency.

## Phase 6 outcome (2026-11-07)

Implemented as designed with these decisions: the Node server converts Y state ⇄ Document JSON with `@tiptap/y-tiptap` and a ProseMirror schema built from the shared manifests; every stored update also carries the converted JSON, which the API validates and writes into the shared `PageDraft` (publishing therefore keeps working from the draft, ADR-019; the API flushes the server before publishing). API → server calls (flush, reset, re-check) are HMAC-signed HTTP requests; the Redis channel for multi-node revocation is not implemented yet (single collaboration node). Tickets are signed with a key from `Raavi:Collaboration:SigningKeyPath` (ephemeral in Development only) and verified with the public key from the signed internal JWKS endpoint. Snapshots are stored inline in SQL Server for now.

## Consequences
Persistence throughput depends on the internal API; batching and snapshotting keep write volume bounded. A load test (100 editors) is part of Phase 6 acceptance.
