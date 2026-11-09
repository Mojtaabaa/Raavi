# Tenancy API (Phase 1)

All paths are under `/api/v1`. Tenancy column: **system** = system host only (404 elsewhere, checked before authentication), **any** = every host, **tenant** = resolved tenant host.

| Method | Path | Host | Auth | Purpose |
|---|---|---|---|---|
| GET | `/host/logo` | tenant | anonymous | The workspace's square logo (PNG/JPEG/WebP), `branding.logoVersion` from `/host/context` is its cache key (`?v=`); 404 when none. `public, max-age=3600`, ETag, `nosniff`. |
| GET | `/host/context` | any | anonymous | SPA bootstrap: `{kind: "tenant", tenant: {slug, displayName{fa,en}, defaultLocale, timeZone, calendar, branding}}` or `{kind: "system"}`. No internal ids. Unknown/inactive host → 404 `tenant.unavailable`. |
| GET | `/discovery/tenants/{code}` | system | anonymous, rate-limited (20/min/IP) | Login discovery by tenant code → `{url}` of the canonical host (primary verified domain, else `slug.{firstBaseDomain}`). Inactive/unknown → 404. |
| POST | `/system/tenants` | system | system admin | Create + provision + activate. Body: `code, slug, displayNameFa, displayNameEn, defaultLocale?, timeZone?, calendar?` → 201 `TenantDto`. Errors: `tenant.code_invalid`, `tenant.slug_invalid`, `tenant.slug_reserved`, `settings.*` (400); `tenant.code_taken`, `tenant.slug_taken` (409). |
| GET | `/system/tenants?status=&skip=&take=` | system | system admin | Paged list (take ≤ 100). |
| GET | `/system/tenants/{id}` | system | system admin | One tenant. |
| POST | `/system/tenants/{id}/provision` | system | system admin | Retry provisioning of a tenant stuck in `Provisioning` (idempotent). |
| POST | `/system/tenants/{id}/activate` | system | system admin | Provisioning/Suspended/Deactivated → Active. |
| POST | `/system/tenants/{id}/suspend` | system | system admin | Body `{reason}`. Active → Suspended; access stops on this node immediately, cluster-wide within the cache L1 lifetime (≤ 15 s). |
| POST | `/system/tenants/{id}/deactivate` | system | system admin | Body `{reason}`. Active/Suspended → Deactivated. |
| GET | `/system/tenants/{id}/domains` | system | system admin | Live custom domains with the TXT record to publish. |
| POST | `/system/tenants/{id}/domains` | system | system admin | Body `{host}`. Normalized (IDN→punycode, lower-case). Rejects platform hosts, duplicates (409 `domain.taken`), hosts quarantined after removal from another tenant (409 `domain.quarantined`), > 10 per tenant. |
| POST | `/system/tenants/{id}/domains/{domainId}/verify` | system | system admin | DNS TXT check now (also done hourly by the worker). |
| POST | `/system/tenants/{id}/domains/{domainId}/primary` | system | system admin | Only verified domains. |
| DELETE | `/system/tenants/{id}/domains/{domainId}` | system | system admin | Stops resolution immediately; host quarantined for other tenants (30 days). |
| GET/PUT | `/system/tenants/{id}/settings` | system | system admin | `defaultLocale (fa/en), timeZone (IANA), calendar (persian/gregorian), storageQuotaBytes?` — executed inside an explicit tenant scope. |
| PUT / DELETE | `/system/tenants/{id}/logo` | system | system admin | Multipart `file`: PNG, JPEG or WebP by content, ≤ 256 KB (`logo.type_invalid`, `logo.too_large`); returns the new version. |
| GET | `/system/tenants` | system | system admin | Each item also carries `url`, the canonical address of the workspace. |
| PUT | `/system/tenants/{id}/branding` | system | system admin | `primaryColor, secondaryColor (#rrggbb), welcomeMessageFa/En (plain text ≤ 500)`. |

## System administrator authentication (Phase 1 bootstrap)

Phase 2 introduces MFA-protected administrator sign-in. Until then, operators use **system API keys**:

1. Generate a long random key and keep it in your secret store.
2. Configure only its SHA-256 hash (never the key) via environment variables:

```bash
export RAAVI_Raavi__Security__SystemApiKeys__0__Id=ops
export RAAVI_Raavi__Security__SystemApiKeys__0__Sha256=$(printf %s "$KEY" | sha256sum | cut -d' ' -f1)
```

3. Call the system host with `Authorization: Bearer <key>`. Keys are ignored on tenant hosts. Every action is written to `system.SystemAuditLogs` with actor `apikey:<Id>`.

## Tenant identification rules

- The tenant is never taken from a request body/query. Request DTOs have no `tenantId`; extra JSON fields are ignored (tested).
- Headers `X-Tenant-Id`, `X-Tenant-Code`, `X-Tenant` never select a tenant. If present they must match the host-resolved tenant, otherwise `400 tenant.conflict` (logged as a security event, EventId 4001). On the system host they are always rejected.
