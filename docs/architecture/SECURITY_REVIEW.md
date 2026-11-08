# Security review notes (Phase 12e)

> **Status:** a *partial, evidence-based self-review*, not an ASVS L2 assessment and not a penetration test. It records what was verified by automated checks in this repository and what was not examined.

## Verified by automated checks (2026-11-08)

| Area (ASVS chapter) | Check | Result |
|---|---|---|
| V14 dependencies | `dotnet list package --vulnerable --include-transitive` over the solution; `npm audit --omit=dev` for frontend (and collaboration earlier) | no known vulnerabilities |
| V4 access control | `EndpointAuthorizationTests`: enumerates all 228 routes of the running API and fails if any lacks authorization metadata and is not in a reviewed list of 24 anonymous routes (health, host context, tenant discovery, 15 sign-in/invitation/setup routes, 5 HMAC-signed collaboration routes, JSON 404, development-only OpenAPI) | pass; a new route that forgets `RequireAuthorization` now breaks the build |
| V4 / V1 multi-tenancy | existing suites: row-level security, tenant isolation model, tenant-scoped services, cross-tenant API tests | pass (see `TESTING_GUIDE.md`) |
| V3 sessions / V8 data | cookies: HttpOnly, SameSite=Strict, Secure (insecure HTTP refused outside Development); external-login binding cookie HttpOnly + Secure | code inspection + existing tests |
| V14 headers | security-headers middleware (Referrer-Policy, Permissions-Policy, COOP/CORP, CSP for API), HSTS | existing `HttpFoundationTests` |
| V6 cryptography | secrets protected with tenant-bound Data Protection; HMAC with timestamps for webhooks and collaboration | existing tests; key ring now in `sandbox/data/keys` (ADR-040) |
| V12 SSRF | outbound hosts: AI (`AllowedHosts`), webhooks (`AllowedWebhookHosts`), generic `OutboundHttp.BlockPrivateNetworks` | existing tests |

## What the authorization test does NOT prove
It proves a route *requires a signed-in principal or policy*, not that its handler checks the right resource permission. Resource-level checks (page/space permissions, owner-only operations) are covered by the feature test suites, not by this guard.

## Not examined / open
* No penetration test, no DAST/fuzzing run, no full ASVS L2 checklist walk-through.
* No scan for secrets or personal data in logs (logging code was written to avoid them; not mechanically verified).
* Upload hardening (content sniffing, antivirus) depends on object storage (7b, deferred) and ClamAV (disabled by default).
* Rate limits exist for anonymous, authenticated and login traffic; abuse behaviour behind a reverse proxy depends on `KnownProxies` being set correctly in each installation.
* Backup encryption path untested (LocalDB); TLS termination, host hardening and database firewalling are deployment responsibilities (`DEPLOYMENT_GUIDE.md`).
* The collaboration WebSocket server and the SPA were not re-reviewed in this step.

## Recommended next
Independent penetration test before production; run ZAP/Nuclei baseline against a staging copy; add a log-scrubbing test (assert no password/token strings in captured logs for the login, AI and webhook flows).
