# Current State Assessment — Tasks (Stage A)

**Date:** 2026-11-12  
**Scope:** Repository inspection only (no code or schema changes)  
**Primary sources:** `backend/Raavi.Wiki/Domain/Tasks.cs`, `Application/TaskService.cs`, `Api/TaskEndpoints.cs`, migration `20261107202438_Tasks`, ADR-026, `frontend/src/modules/tasks/`, `Raavi.Tests/Wiki/TaskTests.cs`

## 1. Where Tasks live today

| Layer | Reality |
|-------|---------|
| Module | **Inside `Raavi.Wiki`**, not a separate feature project |
| Table | `content.Tasks` (`WorkTask`), `ITenantOwned`, RLS via `AddTenantIsolation` |
| Domain class | `Raavi.Wiki.Domain.WorkTask` |
| Service | `TaskService` (scoped in `WikiModule`) |
| API | Mapped by `TaskEndpoints` under tenant host + `AuthenticatedUser` |
| UI | Single view `TasksView.vue` for `/tasks` (mine) and `/spaces/:key/tasks` |
| Docs | ADR-026; `docs/api/WIKI_API.md` §Tasks; user guide fa §وظایف |

`MODULE_BOUNDARIES.md` lists a conceptual `Tasks` namespace under Domain; **runtime ownership is Wiki**. Editor checklist nodes (`core.taskList` / `core.taskItem`) are **document schema only** and are not linked to `content.Tasks` (ADR-026 explicit).

## 2. Data model (as implemented)

`WorkTask` fields: `Id`, `TenantId`, `SpaceId`, `ContentId?` (optional page of same space, **no FK**), `Title` (≤300), `Notes` (≤4000 plain text), `Status` (`Open`|`Done`), `AssigneeId?` (single user), `DueDate?` (`date`), `CreatedBy`, `CreatedAt`, `UpdatedAt`, `CompletedAt?`, `CompletedBy?`, `Revision`.

Indexes: `(TenantId, AssigneeId, Status)`, `(TenantId, SpaceId, Status, DueDate)`.  
FK: composite to `spaces.Spaces`. Hard delete on `DELETE` (no archive soft-delete).

**Absent from schema:** priority, type, start date, estimate/remaining, progress, parent task, multi-assignee, checklist table, watchers, labels, workflow status set, external source keys, time entries, dependencies.

## 3. Behaviour and permissions

- Space **View** → list/get/export; invisible space → **404** (`task.not_found`).
- Space **Edit** (active space) → create/update/delete.
- **Assignee** (with View) may complete/reopen own task without Edit.
- Assignee must be active tenant member **and** able to View the space (`task.assignee_invalid` / `task.assignee_no_access`).
- `ContentId` must be non-trashed page in same space (`task.content_invalid`).
- Optimistic concurrency: PUT requires `If-Match: "r{n}"`.
- Cap: 500 rows per list/mine.
- Overdue: Open + DueDate &lt; UTC calendar date.
- No outbox events for task create/update/complete/delete (no ActivityProjection kinds found for tasks).
- No assignment notifications.
- CSV export formula-safe (UTF-8 BOM).

## 4. API surface

| Method | Path | Notes |
|--------|------|-------|
| GET | `/spaces/{key}/tasks?status=&assignee=` | Space list |
| POST | `/spaces/{key}/tasks` | Create |
| GET | `/spaces/{key}/tasks/export` | CSV |
| GET | `/tasks/mine?status=` | Assignee = caller |
| GET/PUT | `/tasks/{id}` | PUT If-Match |
| POST | `/tasks/{id}/complete` \| `/reopen` | |
| DELETE | `/tasks/{id}` | Hard delete |

## 5. Frontend

- List + Open/Done/All filter; create/edit dialog (title, notes, assignee picker, due date, optional page).
- Complete via checkbox; overdue tag; link to space and optional page.
- Jalali/Gregorian via existing locale/calendar stores.
- **No** Kanban, Timeline, Workload, task detail tabs, bulk actions, saved views.
- Knowledge maintenance admin can **create** a space task from a suggestion (`KnowledgeMaintenanceView` → `tasksApi.create`).

## 6. Tests

- Backend: `TaskTests` (CRUD, assignee rules, mine cross-space, isolation, CSV, ETag).
- Frontend: `tasks.test.ts`.
- Export ZIP includes `tasks.json` (`ExportTests`).
- Tenant isolation model tests cover `content.Tasks`.

## 7. Classification snapshot

| Capability | Status |
|------------|--------|
| Space-scoped CRUD + mine list | **Production-ready** (9a) |
| Single assignee + due date + page link | **Production-ready** |
| Space ACL + RLS + ETag | **Production-ready** |
| CSV export | **Production-ready** |
| Inline editor task lists | **Separate feature** (doc nodes; not WorkTask) |
| Multi-assignee / roles / workflow / views / AI / Jira-as-task | **Missing** (see Gap Analysis) |
| Notifications / outbox / search index | **Missing** for tasks |
| Soft archive | **Missing** (hard delete) |

## 8. Architectural notes for Stage B+

1. Extending `WorkTask` in-place vs extracting `Raavi.Tasks` module: boundaries say feature modules must not reference each other; today Tasks are Wiki-owned — **prefer evolve Wiki + Application abstractions** unless PO mandates a new project.
2. Do not conflate TipTap `taskList` with enterprise tasks.
3. Jira issues already exist as **page links** (`PageIssueLink`), not as `WorkTask` rows — ownership model already separates Raavi vs Jira for issues.
