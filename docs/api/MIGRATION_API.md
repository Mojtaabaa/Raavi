# Migration API (Phase 8)

Tenant host only; every endpoint requires a **tenant administrator** (403 otherwise). Jobs are private to their workspace. Base path: `/api/v1/migrations/confluence`. Design: ADR-025.

| Method | Path | Notes |
|---|---|---|
| GET | `/` | the workspace's jobs, newest first (max 50) |
| POST | `/` | `{url, token, dryRun?, spaces?, keyMap?}` → 201 job. `dryRun` defaults to **true**. The address must be allow-listed (`Raavi:Migration:AllowedSourceHosts`) and the token is proven by reading the source version; nothing is read or written yet. `keyMap` maps Confluence space keys to Raavi keys. Errors: `source.host_not_allowed`, `source.host_forbidden`, `source.url_invalid`, `source.unauthorized`, `source.not_confluence`, `source.version_unsupported`, `migration.token_required` |
| GET | `/{id}` | job: status (`Created, Queued, Running, Paused, Cancelled, Completed, CompletedWithErrors, Failed`), `control` (`Pause`/`Cancel` requested), counters (`spacesTotal/Done`, `pagesTotal/Done/Failed`, `versionsDone`, `warnings`), `currentOperation`, `hasReport`, `canResume` |
| POST | `/{id}/start` | `{token?}` → queues the job on the Workers host. A token is required again only when the job's stored token was erased (finished/cancelled/failed jobs). Re-starting a paused or failed job resumes it; imported pages/versions are skipped. `409 migration.already_running` while active |
| POST | `/{id}/pause` · `/{id}/cancel` | honoured between pages; `409 migration.not_running` otherwise |
| GET | `/{id}/events?level=&take=` | progress/warning/error records (≤ 500) |
| GET | `/{id}/report` | JSON report: per-space counts (pages, versions, imported, failed, key conflicts), unsupported macros, warnings by code, attachment and page-link references, unmapped authors, link-pass result, validation issues. Counts only — never page content or secrets |

**Create options** (all optional): `reconvert` (refresh already-imported pages, authors and files), `mapUsersByEmail` (apply matches by verified e-mail; off = suggestions only), `userMap` (`{"sourceUser": "member@example.com"}`). The report adds `attachments` (found, bytes, imported, replaced, tooLarge, failed, sizeMismatch, maxBytes), `access` (restrictedPages, restrictionsApplied, unmappedSubjects, spacePermissions = `not_imported`) and `authorMappings` (source user, e-mail, suggested member, applied), and `comments` (found, imported, skipped, failed).

The source token is never returned by any endpoint and is erased from the job when it ends (a paused job keeps it).
