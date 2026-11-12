# B2 report — Multiple Assignees Hardening

**Date:** 2026-11-12  
**Status:** Complete — awaiting PO before B3

## 1. Issues found in prior model

- Role changes updated `Role` in place → prior role not retained in `TaskAssignees` history.
- Multiple `Responsible` rows were possible (only “primary” was soft-enforced).
- No unique index guaranteeing a single `IsPrimary` row per task.
- No assignee history API; activity titles did not include role.
- Concurrent unique violations could surface as unhandled DB errors.
- FE could not change roles without re-picking users; groups were selectable for assignees.

## 2. Changes

### Backend
- `ApplyAssigneesAsync`: normalize one role/user; **at most one Responsible** (`task.multiple_responsible`); Responsible ⇒ sole `IsPrimary`; **role change = soft-remove + insert** + `task.unassigned` / `task.assigned` (title includes `· {role}`).
- Tasks without Responsible remain allowed (`AssigneeId` null).
- Legacy `assigneeId` dual-write keeps non-Responsible roles; promotes via soft-remove when needed.
- `GET /tasks/{id}/assignees?history=` — active for View; history (incl. `RemovedAt`) requires Edit.
- `DbUpdateException` on create/update → `task.assignee_conflict` (409).
- Optimistic concurrency still via ETag / revision.

### Database
- Migration `TaskAssigneesPrimaryUnique`: unique filtered index `(TenantId, TaskId) WHERE RemovedAt IS NULL AND IsPrimary = 1` (additive).
- Existing unique `(TenantId, TaskId, UserId) WHERE RemovedAt IS NULL` retained (B1).

### Frontend
- Per-assignee role `Select` (five roles); `PrincipalPicker` `users-only`.
- Client guard for multiple Responsible; loading disable on save; existing error toast path for API conflicts.

## 3. Constraints

| Constraint | Purpose |
|------------|---------|
| Unique active (TaskId, UserId) | One assignment per user |
| Unique active IsPrimary per Task | One primary Responsible |
| Domain: ≤1 Responsible | Aligns API with product rule |

## 4. API compatibility

- Existing list/create/update/mine/export/complete unchanged for 9a clients using `assigneeId`.
- Additive: richer `assignees[]` fields, `GET …/assignees`, new error codes.
- No Responsible required (unchanged policy).

## 5. Security / tenant isolation

- Assign still requires active tenant membership + space View for assignee (`task.assignee_invalid` / `task.assignee_no_access`).
- Cross-tenant user id rejected as invalid member.
- History endpoint Edit-gated; space View still required to see the task (404 otherwise).
- RLS unchanged on `TaskAssignees`.

## 6. Tests

- Backend `TaskTests` (roles ×5, multiple Responsible, history, ETag stale, foreign/unknown assignee, history Edit gate).
- Frontend: create payload + edit/unassign roles test.
- Regression: prior TaskTests + FE tasks suite.

## 7. Deferred

- B3 checklists/subtasks UI polish; B4 comments/attachments/watchers; B5 time entries.
- **Workload / allocation** (Stage C): estimates stay **task-level**; Observer is included in My tasks (assignment) but must **not** double-count estimate when workload lands — document for C.
- Deactivated membership: blocked at assign time; orphaned historical rows kept (no auto-purge).

## 8. Migration / rollback

- `TaskAssigneesPrimaryUnique` is additive index-only.
- Rollback = drop index / redeploy prior app; no data deletion.
- If duplicate primaries existed, index create fails (abort — no silent deletes); none expected after B1 normalize.
