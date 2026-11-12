# ADR-056 — Enterprise Task foundation (Stage B)

**Status:** Accepted (2026-11-12)  
**Extends:** [ADR-026](ADR-026-tasks.md)

## Context

Stage A (`docs/task-management/`) confirmed that Phase 9a `content.Tasks` is production-ready for single-assignee space work items, and that Enterprise Task Management must evolve that table rather than invent a parallel product. Product owner approved Stage B.

## Decisions

1. **Stay in Wiki / `content` schema.** Introduce Application-facing DTOs; no new feature project in B.
2. **Additive schema.** New columns on `content.Tasks` (priority, start date, parent, estimates, archive, ActualSpentMinutes, ProgressPercent, BlockCompleteWhenOpenChildren) and `content.TaskAssignees` for multi-user roles. Keep `AssigneeId` as the denormalized **primary Responsible** for 9a clients (dual-read/write).
3. **Roles:** `Responsible`, `Contributor`, `Reviewer`, `Approver`, `Observer`. At most **one** Responsible (who is the sole `IsPrimary`); **one active role per user per task** (unique `(TaskId, UserId)` where `RemovedAt` is null). Role changes soft-remove the prior row and insert a new one so history is retained; `GET /tasks/{id}/assignees?history=true` (Edit) exposes removed rows.
4. **Estimates** are task-level only (Original / Remaining / ActualSpent denormalized). No per-assignee estimate duplication. ProgressPercent is 0–100 (manual until checklist/time derive it).
5. **Parent complete policy:** `BlockCompleteWhenOpenChildren` (default true) refuses Complete while any non-archived Open child exists.
6. **Outbox:** emit `task.created|updated|assigned|unassigned|completed|reopened|archived` with existing `WikiEvent` payload (`ContentId` = linked page when present). `ActivityProjection` handles `task.*`.
7. **Notifications:** in-app assignment notice only when the task has a linked page (`ContentId`), matching CalendarReminderJob’s Content FK constraint — full task-target notifications deferred.
8. **Archive:** `ArchivedAt` soft-hides from default lists; hard `DELETE` remains for Edit (9a) until a later policy change.
9. **Editor task lists** remain document nodes (ADR-026).
10. **Status** stays Open|Done in Stage B; Stage C may add StatusCode/WorkflowId additively.

## Consequences

- Migrations must register RLS on `TaskAssignees` (and later task-scoped tables).
- Mine/list queries include assignees table OR legacy `AssigneeId`.
- FE may keep single-assignee field while also showing `assignees[]`.
- See `docs/task-management/B1_DATA_MODEL_AND_MIGRATION_PLAN.md` for M0–M4 sequencing.
