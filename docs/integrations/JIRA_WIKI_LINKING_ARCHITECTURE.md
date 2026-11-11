# Jira ↔ Wiki Linking — Architecture

**Date:** 2026-11-11  
**Status:** Accepted for implementation

## Authority

| Concern | Owner |
|---|---|
| Page–Issue relationship + source contexts | **Raavi** (`integrations` schema) |
| Issue fields / workflow | **Jira** |
| Reporting / Templar consumption | **Templar** via versioned events (no UI dependency) |

## Domain

### `PageIssueLink` (tenant-owned)

- Ids: `Id`, `TenantId`, `ContentId` (Raavi page), `ConnectorId` (Jira instance), `IssueId` (Jira stable id string), `IssueKey` (mutable display)
- Snapshots (cache): summary, status, type, priority, assignee, project key, issue URL, Jira updated
- Source: `CreatedFromRaavi` | `LinkedExistingIssue` | `Imported` | `Other`
- Status: `Active` | `Unlinked` (soft)
- Sync: `SyncState` (`Synced` | `PendingRemoteLink` | `RemoteLinkFailed` | `IssueUnavailable`), `RemoteLinkGlobalId`, `RemoteLinkId`, `LastSyncedAt`, `LastSyncError`
- Audit: `CreatedBy`, `CreatedAt`, `UpdatedAt`, `UnlinkedBy`, `UnlinkedAt`

**Unique (active):** `(TenantId, ContentId, ConnectorId, IssueId)` where Status = Active (filtered unique index).

### `PageIssueLinkContext`

Multiple selections for the same page+issue without duplicating the relationship:

- `LinkId`, `Exact` / `Prefix` / `Suffix` (TextQuote), optional `AnchorJson` (future ProseMirror meta)
- `CreatedBy`, `CreatedAt`

### Remote Link identity

`globalId = raavi:page:{tenantId:N}:{contentId:N}` — deterministic per page; one Remote Link object per Raavi page on a given issue. Idempotent GET-by-globalId then POST/PUT.

Canonical object URL: `{tenantCanonicalBase}/spaces/{spaceKey}/pages/{contentId:D}` (id-stable; title changes update Remote Link **title** only).

## Module placement

```
Raavi.Integrations
  Domain/Persistence: PageIssueLink*, migration + RLS
  JiraIssueClient: createmeta / create / search / remotelink (Jira Kind only)
  PageIssueLinkService: authz + orchestration
  PageIssueLinkSyncJob: Hangfire retry PendingRemoteLink
  RelationshipEventPublisher → content.Outbox types jira.page_issue_*
  IOutboxHandler: content.updated|moved|trashed → refresh/remove Remote Links; Templar no-op when unconfigured
```

Wiki does not reference Integrations. Frontend calls Integrations HTTP APIs.

## Authz

| Action | Raavi | Jira |
|---|---|---|
| List links on page | Content **View** | Cached snapshots; refresh uses connector token |
| Create / link / unlink | Content **Edit** | Connector token must allow create/link |
| List pages for issue | Each page **View** (omit others as absent) | — |
| Connector admin | Tenant admin (existing) | — |

Never take TenantId from the client. Foreign ids → 404.

## Description template (create)

User description + optional appendix (once):

```
----
Created from Raavi
Page: {title}
Page URL: {canonical}
Page ID: {contentId}
Selected text:
{exact}
```

## Templar events (schema v1)

Outbox types (payload JSON, no secrets/bodies beyond titles):

- `jira.page_issue_linked`
- `jira.page_issue_unlinked`
- `jira.page_issue_metadata_updated`
- `jira.page_metadata_updated` (title/URL change affecting links)
- `jira.relationship_sync_failed`

Fields: `eventId`, `schemaVersion`, `tenantId`, `connectorId`, `issueId`, `issueKey`, `contentId`, `pageTitle`, `pageUrl`, `source`, `actorId`, `occurredAt`.

Delivery: existing outbox handlers / webhooks; **no** invented Templar production URL. Contract: `TEMPLAR_PAGE_ISSUE_EVENTS.md`.

## Failure modes

1. Create issue succeeds, Remote Link fails → link saved `PendingRemoteLink`, job retries; **no** second create.
2. Jira 404 on refresh → `IssueUnavailable`; relationship kept.
3. Templar down → events remain in outbox; Raavi UI unaffected.
