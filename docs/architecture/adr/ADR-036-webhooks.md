# ADR-036 — Outbound webhooks (Phase 11d)

**Status:** Accepted (2026-11-08)

## Context

Phase 11 lists integrations and webhooks. Outbound webhooks are the smallest integration that serves every external system (chat, ERP, ticketing, automation tools) and reuse what exists: the transactional outbox, per-tenant secrets (as in ADR-034) and the host allow-list pattern. Named integrations (Jira, GitHub, Teams, …) are later slices; they will use the same SSRF-guarded client approach.

## Decisions

1. **Module and storage.** `Raavi.Integrations` owns `IntegrationsDbContext` (schema `integrations`, RLS, migration applied by the Migrator): `Webhooks` (name, URL, protected secret, comma-separated events, optional space, enabled, failure counter, revision) and `WebhookDeliveries` (one row per webhook and event: status, HTTP status, attempts, duration, stable error code — **no** request or response body). Up to 20 webhooks per tenant.
2. **Who and what.** Only tenant administrators manage webhooks (create, edit with ETag, enable/disable, rotate the secret, test, delete, read the delivery log). A webhook subscribes to a fixed list of events (space, page, comment, approval events; `ping` is for tests only) and may be limited to one space. The module is registered in the API and in the workers (the outbox poller runs there).
3. **Payload = identifiers only.** `{ id, type, occurredAt, tenant, data }` where `data` contains only ids (`spaceId`, `contentId`, `actorId`, `requestId`, `commentId`, `threadId`), the page `version` and an approval `status` — taken from the outbox event by a whitelist. No titles, excerpts or text: a webhook is a system-level consumer that cannot be subject to per-user page restrictions, so it must never learn the *content* of restricted pages; the receiver fetches what it needs through the normal API with its own credentials.
4. **Signing.** Each webhook has a secret (`whsec_` + 32 random bytes), shown **once** (creation or rotation, `Cache-Control: no-store`), stored only protected (Data Protection, tenant-bound purpose), never returned or logged. Every request carries `X-Raavi-Event`, `X-Raavi-Delivery` (= the event id, for de-duplication) and `X-Raavi-Signature: t=<unix>,v1=<hex HMAC-SHA256(secret, "<t>.<body>")>`; receivers must check the signature and reject old timestamps.
5. **Egress policy (SSRF).** The installation lists permitted hosts (`Raavi:Integrations:AllowedWebhookHosts`, empty = webhooks off). Loopback, link-local (cloud metadata), multicast and unspecified addresses are refused even if listed and re-checked at connect time after DNS resolution; redirects are never followed; the policy is applied when saving and again when sending; credentials in the URL and fragments are refused; the response body is never read.
6. **Delivery semantics.** `WebhookDispatcher` is an outbox handler: at-least-once becomes **idempotent per (webhook, event)** through the delivery log — a delivered event is never sent again. A failure (5xx, timeout, unreachable, 408/429) makes the handler throw so the outbox retries with its back-off (up to 10 attempts); webhooks that already succeeded are skipped on the retry and one failing webhook never holds back the others. A permanent client error (other 4xx) is recorded and **not** retried. After 20 consecutive failures the webhook is switched off (`repeated_failures`) and must be enabled again by an administrator, which resets the counter.
7. **Ordering and timing.** Delivery order across events is not guaranteed (retries reorder); receivers should use `occurredAt` and the ids. Delivery happens after the originating change committed (outbox), typically within seconds.

## Not in 11d

Payload customisation or templates, per-event filters beyond type and space, batching, a dead-letter replay button, inbound webhooks, delivery rate limits per receiver, receiver-side secrets rotation overlap (old and new secret valid together), webhook delivery for events outside the list (workflow runs, AI usage).
