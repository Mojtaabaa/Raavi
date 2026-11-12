# C0 — Target Architecture (Stage C)

**Date:** 2026-11-12  
**Companion:** [C0_ARCHITECTURE_ASSESSMENT.md](C0_ARCHITECTURE_ASSESSMENT.md), proposed ADR-057 (task status workflows).

---

## 1. North star

One **Task Query Spine** + one **Transition Service** feed five views:

```
                    ┌─────────────┐
                    │ Task Query  │  (space ACL, filters, paging)
                    │   Spine     │
                    └──────┬──────┘
           ┌───────┬───────┼───────┬────────┐
           ▼       ▼       ▼       ▼        ▼
         List   Kanban  Calendar Timeline Workload
           │       │       │       │        │
           └───────┴───┬───┴───────┴────────┘
                       ▼
              TaskTransitionService
              (only path that changes status)
```

Field edits (title, dates, assignees, estimates) stay on existing update APIs with ETag. **Status changes never** go through generic PUT of a free-form status string from the board.

---

## 2. Two workflow layers (unchanged principle)

| Layer | Name | Stage C role |
|-------|------|--------------|
| **A. Task status machine** | New (`TaskWorkflow*`) | Statuses, transitions, Kanban columns, transition permissions/guards/actions |
| **B. Automation rules** | Existing ADR-031 `Workflows` | Optional later: `task.*` triggers → Notify/AddLabel/… — **not** Kanban SoT |

Approvals (ADR-032) remain page-centric; task Approver role may gate transitions without creating Approval entities in C.

---

## 3. Status model (compatibility)

Each task binds to:

- `WorkflowDefinitionId` + **immutable** `WorkflowVersion` (the published snapshot in force when the task was created or last rebound)
- `StatusId` (FK into that version’s statuses) — **SoT for board column**
- Denormalized `Lifecycle` = `Open` \| `Done` derived from status category (`Active` vs `Terminal`) for:
  - 9a clients and CSV
  - `BlockCompleteWhenOpenChildren` (“open child” = child’s Lifecycle Open)
  - My Tasks default filters
  - Activity “completed/reopened” projections

**Rules:**

1. Transition updates StatusId + Lifecycle + Completed* when entering/leaving Terminal.
2. PUT task body does **not** accept arbitrary StatusId (or only via dedicated transition).
3. `POST /complete` and `/reopen` call the same Transition Service targeting configured terminal / reopen transitions of the bound workflow version.
4. Subtasks may use the **same space default workflow** (C1); optional per-task-type workflows deferred if complexity spikes.
5. Parent/child: independent StatusIds; complete-parent still blocked by open children / mandatory checklist when those policies are on **and** the target status is Terminal (or marked `RequiresCompleteGuards`).

---

## 4. Workflow definition lifecycle

```
Draft version ──validate──► Published version (immutable)
                                │
                     space default / explicit bind
                                │
                          active tasks pin Version N
                     new publish → Version N+1 (new tasks / optional migrate)
```

- Editing a published graph = **new draft** from snapshot; publish creates N+1.
- Disabling a workflow: no new binds; existing tasks keep Version N until rebound.
- Changing definition that still has active tasks: **no silent rewrite** of historical versions; admin may “migrate tasks to latest” as an explicit job (C2/C7).

**Scope of binding (C1 default):** one **default Task Workflow per Space** (like space publishing settings). Tenant-wide library of definitions reusable by spaces — allowed in schema; UI can start space-scoped.

---

## 5. Transition pipeline (single backend path)

```
Authorize space View → load task + pinned workflow version
→ resolve Transition(from→to) or named transition key
→ check Transition.Permission (Edit | role set | Completer)
→ evaluate Conditions (checklist, open children, custom flags — server)
→ run pre-actions (optional outbox intents)
→ apply StatusId / Lifecycle / Completed* / Revision++
→ outbox task.transitioned (+ completed/reopened aliases when Lifecycle flips)
→ SaveChanges
```

Kanban DnD, Timeline, and Complete button **only** call this API. Illegal edge → 409 with stable code (`task.transition_forbidden`, `task.open_children`, …).

---

## 6. View architectures

### 6.1 Kanban

- `GET board`: columns = statuses of pinned/default workflow version (ordered); cards = paged/virtualized tasks per column (or single query grouped server-side with caps).
- Card DTO: id, title, priority, due, progress, checklistProgress, childCounts, assignees[{userId,role,name}], revision, **allowedTargetStatusIds** (optional, for UI disable) — **server still re-validates** on drop.
- Drop → `POST /tasks/{id}/transitions` with If-Match.

### 6.2 Calendar overlay

- Do **not** create CalendarEvents by default.
- Implement **`ICalendarOverlaySource`** for native tasks (mirror `JiraCalendarOverlay`); `CalendarService` already merges overlays into `/calendar/occurrences`.
- Overlay shape: all-day from DueDate, or multi-day StartDate→DueDate; `SourceKind` e.g. `Task`; include `taskId` in URL/external id fields as today overlays do.
- Reschedule with permission → `PUT` task dates (ETag), not transition (unless product ties due-change to status — **not** default).

### 6.3 Timeline / Gantt-lite

- Hierarchical rows (parent/children); bars from Start/Due; ProgressPercent for bar fill.
- **Dependencies:** new `TaskDependencies` (DependsOn) — cycle-checked (reuse parent-walk patterns).
- Effort minutes ≠ bar duration (calendar span). Show estimate as badge, not bar length.
- Undated tasks in a side bucket / list under the chart.
- Scales: day / week / month (client zoom; server returns windowed query by date overlap).

### 6.4 Workload + allocation

- **Capacity:** working hours/days + optional holiday calendar (tenant or user prefs — lean MVP: tenant defaults).
- **Demand:** sum of **allocations** (not raw OriginalEstimate × assignee count).
- **Actual:** time entries by user (already exist) — separate series from allocation.
- Allocation rows: share of task Original (minutes or % normalized to minutes); Observer default 0%; sum ≤ Original (configurable soft warn vs hard block — recommend **hard block** on save).

---

## 7. Integration wiring

| Infra | Integration |
|-------|-------------|
| Outbox | `task.transitioned`; keep `task.completed`/`reopened` when Lifecycle flips for existing projectors |
| Activity | Project transition with from/to status names |
| Notifications | Optional watchers on transition if ContentId present (C7); no parallel engine |
| Search | SQL filters only in C; index deferred |
| AI | No auto-transition; Stage F |
| Hangfire | Optional: workflow migrate job; workload snapshot refresh (C6/C7) |
| Calendar | Overlay query only |
| ACL | All list/board/timeline/workload filter via View; manage workflow = space Administer (align with ADR-031) |

---

## 8. Module placement

Remain in **Raavi.Wiki** for Stage C (same as B). Extract `Raavi.Tasks` only if DependencyRules force it — separate ADR.

---

## 9. Non-goals (Stage C)

Configurable BPMN, parallel gateways, SLA clocks, Jira status sync, AI status suggest, full resource leveling, personal calendars, replacing ADR-031 designer with a canvas product.
