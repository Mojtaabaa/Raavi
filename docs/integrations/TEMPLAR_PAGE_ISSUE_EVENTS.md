# Templar contract — Page↔Issue relationship events

**Status:** Contract published; no production Templar sink in Raavi  
**Schema version:** 1  
**Transport:** Raavi outbox (`integrations.Outbox` and/or tenant webhooks subscribed to `jira.*` events)

## Principles

- Raavi UI and issue creation do **not** depend on Templar availability.
- Templar consumes events **idempotently** (key = outbox/`event` id).
- Do not invent Templar production URLs in Raavi until an agreed base URL and auth exist.

## Event types

| Type | When |
|---|---|
| `jira.page_issue_linked` | Relationship created (create or link) |
| `jira.page_issue_unlinked` | Soft unlink |
| `jira.page_issue_metadata_updated` | Cached issue fields refreshed |
| `jira.page_metadata_updated` | Page title/URL change affecting Remote Links |
| `jira.relationship_sync_failed` | Remote Link sync failed (recoverable) |

## Payload (schemaVersion = 1)

Identifiers only for webhook delivery (see `WebhookPayload`). Full relationship events in `integrations.Outbox` may also include:

- `tenantId`, `connectorId`, `issueId`, `issueKey`, `contentId`, `spaceId?`
- `pageTitle?`, `source`, `actorId?`, `syncState?`, `occurredAt` (envelope)

No secrets, no Jira tokens, no unrestricted page bodies.
