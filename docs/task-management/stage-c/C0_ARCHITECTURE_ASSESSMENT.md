# C0 — Architecture Assessment (Stage C)

**Date:** 2026-11-12  
**Scope:** Repository review only — no code, schema, or API changes.  
**Inputs:** B1–B6 reports, ADR-026/027/031/032/033/056, `IMPLEMENTATION_ROADMAP.md`, Wiki Task/Calendar/Workflow modules, FE tasks/calendar/workflows.

---

## 1. Verdict

Stage B left a **coherent native task aggregate** (`WorkTask` + assignees, checklists, collaboration, time) inside Wiki/`content`, with space ACL, RLS, outbox `task.*`, and Open|Done lifecycle. Stage C must **extend that spine** with a **task status machine** and **five view families**, without cloning ADR-031’s page automation engine or the Calendar event SoT.

---

## 2. Current state by area

### 2.1 Task domain (`WorkTask`)

| Area | State | Evidence |
|------|--------|----------|
| Placement | Wiki module, `content.Tasks`, `ITenantOwned` + RLS | ADR-026/056, `Domain/Tasks.cs` |
| Status | Enum `Open` \| `Done` only; `Complete`/`Reopen` mutate Status + Completed* | ADR-056 §10 allows additive StatusCode/WorkflowId in C |
| Assignees | `TaskAssignees` roles; one Responsible; dual-write `AssigneeId` | B2 |
| Hierarchy | `ParentTaskId`, depth ≤8, cycle guard, promote, delete-with-children | B3 |
| Checklists | Mandatory items can block complete | B3 |
| Collaboration | TaskComments / Attachments / Watchers; Activity.TaskId | B4 |
| Time | `TaskTimeEntries`; ActualSpent = active sum (+ opening balance) | B5 |
| Estimates / Progress | Task-level Original/Remaining; Progress manual; ChecklistProgress separate | B5/B6 |
| Dependencies | **Absent** (Stage A proposed `TaskRelations`; Stage E in roadmap) | Gap for Gantt |
| Allocation | **Absent** (estimates are task-level only) | Gap for Workload |

### 2.2 Task API and permissions

- Routes under `TaskEndpoints`: space list/export, mine, CRUD, complete/reopen, archive, assignees, children, checklists, comments, attachments, watchers, activity, time.
- Existence = space **View** (404). Mutations = space **Edit** (with role exceptions: complete, log time for Responsible/Contributor).
- Optimistic concurrency: ETag `"r{n}"` on task / comment / time updates.
- **No** `POST …/transition`, board, timeline, or workload endpoints yet.

### 2.3 Complete logic vs future workflow

Today “Done” is both **status** and **terminal lifecycle**. Stage C must keep:

- `BlockCompleteWhenOpenChildren`
- `BlockCompleteWhenMandatoryIncomplete`
- Assignee complete-without-Edit

…as **guards on transitions into a terminal (Done-category) status**, not as a parallel UI checkbox path that bypasses the transition service.

### 2.4 Outbox, activity, notifications

- Mutations publish `WikiEvent` / outbox `task.*` (and collab/time/estimate kinds).
- `ActivityProjection` stores task-scoped activity.
- In-app notifications still require linked `ContentId` for many kinds (ADR-056) — assignment/mention without page remain limited. Stage C transitions should emit outbox events; notification expansion is additive and must not invent a second notify engine.

### 2.5 Existing “Workflow” (ADR-031) — **not** a task status machine

| Aspect | Page WorkflowEngine | Needed for Stage C |
|--------|---------------------|--------------------|
| Purpose | When page event → conditions → actions | Statuses + allowed edges for Kanban |
| Storage | `content.Workflows` JSON rules | Dedicated task workflow definition/version/status/transition tables |
| Designer | Form + read-only `WorkflowFlow.vue` | Form + light status graph (same spirit) |
| Execution | Outbox handler, `WorkflowRun` | Synchronous authorize-and-apply transition in Task service |

**Decision confirmed:** do **not** overload ADR-031 tables for Kanban columns. Optionally later add `task.*` **triggers** to the page automation engine (automation layer), after the status machine exists.

### 2.6 Approvals (ADR-032)

Page approval requests — pattern for multi-person confirm, not task status. Stage C may reference Approver **role** on tasks; wiring Approvals objects to task transitions is **out of C** unless PO expands scope (recommend Stage E).

### 2.7 Calendar (ADR-027)

- Space calendars, wall-clock events, Persian/Gregorian recurrence, `/calendar/occurrences` with `sourceKind` filter.
- Tasks already have `StartDate` / `DueDate` (`date`, no TZ) — suitable for **overlay occurrences**, not mandatory dual-write into `CalendarEvents`.
- **Existing hook:** `ICalendarOverlaySource` / `CalendarOverlayOccurrence` (`Raavi.Application/Abstractions/Calendar/`) is already merged in `CalendarService.OccurrencesAsync`. Live implementers: `JiraCalendarOverlay`; Templar stub. **C4 should register a native Task overlay** the same way — not invent a parallel calendar engine or default `CalendarEvent` clones.
- FE: `CalendarView.vue` + `calendarMath.ts` (Jalali via Intl); already discriminates `sourceKind`.

### 2.8 UI architecture

- Tasks: single `TasksView.vue` (+ collaboration/time panels) for My Tasks and space list/drawer — no board/timeline routes yet.
- Workflows: form editor + `WorkflowFlow` (read-only HTML flow) — **reuse pattern**, not a second canvas product.
- ADR-033 explicitly deferred drag-and-drop workflow canvas.

### 2.9 Search / AI / jobs

- Tasks are **not** in RAG chunks / OpenSearch (7c deferred). Stage C filters stay SQL + View ACL.
- Hangfire: stale sweep, calendar reminders, maintenance — no task board jobs yet. Workload aggregation should prefer query-time or cached daily snapshots via existing Workers patterns if needed (C6/C7).

### 2.10 Tenant isolation

All B1–B5 task tables use `AddTenantIsolation` + space-scoped ACL. Stage C tables must follow the same (`ITenantOwned`, composite FKs, RLS, 404 semantics).

---

## 3. Gaps vs Stage C goals

| Goal | Gap |
|------|-----|
| Configurable workflow | No workflow/status/transition entities; Open/Done enum only |
| Workflow designer | N/A |
| Kanban | No board API; no server transition |
| Calendar overlay | No task→occurrence projection; calendar does not load tasks |
| Timeline / Gantt | No dependency model; list APIs not hierarchical windowed |
| Workload / allocation | No capacity model; no per-assignee estimate shares |
| Unified query spine | Space list + mine still Take(500); pagination needed before large boards |

---

## 4. Reuse map (mandatory)

| Asset | How Stage C uses it |
|-------|---------------------|
| `WorkTask` + Task* services | Extend; single transition entrypoint |
| Space `WikiAuthorizer` | Board/calendar/timeline/workload row filters |
| Outbox + Activity | `task.transitioned` (+ field change events) |
| Notifications | Existing kinds; extend carefully with ContentId rule |
| `ICalendarOverlaySource` | Native Task overlay registered like `JiraCalendarOverlay` |
| `WorkflowFlow` UX pattern | Status graph preview (read-only) |
| ETag / Result / Problem details | Transition conflicts, design publish validation |
| i18n / CalendarDateInput | All new surfaces |

## 5. Anti-patterns (forbid in Stage C)

- Parallel `TaskStatusEngine` microservice or second outbox.
- Client-only Kanban status writes / forging transitions.
- Cloning every task into `CalendarEvents` as default.
- Full BPMN / drag-drop workflow canvas (ADR-033).
- Treating ADR-031 page rules as Kanban columns.
- Double-counting estimate across all assignees without allocation rows.
- Unfiltered search/AI facets of tasks.

---

## 6. Compatibility baseline for migration

- Existing Open → default workflow status `open` (non-terminal).
- Existing Done → `done` (terminal); keep `CompletedAt`/`CompletedBy`.
- Retain enum column or denormalized `Lifecycle` during dual-read (see data model proposal).
- Complete/reopen HTTP routes remain as **aliases** to transitions into terminal / reopen-to-initial for 9a clients.
