# B1 — Data Model and Migration Plan (Enterprise Tasks)

**Status:** Approved and implementing (2026-11-12)  
**Related:** [ADR-056](../architecture/adr/ADR-056-enterprise-task-foundation.md), Stage A `DATABASE_AND_MIGRATION_PLAN.md`

## Baseline (M0)

Migration `20261112062201_TaskEnterpriseFoundation` (additive): Priority, StartDate, ParentTaskId, Original/RemainingEstimateMinutes, ArchivedAt, `content.TaskAssignees` + RLS + AssigneeId → Responsible backfill.

## Decisions

1. Keep `content.Tasks` / `WorkTask` as SoT; no parallel product.
2. Keep `AssigneeId` as denormalized primary Responsible (dual-read/write).
3. Status stays Open|Done in Stage B (no new workflow engine).
4. One active role per user per task: unique `(TenantId, TaskId, UserId) WHERE RemovedAt IS NULL`.
5. Estimates on the task only (not per assignee).
6. Page Comment/Attachment/Watch stay ContentId-bound; Stage B adds task-scoped tables later (B3–B5).
7. TipTap `taskList` remains editor content only.

## M1 (this step)

Additive columns on `content.Tasks`:

| Column | Type | Default |
|--------|------|---------|
| ActualSpentMinutes | int | null |
| ProgressPercent | tinyint | null (0–100) |
| BlockCompleteWhenOpenChildren | bit | 1 |

Index change on `TaskAssignees`: drop unique `(TenantId, TaskId, UserId, Role) WHERE RemovedAt IS NULL`; add unique `(TenantId, TaskId, UserId) WHERE RemovedAt IS NULL`. No silent deletes if duplicates exist — migration aborts.

## Later migrations (not M1)

- **M2 (B3):** TaskChecklists, TaskChecklistItems  
- **M3 (B4):** TaskComments, TaskAttachments, TaskWatchers — done (`20261112072911_TaskCollaborationB4`, + `Activity.TaskId`)
- **M4 (B5):** TaskTimeEntries (recomputes ActualSpentMinutes) — done (`20261112075325_TaskTimeEntriesB5` + opening-balance seed)

## Rollback

Forward-only EF in production. App rollback ignores new nullable columns. SQL restore for recovery. No destructive Down without PO approval.

## Compatibility

Existing `assigneeId` request/response fields preserved; new JSON fields additive. Hard DELETE and Open/Done complete/reopen unchanged.
