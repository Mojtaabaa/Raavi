# Jira ↔ Wiki Page Linking — Assessment

**Date:** 2026-11-11  
**Scope:** Bidirectional Page–Issue relationships (create from selection, link existing, panel, Remote Links, Templar events)

## Executive summary

Raavi today has **read-only** issue connectors (ADR-043): lookup by reference, encrypted PAT/Bearer, SSRF allow-list, inline `raavi.issue` editor chip. There is **no** Page–Issue relationship store, **no** Jira create/search/Remote Link writes, **no** selection-bar Jira actions, and **no** `docs/integrations/` prior to this work. Identity Jira OAuth (ADR-007) is for sign-in only and must not be reused as the connector token.

**Verdict:** Implement by **extending** `Raavi.Integrations` (Jira-only write surface on existing connectors) + page-scoped APIs gated by `IPermissionEvaluator`, reusing selection UX from comments (`TextQuote`), canonical page URLs via `ITenantUrlResolver`, and outbox/Hangfire for Remote Link retries. Do not invent production Templar endpoints; publish versioned relationship events for Templar to consume.

## Present (reuse)

| Area | Evidence |
|---|---|
| Connectors CRUD + lookup | `IssueConnector`, `IssueConnectorService`, `IssueLookupService`; routes under `/api/v1/integrations/*` |
| HTTP client + SSRF | `ConnectorHttp.ClientName`, `ConnectorHostPolicy`, `Raavi:Integrations:AllowedConnectorHosts` |
| Fake tracker | `FakeIssueTracker` (tests) |
| Editor issue chip | schema `raavi.issue`, `IssueView.vue`, slash command |
| Selection → comment | `PageView.vue` `.selection-bar`, `commentAnchors.ts` `TextQuote` (exact/prefix/suffix) |
| Page authz | `WikiAuthorizer` / `IPermissionEvaluator`; invisible → 404 |
| Page text + URL | `IPageTextReader`, `ITenantUrlResolver.GetCanonicalUrlAsync` + `/spaces/{key}/pages/{id}` |
| Outbox / webhooks | `content.Outbox`, `IOutboxHandler`, `WebhookDispatcher` |
| Hangfire tenant jobs | `ITenantJobScheduler`, `[AutomaticRetry]` |

## Missing (must build)

1. Relationship tables (`PageIssueLinks` + source contexts) with RLS
2. Jira write ops: projects, createmeta, create issue, search/JQL, Remote Link CRUD
3. Page–Issue APIs (list/create/link/unlink/refresh) with Edit/View checks
4. Selection-bar actions + create/link modals + linked-issues panel
5. Anchor navigation (TextQuote + optional ProseMirror meta); degrade gracefully
6. Sync state + Hangfire retry when Remote Link fails after issue create
7. Templar event contract + outbox publication (no fake Templar host)
8. Docs/ADR amendment: ADR-043 remains lookup baseline; write/link is additive for **Jira** connectors with token

## Explicit non-assumptions

- Not Jira Cloud `/rest/api/3` unless later approved (DC/Server `rest/api/2` as today)
- Connector token is workspace PAT/Bearer — not per-user OAuth for V1 write
- GitHub/GitLab stay read-only lookup; linking UI is Jira-only
- No BubbleMenu today — extend sticky selection bar, not TipTap Pro BubbleMenu

## Risks

| Risk | Mitigation |
|---|---|
| ADR-043 “read-only” vs create/link | Document additive Jira write; require token + Edit; keep lookup unchanged |
| Partial success (issue ok, Remote Link fail) | Persist link with `SyncPending` + retry job; never create duplicate issues |
| Permission mismatch (Jira vs Raavi) | Remote Link URL still requires Raavi auth; list pages for issue filters by View |
| Module boundaries | Integrations owns persistence + Jira HTTP; uses Application abstractions only |
