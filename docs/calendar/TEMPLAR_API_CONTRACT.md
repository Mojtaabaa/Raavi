# Templar Calendar API Contract (required)

**Status:** Blocked — live integration waits on this contract  
**Product:** V1 requirement (not removed from scope)  
**Raavi side:** Connector/provider shell + config + errors; no fake production endpoints; calendar core works without Templar

## Purpose

Raavi needs a read path (and later optional write path) to surface Templar-originated schedule items on the enterprise calendar without treating Templar rows as Raavi-owned authority when deleting calendars.

## Configuration Raavi will expose

| Setting | Source | Notes |
|---|---|---|
| Base URL | Tenant or system setting | Absolute HTTPS URL |
| Authentication | Secret store / `RAAVI_*` | No secrets in source, frontend, or logs |
| Enabled | Derived | Disabled + clear UI when Base URL or auth missing |
| Timeout / retry | Config | Bounded; failures are typed errors, not silent empty feeds |

## Contract Raavi needs from Templar

Until Templar publishes a stable contract, Raavi will not call a live host. The following is the **minimum** surface expected; Templar may name resources differently if the mapping is documented.

### Authentication

- Machine-to-machine credential (API key, OAuth client credentials, or mTLS) suitable for on-premises.
- Document token lifetime, refresh, and clock skew.
- Raavi must never forward end-user passwords.

### Read calendar / events (required for V1 live)

- List events in a time window (from/to, time zone aware).
- Stable event id, title, start/end (or all-day), time zone, recurrence (if any), status, last-modified.
- Pagination or a hard page size.
- Error codes for unauthorized, not found, validation, upstream unavailable.

### Optional later (architecture reserved)

- Create / update / delete with Templar permission checks (write-back).
- Webhooks for incremental sync.
- Free/Busy only payloads.

### Non-goals for Raavi until contract exists

- Mocking a “real” Templar API in production.
- Hard-coding assumed URL paths or payloads that are not agreed.

## Error handling (Raavi)

- Unconfigured → provider disabled, calendar UI remains usable.
- Configured but unreachable → typed error / degraded overlay; local calendars unaffected.
- Auth failure → no silent fallback to empty success.

## Acceptance for unblocking

1. Templar delivers a versioned OpenAPI (or equivalent) covering the read surface above.
2. Raavi implements the connector against that contract behind feature/config gates.
3. Automated tests use a **fake** HTTP handler, not production Templar.
