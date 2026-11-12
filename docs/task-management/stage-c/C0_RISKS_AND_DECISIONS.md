# C0 — Risks and Decisions (Stage C)

**Date:** 2026-11-12  
**Status:** Proposed for PO approval with C0 package. No code until C1 approved.

---

## 1. Decisions (propose)

| ID | Decision | Rationale |
|----|----------|-----------|
| D1 | **Task status machine ≠ ADR-031** page WorkflowEngine | Different SoT (graph of statuses vs event→actions); avoids dual use of `content.Workflows` |
| D2 | Keep **Lifecycle Open\|Done** denormalized from status category | 9a/CSV/complete guards/My Tasks compatibility |
| D3 | **Single Transition Service** for all status changes | Kanban cannot forge status; complete/reopen are aliases |
| D4 | Tasks **pin workflow version** | Prevents silent behavior change when designers publish |
| D5 | Workflow **admin = space Administer** | Align with ADR-031 who-may-define-rules |
| D6 | Designer = **form + matrix + read-only graph**, not canvas | ADR-033 deferred canvas; faster, a11y-friendly |
| D7 | Calendar = **`ICalendarOverlaySource`** (existing merge in CalendarService), not default CalendarEvent clone | Hook already used by Jira; avoids duplicate SoT and parallel endpoints |
| D8 | Gantt deps = **Finish-to-Start only** in C; no auto-cascade schedule | Limits XL scope |
| D9 | Workload demand = **allocations**, not estimate×assignees | B2 explicit risk |
| D10 | Stay in **Raavi.Wiki** for C | Same as B; extract module only with new ADR |
| D11 | Search/AI indexing of tasks **out of C** | 7c deferred; SQL+ACL filters only |
| D12 | Propose **ADR-057** at C1; do not rewrite ADR-056 beyond additive note | Stability |

---

## 2. Open questions for PO (non-blocking for C1 default path)

| # | Question | Default if unanswered |
|---|----------|------------------------|
| Q1 | Space-only workflows vs tenant template library first? | Space default + seed template |
| Q2 | Hard-block allocation sum > Original vs warn? | **Hard-block** |
| Q3 | Cancelled category → Lifecycle Done or hidden filter? | Lifecycle Done + `statusKey` filter |
| Q4 | Ship Kanban on default Open/Done before custom designer (C3 before C2 finishes)? | Allow C3 MVP on default workflow |
| Q5 | Transition notifications without ContentId? | Defer (ADR-056 constraint) |
| Q6 | Personal working calendars vs tenant defaults for capacity? | Tenant defaults in C6 |

---

## 3. Risks and mitigations

| Risk | Level | Mitigation |
|------|-------|------------|
| Dual workflow vocabulary confuses admins (“Workflows” page vs task workflow) | M | Naming: **Task workflow** / مسیر وضعیت کار; keep Automation under existing Workflows |
| Pinning versions leaves tasks on old graphs | M | UI badge “Workflow vN”; optional migrate job in C7 |
| Board query cost / N+1 | H | Caps, cursors, batched assignees, indexes (C1/C3/C7) |
| DnD accessibility | M | Status menu fallback on card |
| Dependency cycle + parent cycle interaction | M | Separate validators; shared DFS utility |
| Workload leaks task titles across ACL | H | Aggregate strictly over View-filtered task set |
| Scope creep (SLA, multi-type deps, canvas) | H | C0 non-goals; C7 rejects new views |
| complete() bypass if someone adds Status to PUT | H | Code review + tests; Transition-only writes StatusId |
| Enum column vs StatusId drift | M | Transition service sole writer; C7 consistency check |
| Calendar overlay + pinned event double display | L | Docs + distinct styling; no auto-pin |

---

## 4. Explicit non-decisions (later stages)

- Jira status ↔ Raavi status mapping (D)
- Task Approvals entity chains (E)
- `task.*` triggers on ADR-031 engine (E or C7 thin slice if PO insists — default **E**)
- Critical path / resource leveling
- Recurring tasks

---

## 5. C0 exit

Architecture coherent with B1–B6 and Raavi infra; roadmap C1–C7 sized; **await PO** to start C1 (and ADR-057 drafting as first C1 doc+code gate).
