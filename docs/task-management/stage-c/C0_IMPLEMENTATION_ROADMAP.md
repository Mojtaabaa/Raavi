# C0 — Implementation Roadmap (Stage C)

**Date:** 2026-11-12  
**Complexity key:** S · M · L · XL  

**Recommended order (by dependency):**  
`C0 → C1 → C2 → C3` in series for the status spine; **C4** may start after C1 (needs Status/Lifecycle + date indexes, not full designer); **C5** after C1 (+ deps schema; prefers C3 patterns); **C6** after C1 (allocation independent of Kanban UI); **C7** last.  
Do **not** start C3 before a working Transition Service (C1).

---

## C0 — Architecture assessment and design ✅ (this folder)

| | |
|--|--|
| **Scope** | Docs only under `docs/task-management/stage-c/` |
| **Deps** | Stage B complete |
| **DB / API / FE / Code** | None |
| **Complexity** | M |
| **Acceptance** | Seven C0 docs + PO approval gate for C1 |

---

## C1 — Configurable workflow foundation · **L**

| | |
|--|--|
| **Scope** | Default space workflow; Tasks pin definition+version+StatusId; Lifecycle dual-write; Transition Service; complete/reopen aliases; backfill Open/Done; list filters by statusKey/lifecycle |
| **Deps** | C0 approved |
| **DB** | TaskWorkflow* tables + RLS; Tasks columns + indexes; seed per space; **non-destructive** |
| **Backend** | Domain + `TaskWorkflowService` + `TaskTransitionService`; wire SetDone → transition |
| **API** | GET workflow default/snapshot; GET/POST transitions; Task DTO extensions; keep PUT without free status |
| **FE** | Show status name/color on list/drawer; complete still works; minimal status badge |
| **Security** | Transition authz; Administer for future publish (C2); tenant RLS on new tables |
| **Tests** | Backfill; open→done→open; guards (children/checklist); cross-tenant 404; ETag on transition |
| **Risks** | Dual-read bugs; incomplete seed on old spaces — mitigate lazy ensure-default |
| **Acceptance** | All existing TaskTests green; new tasks get default workflow; complete uses transition path; no Kanban required yet |

---

## C2 — Workflow designer and transition rules · **L**

| | |
|--|--|
| **Scope** | Draft edit, validate, publish versions, enable/disable, space default pointer; conditions/actions JSON allow-list; historical version view; block unsafe in-place edit of published |
| **Deps** | C1 |
| **DB** | Draft storage; maybe TaskWorkflowDefinition.Revision only — versions immutable |
| **Backend** | Publish validator; migrate-tasks-to-latest **optional explicit** API (or defer hard migrate to C7) |
| **API** | Full admin routes from API design |
| **FE** | `TaskWorkflowSettings` + status graph preview (WorkflowFlow-like); no canvas product |
| **Security** | Space Administer only for mutate/publish |
| **Tests** | Publish validation failures; task remains on v1 after v2 publish; illegal transition still 409 |
| **Risks** | Over-building designer — **cap** to form+matrix+preview |
| **Acceptance** | Admin can publish custom statuses; tasks on v1 unaffected; transitions honor new edges for **new** binds / rebound tasks |

---

## C3 — Kanban view · **L**

| | |
|--|--|
| **Scope** | Board API + FE DnD; filters; per-column paging; allowed-target hints; snap-back on failure |
| **Deps** | C1 (hard); C2 (soft — default workflow enough for MVP board) |
| **DB** | Indexes from C1; no new tables required |
| **Backend** | Board query (group/cap/cursor); reuse Transition Service only |
| **API** | `GET …/tasks/board` |
| **FE** | `TaskBoardView`; view switcher on space tasks |
| **Security** | Cards only if View; drop cannot bypass transition |
| **Tests** | Illegal column drop; concurrent If-Match; filter ACL |
| **Risks** | N+1 assignees — batch load; huge columns — hard caps |
| **Acceptance** | DnD success/fail matches server; board matches workflow columns |

*Note: Shipping C3 after C1 with **only** default Open/Done columns is acceptable if PO wants early UX; custom columns need C2 publish.*

---

## C4 — Calendar overlay · **M**

| | |
|--|--|
| **Scope** | Task occurrence projection; FE toggle; optional date drag; Jalali/Gregorian unchanged |
| **Deps** | C1 (lifecycle/status filters); Calendar module stable |
| **DB** | Date indexes if not present |
| **Backend** | `ICalendarOverlaySource` implementation for native tasks (like Jira overlay); **no** default CalendarEvent writes |
| **API** | Reuse `/calendar/occurrences` (+ `sourceKind=Task`); extend overlay filters only if needed |
| **FE** | Toggle Tasks on CalendarView; rely on merged occurrences |
| **Security** | Same View filter as task list |
| **Tests** | Multi-day span; foreign space excluded; reschedule permission |
| **Risks** | Double-counting if someone also pinned events — document overlay vs pin |
| **Acceptance** | Tasks appear on calendar by due/start; existing events unaffected |

---

## C5 — Timeline / Gantt-lite · **L**

| | |
|--|--|
| **Scope** | Windowed hierarchy; day/week/month; Progress on bars; FS dependencies + cycle guard; undated bucket; date drag |
| **Deps** | C1; C3 optional (shared card chrome) |
| **DB** | `TaskDependencies` + RLS |
| **Backend** | Timeline query; dependency CRUD + cycle check |
| **API** | timeline + dependencies routes |
| **FE** | `TaskTimelineView` |
| **Security** | Edit for dep mutate / date drag |
| **Tests** | Cycle reject; overlap window; effort ≠ duration display contract (unit/UI) |
| **Risks** | Scope creep into full scheduler — **forbid** auto-reschedule cascade in C |
| **Acceptance** | Parent/child visible; dep line FS; drag updates dates only via PUT |

---

## C6 — Workload and estimate allocation · **L**

| | |
|--|--|
| **Scope** | Allocation rows; workload API; tenant default capacity; overload; FE editor + heatmap; Observer default 0; allocation vs actual vs calendar distinguished |
| **Deps** | C1; B2 assignees; B5 estimates/time |
| **DB** | `TaskEstimateAllocations` (+ optional history); capacity settings |
| **Backend** | Allocation service; workload aggregate by bucket |
| **API** | allocations + workload |
| **FE** | `TaskWorkloadView` + allocation editor on drawer |
| **Security** | Workload privacy: only users/tasks caller can View; no leaking titles via aggregate without View — **aggregate only over visible tasks** |
| **Tests** | Sum ≤ Original; assignee remove; overload flag; tenant isolation |
| **Risks** | Heavy aggregates — pre-aggregate buckets or limit range (≤ 31/90 days) |
| **Acceptance** | No double-count without allocation; actual series separate |

---

## C7 — Integration, testing, stabilization · **M–L**

| | |
|--|--|
| **Scope** | Outbox/activity/notification polish for transitions; EndpointAuthorization coverage; perf pass (board/timeline/workload); cursor pagination on list spine; docs/user-guide fa; regression suite; optional task migrate-to-latest job; **no new business views** |
| **Deps** | C1–C6 as shipped |
| **DB** | Indexes/fixes only; no destructive drops of enum without PO |
| **Tests** | Cross-view transition consistency; security matrix; FE view switcher |
| **Risks** | Notification ContentId gap remains — document |
| **Acceptance** | Builds green; Stage C exit criteria in parent roadmap; PO gate for Stage D |

---

## Parallelism diagram

```
C0 ──► C1 ──► C2 ──► C3 ──► C7
         │              ▲
         ├──► C4 ───────┤
         ├──► C5 ───────┤
         └──► C6 ───────┘
```

C4/C5/C6 after C1 can proceed in parallel **if** staffing allows; still serialize merge/stabilization into C7.

---

## Suggested ADR

| ID | Title | When |
|----|-------|------|
| **ADR-057** | Task status workflows (distinct from ADR-031 page rules) | Accept at C1 start |
| ADR-056 | Amend only if Status/Lifecycle dual-read wording needed | Minimal touch |

Do not revise ADR-031/027 semantics; reference them as boundaries.

---

## Exit criteria (Stage C overall)

1. Configurable statuses/transitions with version pin.  
2. Kanban uses server transitions only.  
3. Calendar shows task overlays without replacing Calendar SoT.  
4. Timeline shows hierarchy + FS deps (lite).  
5. Workload uses allocations (no naive N×estimate).  
6. Open|Done clients still function via Lifecycle.  
7. Security/RLS/tests at B6 bar or better.
