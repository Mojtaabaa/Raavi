# Integration Architecture

## Principles

1. External systems remain authoritative for their entities.  
2. Raavi stores links, mirrors, and knowledge — not a shadow SoT.  
3. Failures must not destroy Raavi content.  
4. Fakes-first tests; real hosts are Blocked until verified.  
5. No invented production APIs without contracts (Templar calendar).

## Current connectors

| Connector | Status |
|-----------|--------|
| GitHub / GitLab / Jira lookup | Complete vs fakes (ADR-043) |
| Jira page create/link / Remote Link | Incomplete (DC v2); Cloud v3 not targeted |
| Outbound webhooks | Complete (ADR-036) |
| Templar page-issue events + calendar shell | Stub / Blocked |
| Identity OIDC / LDAP / Jira login | Complete vs fakes |

## Target connector SDK (logical)

Shared interface in Integrations:

- Configure, test connection, health  
- Auth (PAT/OAuth) in secrets  
- Sync direction / schedule / webhook  
- Retry, DLQ via jobs  
- Audit + conflict policy  
- Permission mapping documentation  

Implement new connectors **behind this interface** without a marketplace in early phases (ADR-015 plugins remain controlled).

## Priority order (EKOS)

1. Harden Jira DC link + real-instance verification  
2. Templar when contract enabled  
3. Calendar Google/Outlook/CalDAV (Calendar assessment)  
4. GitHub/GitLab write-back / PR docs (later)  
5. OpenAPI ingest → Catalog entries  
6. Teams — only with clear knowledge value  

## Ownership matrix

See DOMAIN_MODEL.md — Jira/Templar/external calendars stay external SoT.