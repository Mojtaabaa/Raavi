# Implementation Roadmap — Task Management

Based on repository findings (not generic SaaS templates). **Stage A complete → wait for PO approval before B.**

Relative complexity: **S** small · **M** medium · **L** large · **XL** extra large

## Stage A — Assessment ✅

Docs under `docs/task-management/`. No code.

## Stage B — Core foundation (L)

| Work | Complexity | Notes |
|------|------------|-------|
| Additive schema + assignees/roles + backfill | L | |
| Checklist + parent/child + cycle checks | M | |
| Detail API/UI expansion | M | Extend TasksView / drawer |
| Watchers + outbox `task.*` + activity | M | |
| Comments/attachments MVP | M | |
| Estimates + time entries MVP | M | |
| Archive vs hard delete | S | |
| Tests isolation/assignee/roles | M | |

**Exit:** Native tasks usable beyond Open/Done fields; multi-assignee; no Kanban required yet.

## Stage C — Workflow and views (XL)

| Work | Complexity | Status |
|------|------------|--------|
| **C0** Architecture assessment & design | M | Done — `stage-c/C0_*.md`; await PO for C1 |
| **C1** Configurable workflow foundation | L | Not started |
| **C2** Workflow designer & transition rules | L | Not started |
| **C3** Kanban (server transition only) | L | Not started |
| **C4** Calendar task overlays | M | Not started |
| **C5** Timeline / Gantt-lite + FS deps | L | Not started |
| **C6** Workload + estimate allocation | L | Not started |
| **C7** Integration, testing, stabilization | M–L | Not started |

Detail, order, and acceptance: [`stage-c/C0_IMPLEMENTATION_ROADMAP.md`](stage-c/C0_IMPLEMENTATION_ROADMAP.md).

**Exit:** Five view families wired to one query spine + Transition Service; Open\|Done Lifecycle preserved.

## Stage D — Knowledge and integrations (L)

| Work | Complexity |
|------|------------|
| Page panel + editor create-task | M |
| Task↔content multi-links / KR ADR | M–L |
| Meeting action capture (manual) | M |
| Task↔Jira external links | L |
| Unified native+external list filters | M |

## Stage E — Advanced (L)

Dependencies, recurrence, reminders, templates, automation (`task.*` triggers), bulk ops, SLA extension points only.

## Stage F — AI (L)

Draft/extract/recommend/risk/assistant with confirm-to-apply; index or SQL retrieval with ACL.

## Stage G — Analytics and harden (M–L)

Dashboards, exports, perf, security regression, docs/user-guide, release package.

## Suggested first vertical after approval

**B0 (S):** ADR for enterprise tasks extending ADR-026 (assignees, archive, outbox).  
**B1:** Schema compatibility (M0/M1) — done.  
**B2:** Multiple assignees hardening (history, one Responsible, FE roles) — done; see `B2_MULTIPLE_ASSIGNEES_REPORT.md`.  
**B3:** Subtasks + checklists — done; see `B3_SUBTASKS_CHECKLISTS_REPORT.md`.  
**B4:** Comments / attachments / watchers — complete (`B4_TASK_COLLABORATION_REPORT.md`).  
**B5:** Estimation + time tracking — complete (`B5_ESTIMATION_TIME_TRACKING_REPORT.md`).  
**B6:** Testing and stabilization — complete (`B6_STABILIZATION_REPORT.md`).  
**Stage B** approved by PO. **C0** design complete (`stage-c/`); await PO before C1.

## Deviation rule

Any reordering must update this file and FINAL_ASSESSMENT changelog.
