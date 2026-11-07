# API Conventions

## 1. Basics

- Base path `/api/v1/` (URL segment versioning, ADR-021). Internal endpoints for the collaboration server: `/internal/…`, reachable only on the internal network and requiring service authentication.
- JSON (UTF-8), `camelCase`, ISO 8601 UTC timestamps (`2026-11-06T10:15:00Z`), ids as GUID strings, enums as strings.
- OpenAPI document `/openapi/v1.json` (development; exposed in production only to authenticated administrators).

## 2. Tenant context

- The tenant is **always** the one resolved from the host. No endpoint accepts `tenantId` in path, query or body for tenant-scoped operations.
- Each endpoint declares its tenancy requirement in code and in OpenAPI (`x-raavi-tenancy`): `tenant` (requires resolved tenant), `system` (system host only), `discovery` (system host, may accept tenant code and redirect), `anonymous-tenant` (resolved tenant, no login, e.g. login provider list).
- A request on the wrong host type returns `404`.

## 3. Requests and responses

- Commands: `POST` create, `PUT` full replace (rare), `PATCH` (JSON Merge Patch) partial update, `DELETE`.
- Success: resource or `{ "data": …, "meta": … }` for collections.
- Collections: cursor pagination `?limit=50&cursor=…` (max 200); response `meta: { nextCursor, limit }`. Offset pagination only for admin grids (`?page&pageSize`, max 100).
- Filtering `?filter[status]=published&filter[updatedAfter]=…`; sorting `?sort=-updatedAt,title` (whitelisted fields).
- Optimistic concurrency: `ETag` (from `RowVersion`) on reads, `If-Match` required on updates of versioned aggregates → `412` on mismatch.
- Idempotency: `Idempotency-Key` header supported (and required for import/export start, payments-like operations, webhooks) — stored 24 h per tenant + user.

## 4. Errors (RFC 9457)

```json
{
  "type": "https://raavi.dev/errors/space.key_taken",
  "title": "Space key already in use",
  "status": 409,
  "code": "space.key_taken",
  "detail": "…localized…",
  "instance": "/api/v1/spaces",
  "correlationId": "4f8c…",
  "errors": { "key": ["space.key_taken"] }
}
```

- `code` is stable and localizable; `detail` is localized from `Accept-Language` / user preference.
- `404` for resources the caller cannot see (incl. other tenants), `403` only when the caller can see the resource but not perform the action.
- Never include stack traces, SQL, or internal identifiers of other tenants.

## 5. Headers

- Request: `X-Correlation-ID` (optional, `[A-Za-z0-9._-]{8,64}`), `X-XSRF-TOKEN` (state-changing requests with cookie auth), `Accept-Language`, `If-Match`, `Idempotency-Key`.
- Response: `X-Correlation-ID`, `ETag`, `RateLimit-*` (draft standard) / `Retry-After` on `429`.

## 6. Security

- Cookie auth for the SPA (host-only), Bearer API tokens for automation.
- Resource-based authorization in handlers via `IPermissionEvaluator`; endpoint policies only for coarse checks.
- DTOs explicit; EF entities never serialized. Server-controlled fields (`id`, `tenantId`, `createdBy`, timestamps, `status` transitions) are not bindable.
- Body size limits per endpoint group; uploads use dedicated streaming endpoints.

## 7. Real-time

SignalR hub `/hubs/events` (tenant-scoped groups `t:{tenantId}:u:{userId}`, `t:{tenantId}:content:{id}`); clients join content groups only after a server-side View check.
