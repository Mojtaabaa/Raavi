# API Design — Task Management (Stage A)

## 1. Compatibility

Keep existing 9a routes working during B:

- Same paths and Open/Done until status workflow ships.
- Additive DTO fields (`priority`, `assignees[]`, …).
- Deprecate single `assigneeId` only after dual-write period (document in ADR).

## 2. Proposed native APIs (incremental)

### Query (shared spine)

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/tasks?…` | Cross-space query (My/Team) with filters, cursor pagination |
| GET | `/spaces/{key}/tasks?…` | Space-scoped (extend filters) |
| GET | `/tasks/{id}` | Detail + rights + etag |

Filters (progressive): status, priority, type, assignee/role, creator, due range, overdue, blocked, label, parent, contentId, externalSource, q.

### Commands

| Method | Path | Notes |
|--------|------|-------|
| POST | `/spaces/{key}/tasks` | Extended body |
| PUT | `/tasks/{id}` | If-Match |
| POST | `/tasks/{id}/transition` | `{toStatus, comment?}` authorized |
| POST | `/tasks/{id}/assignees` | add/remove roles |
| POST | `/tasks/{id}/complete` | Keep; map to terminal transition |
| POST | `/tasks/{id}/archive` | Prefer over hard delete |
| DELETE | `/tasks/{id}` | Soft-delete or admin-only hard |

Sub-resources: `/checklist`, `/time-entries`, `/relations`, `/watchers`, `/comments`, `/attachments`, `/external-links`.

### Views helpers

| Method | Path | Purpose |
|--------|------|---------|
| GET | `/tasks/board?space=&workflow=` | Columns + cards (server authz) |
| GET | `/tasks/timeline?…` | Range + hierarchy |
| GET | `/tasks/workload?…` | Person capacity slice |
| GET | `/calendar/task-overlays?from=&to=` | Overlay for Calendar UI |

## 3. Existing routes to retain

Documented in `docs/api/WIKI_API.md` §Tasks — remain until replaced by versioned supersets.

## 4. Jira

Do not invent Jira REST shapes. Reuse Integrations:

- Page flows already: create/link/refresh/unlink.
- Task flows: wrap same client behind `/tasks/{id}/external-links` once native task exists.

## 5. AI (confirm-to-apply)

| Method | Path | Notes |
|--------|------|-------|
| POST | `/ai/tasks/draft` | Propose fields; no persist |
| POST | `/ai/tasks/from-page` | `{pageId, selection?, confirmed}` |
| POST | `/ai/tasks/assist` | Q&A over authorized task set |

Mirror Editor AI: `confirmed` required for side effects.

## 6. Errors

Stable codes: extend `task.*` (`task.transition_denied`, `task.assignee_duplicate`, `task.cycle`, `task.external_sync_failed`, …). Foreign/unauthorized → `task.not_found` (404).
