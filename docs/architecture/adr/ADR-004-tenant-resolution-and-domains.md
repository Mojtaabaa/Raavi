# ADR-004: Tenant Resolution and Domain Management

**Status:** Accepted · **Date:** 2026-11-06

## Context
Tenants are reached via `slug.{baseDomain}` or custom domains. Host headers are attacker-controlled input.

## Decision
- A single `TenantResolutionMiddleware` (Phase 1) using `TenantHost` normalization (implemented and tested in Phase 0) and `ITenantResolver`.
- Precedence: **system host → verified custom domain → single-label subdomain of a configured base domain → explicit tenant code (discovery endpoints only, results in redirect)**. Anything else → 404.
- Forwarded headers honoured only from `KnownProxies/KnownNetworks`. No custom tenant headers are trusted.
- Conflicting identifiers → reject + security event.
- Inactive tenants → generic unavailable response (no existence disclosure).
- Resolution is cached (memory + Redis) with invalidation on domain/status changes; tenant status is also re-checked against the session.
- Custom domains require DNS TXT verification (`_raavi-verify.<host>`), are globally unique, re-verified periodically, quarantined after removal.
- Slugs: reserved-word list, rename only by system admin with redirect grace period.
- Cookies are host-only (`__Host-` prefix) so sessions never cross tenant hosts.
- Development uses `*.raavi.localhost` (resolves to loopback in modern browsers).

## Alternatives considered
- Path-based tenants (`/t/acme/…`) — rejected: cookies and CSP become shared across tenants; weaker isolation.
- Tenant selection header from the SPA — rejected: trivially spoofable.
- HTTP-file domain verification — possible later; DNS TXT is standard and works before TLS exists.

## Consequences
Operators must provide wildcard DNS/TLS for base domains and certificates for custom domains (DEPLOYMENT_GUIDE). OAuth callbacks need per-host or relay handling (AUTHENTICATION_ARCHITECTURE §4).
