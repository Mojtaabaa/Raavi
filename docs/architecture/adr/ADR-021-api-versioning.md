# ADR-021: API Versioning and Compatibility

**Status:** Accepted · **Date:** 2026-11-06

## Decision
- URL-segment major versions (`/api/v1`, `/api/v2`) using Asp.Versioning.Http; OpenAPI document per version (`Microsoft.AspNetCore.OpenApi`).
- Within a major version only additive, backward-compatible changes (new endpoints, optional fields, new enum values that clients must tolerate). Breaking changes → new major version; previous major supported ≥ 12 months after the next is GA, announced with `Deprecation` and `Sunset` headers.
- Internal endpoints (`/internal/…`) are versioned with the collaboration server release and not part of the public contract.
- Webhook payloads carry `schemaVersion`; plugin manifests declare compatible API ranges.
- Contract tests: OpenAPI snapshot diffed in CI; breaking changes fail the build unless the version is bumped.
- Conventions: `docs/api/API_CONVENTIONS.md`.

## Alternatives considered
Header/media-type versioning — rejected: harder to use from scripts and to route at the proxy.
