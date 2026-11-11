# Jira ↔ Wiki Linking — Implementation Plan

**Date:** 2026-11-11  
**Assessment:** [JIRA_WIKI_LINKING_ASSESSMENT.md](./JIRA_WIKI_LINKING_ASSESSMENT.md)  
**Architecture:** [JIRA_WIKI_LINKING_ARCHITECTURE.md](./JIRA_WIKI_LINKING_ARCHITECTURE.md)

## Stages

| Stage | Work | Exit |
|---|---|---|
| 1 | Assessment + architecture + this plan | Docs in `docs/integrations/` |
| 2 | `PageIssueLink` + `PageIssueLinkContext` + RLS migration | `TenantIsolationModelTests` + EF migrate |
| 3 | `JiraIssueClient` (projects, createmeta, create, search, remotelink) + FakeIssueTracker shapes | Unit/API tests with fake |
| 4 | `PageIssueLinkService` + endpoints + authz | Backend tests: create/link/unlink/dupes/isolation |
| 5 | Selection bar + create modal (metadata-driven) | Frontend + locales |
| 6 | Link-existing search modal | Same |
| 7 | Linked Issues panel on `PageView` | Same |
| 8 | Source-context navigate (TextQuote locate) | Component test |
| 9 | Outbox events + sync job + Templar contract doc | Handler/job tests; Templar optional |
| 10 | Regression + IMPLEMENTATION_STATUS + API docs | Calendar/wiki/connectors green |

## API sketch (tenant, authenticated)

| Method | Route | Notes |
|---|---|---|
| GET | `/integrations/jira/connectors` | Enabled Jira connectors for member |
| GET | `/integrations/jira/connectors/{id}/projects` | |
| GET | `/integrations/jira/connectors/{id}/createmeta?project=&issuetype=` | |
| GET | `/integrations/jira/connectors/{id}/search?q=` | key or text / JQL subset |
| GET | `/pages/{contentId}/jira-links` | View |
| POST | `/pages/{contentId}/jira-links/create` | Edit; body fields + quote |
| POST | `/pages/{contentId}/jira-links` | Edit; link existing |
| POST | `/pages/{contentId}/jira-links/{linkId}/refresh` | View |
| DELETE | `/pages/{contentId}/jira-links/{linkId}` | Edit; soft unlink |
| GET | `/integrations/jira/issues/{connectorId}/{issueId}/pages` | View-filtered |

## Config / permissions (ops)

- Connector: Kind=Jira, https BaseUrl, token with Browse + Create Issues + Link Issues (Remote Links)
- `Raavi:Integrations:AllowedConnectorHosts` must include Jira host
- Optional: `Raavi:Integrations:Jira:AppendSourceTemplate` (default true)

## Non-goals this delivery

- Jira Cloud API v3 / ADF description as sole format (use API v2 wiki/plain)
- Per-user Jira OAuth for writes
- GitHub/GitLab bidirectional linking
- Live Templar HTTP sink (contract + outbox only)
