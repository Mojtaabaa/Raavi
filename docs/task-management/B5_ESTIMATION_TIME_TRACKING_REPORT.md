# B5 — Estimation and Time Tracking Report

**Status:** Complete (B6 followed — see `B6_STABILIZATION_REPORT.md`)  
**Date:** 2026-11-12  
**Related:** ADR-056, [B1_DATA_MODEL_AND_MIGRATION_PLAN.md](B1_DATA_MODEL_AND_MIGRATION_PLAN.md) (M4)

## 1. Estimate and Time Entry model

| Concept | Storage | Notes |
|---------|---------|--------|
| Original Estimate | `Tasks.OriginalEstimateMinutes` | Task-level effort estimate (minutes). Editable after work starts; changes emit `task.estimate.original_changed`. |
| Remaining Estimate | `Tasks.RemainingEstimateMinutes` | **Manual** task-level remaining effort. Not auto-reduced on time log (avoids surprise). Changes emit `task.estimate.remaining_changed`. |
| Actual Spent | `Tasks.ActualSpentMinutes` | Denormalized **sum** of non-deleted `TaskTimeEntries.DurationMinutes`. |
| Progress | `Tasks.ProgressPercent` | **Manual** 0–100 SoT. Checklist progress remains separate (`ChecklistProgressPercent` on DTO). |
| Time Entry | `content.TaskTimeEntries` | UserId, WorkDate, DurationMinutes, Description?, IsOpeningBalance, soft-delete, Revision. |

**Effort vs calendar duration:** estimates and time entries are **effort minutes**, not calendar span between StartDate/DueDate.

## 2. ActualSpentMinutes ↔ Time Entries

- **SoT going forward:** sum of active time entries (including opening-balance rows).
- **No client write** of ActualSpentMinutes on `TaskRequest` (unchanged).
- **Recompute** after every create/update/delete of a time entry (`TaskTimeService.RecomputeActualAsync`).
- **Legacy:** migration seeds `IsOpeningBalance = 1` rows from existing `ActualSpentMinutes > 0` (attributed to `CreatedBy`, description “Opening balance…”). ActualSpent remains equal; no silent wipe.
- **Reconciliation:** summary exposes `totalMinutes`, `entryMinutes`, `openingBalanceMinutes`, and cached `actualSpentMinutes` (must match total).

## 3. Remaining Estimate policy

Manual only in B5. Completing a task does **not** force Remaining → 0. FE may later offer “set remaining = max(0, original − actual)” as a helper; not auto-applied.

## 4. Progress calculation policy

- `ProgressPercent`: user/manual only (ADR-056).
- `ChecklistProgressPercent`: derived from checklists (B3), display-only, never overwrites ProgressPercent.
- Time-based progress deferred (Stage C) to avoid two conflicting SoTs.

## 5. Migration

`20261112075325_TaskTimeEntriesB5`: table + RLS + SystemTenants FK; RLS off → seed opening balances → RLS on. Non-destructive Down drops the new table only.

## 6. API / Frontend

- `GET/POST /tasks/{id}/time`, `PUT/DELETE /task-time/{id}` (+ If-Match on edit).
- Estimate fields on existing task create/update; activity events for estimate/progress/time.
- FE: estimate inputs, list badges, `TaskTimePanel` (log/edit/delete, by-user totals, opening balance label).

## 7. Permissions and audit

- Log own time: space Edit **or** Responsible/Contributor (or legacy `AssigneeId`).
- Log for another user / edit opening balance: space Edit.
- Edit/delete own non-opening entry: owner; others: Edit.
- Reviewer / Approver / Observer: no self-log by default.
- Activity via wiki outbox (`task.time.*`, `task.estimate.*`, `task.progress.changed`). **No** default in-app notifications for time (product: avoid noise).

## 8. Tests

- Backend: `Time_tracking_sums_actual_spent_without_double_counting` + B1–B4 TaskTests regression.
- Frontend: tasks test stubs time panel; create body includes estimate fields.

## 9. Deferred (Stage C / later)

- Per-assignee estimate **allocation** / workload dashboard.
- Auto Remaining from Original − Actual.
- Progress derived from time.
- Jira time sync, AI estimates.
- Configurable past-date policy beyond fixed MaxPastDays.

## Concurrency

Entry `Revision` + task concurrency token on ActualSpent update; recompute uses Local+DB sum in one SaveChanges.
