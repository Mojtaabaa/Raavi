# ADR-001: Overall Architecture

**Status:** Accepted · **Date:** 2026-11-06

## Context
Raavi must offer a Confluence-Cloud-class feature set, fully on-premises, multi-tenant, with real-time co-editing, operable by customer IT teams without a SaaS control plane.

## Decision
- Primary application: ASP.NET Core 10 **modular monolith** (API host + Worker host sharing modules) — ADR-002.
- Dedicated **collaboration server** (Hocuspocus on Node.js) because the Yjs ecosystem (y-prosemirror, Hocuspocus) is JavaScript-native — ADR-011.
- **SPA**: Vue 3 + TypeScript + PrimeVue (customized Raavi preset and design tokens), Tiptap 3 editor.
- **Stores**: SQL Server (system of record), Redis-protocol cache (Valkey/Redis), OpenSearch, S3-compatible object storage.
- **Same-origin** deployment behind a reverse proxy per tenant host: `/` SPA, `/api` API, `/hubs` SignalR, `/collab` WebSocket. This keeps cookies host-only and avoids CORS.
- Asynchronous work via outbox + Hangfire — ADR-017.

## Alternatives considered
- Microservices per bounded context — rejected: operational burden for on-prem customers, distributed transactions, no scaling need that a monolith cannot meet at the target scale (5k users / 500 concurrent).
- .NET-only collaboration (YDotNet / Ycs) — rejected for v1: smaller ecosystem and less maturity than Hocuspocus; revisit if a mature .NET Yjs port emerges.
- Server-rendered UI — rejected: the editor, whiteboards and collaboration require a rich client.

## Consequences
Two runtimes (.NET and Node.js) must be built, secured and monitored. The collaboration server holds no business rules; it delegates every authorization and persistence decision to the API.
