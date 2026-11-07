# Security Architecture

Baseline: **OWASP ASVS 4.0.3 Level 2** (re-evaluate against ASVS 5.0 at Phase 12).

## 1. Threat model highlights

| Threat | Primary controls |
|---|---|
| Cross-tenant disclosure / TenantId manipulation | `TENANT_ISOLATION.md` (12 layers, RLS, no TenantId in DTOs, 404-on-foreign-id) |
| Host header injection / subdomain spoofing | Strict normalization (`TenantHost`), forwarded headers only from known proxies, system host separate, verified custom domains only |
| Domain takeover of custom domains | DNS TXT verification, periodic re-verification, quarantine after removal |
| IDOR | Every resource access goes through `IPermissionEvaluator`; ids are not authorization |
| Privilege escalation | Central authorization, role changes audited, tenant admins cannot grant system roles, permission management requires `ManagePermissions` at the scope |
| Provider confusion / account linking attacks / identity collisions | Identity key `(provider, subject)`, no e-mail auto-linking, state bound to tenant + provider, single-use PendingLogin |
| Malicious Confluence import / stored XSS | Imported markup parsed as XML into an AST and converted node-by-node; no HTML passthrough; unsupported content stored as escaped text; attachments re-validated (MIME sniffing) |
| Unsafe macros / embeds | Extension allow-list, server-side validation, sandboxed rendering |
| Collaboration ticket abuse | 60 s single-use ES256 tickets bound to user/tenant/document, replay cache, revocation |
| Search ACL bypass | Mandatory tenant + ACL filters in the only query builder; snippets generated only from authorized hits |
| AI context leakage | RAG retrieval uses the same ACL-filtered search; prompts include only authorized chunks; per-tenant provider keys; no cross-tenant caches |
| Plugin privilege escalation | Untrusted plugins run out-of-process with declared permissions (ADR-015) |
| Webhook abuse / SSRF | SSRF-guarded HTTP client (§5), HMAC-signed outbound webhooks, rate limits |
| Session hijacking | Host-only `__Host-` cookies, Secure/HttpOnly, rotation, idle/absolute timeouts, server-side revocation |
| Brute force | Rate limiting per IP and per account, lockout, MFA for admins |

## 2. Input validation and output encoding

- FluentValidation on every command DTO; size limits on all strings/collections; request body limits per endpoint.
- JSON only (no XML endpoints except the Confluence importer, which uses `XmlReader` with `DtdProcessing.Prohibit`, `XmlResolver = null` to block XXE).
- Vue templates escape by default; `v-html` is forbidden by lint rule except in the sanitized content renderer component.

## 3. Data protection and secrets

- ASP.NET Core Data Protection keys persisted to a shared, encrypted location (file system/SQL) and protected with a certificate.
- Integration credentials (Confluence source, tenant integration tokens) encrypted at rest with envelope encryption (data key per tenant protected by a master key from the secret store).
- Secrets never in `sandbox/config` files in production, never logged, never returned to the frontend (APIs return `hasSecret: true`).
- TLS 1.2+ everywhere; internal service traffic over TLS in HA deployments.

## 4. File uploads

Extension deny-list + MIME sniffing (content-based, not the client header) + size and quota limits + optional ClamAV scan (quarantine until clean) + randomized storage keys + `Content-Disposition: attachment` for non-previewable types + previews served from a separate cookieless origin or with `Content-Security-Policy: sandbox` + SVG served only as attachment.

## 5. Outbound HTTP (SSRF)

`IGuardedHttpClientFactory` used by integrations, webhooks, AI providers, Confluence REST, link previews: resolves DNS, rejects private/loopback/link-local/metadata ranges (unless explicitly allow-listed in `security.json` — on-prem Jira/Confluence are typically allow-listed hosts), re-checks after redirects (max 3), timeouts, response size limits.

## 6. HTTP security headers

API (implemented in `SecurityHeadersMiddleware`): `X-Content-Type-Options: nosniff`, `X-Frame-Options: DENY`, `Referrer-Policy: strict-origin-when-cross-origin`, `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'`, `Permissions-Policy`, `Cross-Origin-Opener-Policy: same-origin`, `Cross-Origin-Resource-Policy: same-origin`, HSTS outside development.

SPA (reverse proxy): `Content-Security-Policy: default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data: blob:; connect-src 'self' wss://<collab host>; frame-ancestors 'none'; object-src 'none'; base-uri 'self'` (PrimeVue styled mode injects styles; `unsafe-inline` for styles only, revisit with nonces in Phase 12).

## 6a. Cookies, CSRF and one-time links (Phase 2a)

- Session cookie `__Host-raavi` and antiforgery cookie `__Host-raavi-af`: `Secure`, `HttpOnly`, `Path=/`, no `Domain`; session `SameSite=Lax`, antiforgery `SameSite=Strict`. Verified in a browser that neither is readable from JavaScript.
- CSRF: `X-XSRF-TOKEN` is required on every non-GET `/api` request without a Bearer header, including login and invitation acceptance (login CSRF). Tokens are identity-bound and refetched after sign-in/out. Bearer requests are never authenticated by the cookie.
- Invitation and setup tokens travel in the URL fragment (`#token=`), are removed from the address bar on load, are stored only as SHA-256 hashes, are single-use and expire after 72 h.
- TOTP secrets are protected with ASP.NET Core Data Protection (`Raavi:Security:DataProtection:KeyRingPath` must point to a shared, backed-up key ring in multi-node deployments). QR codes are rendered in the browser; no external QR service is used.

## 7. Rate limiting

ASP.NET Core rate limiter, partitioned by tenant + user (authenticated) or IP (anonymous); stricter policies for login, password reset, ticket issuing, search, AI and export endpoints. Redis-backed counters for multi-node consistency.

## 8. Logging and audit

- Never log: passwords, tokens, cookies, authorization headers, secrets, page content, attachment content, AI prompts/responses (only metadata).
- `SecurityEvents` (append-only; tenant-scoped or system) separate from `ActivityLogs`.
- Audit write failures for security-critical operations fail the operation.

## 9. Supply chain

Pinned dependency versions (`--save-exact`, NuGet central versions in Phase 1), `dotnet list package --vulnerable` and `npm audit` in CI, SBOM generation (CycloneDX) per release, licence review (`DEPENDENCY_AND_LICENSING.md`), container images from minimal bases, non-root users.

## 10. Backups

Encrypted backups (SQL Server TDE/backup encryption, object storage server-side encryption), keys stored separately from backups, restore tested (see `docs/deployment/BACKUP_AND_RESTORE.md`).
