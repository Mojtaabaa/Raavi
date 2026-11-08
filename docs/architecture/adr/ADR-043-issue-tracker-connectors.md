# ADR-043 — Read-only issue tracker connectors: GitHub, GitLab, Jira (Phase 13a)

**Status:** Accepted (2026-11-08)

## Context
The roadmap lists Jira/GitHub/GitLab links. No live instance of any of them was available, and the product already has an SSRF-guarded outbound HTTP pattern (webhooks, ADR-036) and tenant-bound secret protection (ADR-034/036).

## Decisions
1. **Scope: read-only reference lookup.** A connector resolves a reference (`owner/repo#42` for GitHub, `group/sub/project#5` for GitLab, `KEY-9` for Jira) to *title, status, type and link*. Nothing is written to the tracker, nothing is synchronised, no webhooks from the trackers are received. Only those fields are read from the answer; bodies/comments/assignees are ignored and never returned.
2. **Per tenant, administrator-managed** (`integrations.IssueConnectors`, RLS, max 10 per tenant, unique name): kind, base URL, optional token. The token is write-only: protected with Data Protection under a tenant-bound purpose (`Raavi.Connectors.Token.v1.<tenant>`), never returned (the API exposes only `hasToken`), kept when an update omits it, removed with an empty string. A token requires https.
3. **SSRF:** a separate installation-level allow-list `Raavi:Integrations:AllowedConnectorHosts` (empty = connectors unusable), the same loopback/link-local/metadata refusal and connect-time DNS re-check as webhooks, no redirects, query strings in the base URL refused, response capped at 256 KB, timeout `TimeoutSeconds`.
4. **References are validated by strict patterns before any URL is built** (no `..`, no query/fragment/slash injection, bounded numbers). GitLab project paths are URL-encoded; the request goes only to the path the connector kind documents.
5. **Who may look up:** every signed-in member of the workspace (`GET /integrations/issues/lookup`) — the token is the workspace's read access, a choice of its administrators, and the connector list shown to members has names only (no URLs). Lookups are cached 60 s per (tenant, connector revision, reference) and limited to 300 per user per hour (in memory, per node).
6. **Errors are stable codes** (`issue.reference_invalid`, `issue.not_found`, `issue.upstream_denied`, `issue.upstream_unreachable`, `issue.upstream_invalid`, `issue.rate_limited`, `connector.*`); upstream text never reaches the client or logs. Returned links must be http(s). Titles are stripped of control characters and truncated.

## Verification and limits
Verified against **in-process fakes** that reproduce the documented request/response shapes (GitHub REST v3 `/repos/{o}/{r}/issues/{n}`, GitLab API v4 `/projects/{path}/issues/{iid}`, Jira DC/Server REST v2 `/issue/{key}?fields=summary,status`) — **not** against a real GitHub, GitLab or Jira. Jira Cloud (different auth and a `/rest/api/3` endpoint), OAuth apps/GitHub App installation tokens and GitLab merge requests are not supported. The rate limit and cache are per node. The editor does not yet turn references into smart links; today the admin page's "check a reference" panel and the API are the only users. Teams, Outlook and Google remain undone.
