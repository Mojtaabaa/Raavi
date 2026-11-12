# C0 — Data Model Proposal (Stage C)

**Date:** 2026-11-12  
**Constraint:** Additive only; no rewrite of applied B1–B6 migrations; RLS + `ITenantOwned` on every new table.

---

## 1. Principles

1. Open|Done remains a **derived lifecycle**, not abandoned.
2. Published workflow versions are **immutable** snapshots.
3. Tasks **pin** `(WorkflowDefinitionId, WorkflowVersion)`; status FK is within that version.
4. Dependencies and allocations are new; do not overload assignees or checklists.
5. Effort (minutes) ≠ calendar duration (date span).

---

## 2. Task workflow tables (C1–C2)

### 2.1 `content.TaskWorkflowDefinitions`

| Column | Notes |
|--------|--------|
| Id, TenantId | PK / tenant |
| SpaceId? | Null = tenant library template; non-null = space-owned |
| Name, Description | |
| IsEnabled | Soft off |
| LatestPublishedVersion | int, 0 if never published |
| DraftJson or row-based draft | Prefer **normalized draft tables** or single DraftDocument JSON validated server-side — choose normalized for queryability of statuses |
| Revision, Created*, Updated* | ETag on definition |

### 2.2 `content.TaskWorkflowVersions` (immutable publish)

| Column | Notes |
|--------|--------|
| Id, TenantId, DefinitionId, Version (int) | Unique (DefinitionId, Version) |
| PublishedAt, PublishedBy | |
| InitialStatusKey | Status key within version |
| Snapshot hash / Revision | Audit |

### 2.3 `content.TaskWorkflowStatuses`

| Column | Notes |
|--------|--------|
| Id, TenantId, VersionId | |
| Key | Stable code e.g. `open`, `in_progress`, `done` (unique per version) |
| NameFa / NameEn or Name + i18n later | C1: Name + locale maps in JSON ok |
| Category | `Active` \| `Terminal` \| `Cancelled` (Cancelled → Lifecycle Done for filters or separate — recommend Terminal+Cancelled both Lifecycle Done) |
| Color / SortOrder | Kanban column order |
| RequiresCompleteGuards | bool — apply open-children + mandatory checklist when entering |
| IsInitial | exactly one per version |

### 2.4 `content.TaskWorkflowTransitions`

| Column | Notes |
|--------|--------|
| Id, TenantId, VersionId | |
| FromStatusId, ToStatusId | |
| Key | optional named transition |
| Name | |
| RequiredPermission | `Edit` \| `Complete` \| `Administer` |
| AllowedRolesJson | e.g. `["Responsible","Approver"]` empty = use RequiredPermission only |
| ConditionsJson | validated schema: `noOpenChildren`, `mandatoryChecklistComplete`, future extensible |
| ActionsJson | limited: `setProgress`, `clearRemaining`, `notifyWatchers` (ContentId-gated) — no arbitrary code |
| SortOrder | |

Unique (VersionId, FromStatusId, ToStatusId).

### 2.5 Seed: system default workflow

Per space on first C1 migrate or lazy create:

- Statuses: `open` (Active, initial), `done` (Terminal, RequiresCompleteGuards=true)
- Transitions: open→done (Complete), done→open (Complete or Edit)
- Bind all existing tasks to this version; map enum Open/Done → status keys.

---

## 3. Changes on `content.Tasks` (C1)

| Column | Type | Notes |
|--------|------|--------|
| WorkflowDefinitionId | Guid? | Null only during migration window; then NOT NULL |
| WorkflowVersion | int | Pinned publish number |
| StatusId | Guid? | FK to TaskWorkflowStatuses (same tenant) |
| *(keep)* Status | tinyint enum | Dual-read Lifecycle; write only via transition service |
| *(keep)* CompletedAt/By | | Set when Category→Terminal |

Indexes (additive):

- `(TenantId, SpaceId, StatusId)` filtered `ArchivedAt IS NULL`
- `(TenantId, SpaceId, DueDate)` / `(StartDate, DueDate)` for calendar/timeline windows
- `(TenantId, WorkflowDefinitionId, WorkflowVersion)` for migrate jobs

**Do not drop** enum Status in C1; C7 may mark obsolete after dual-read period.

---

## 4. Dependencies (C5)

### `content.TaskDependencies`

| Column | Notes |
|--------|--------|
| Id, TenantId | |
| PredecessorTaskId, SuccessorTaskId | same space |
| Type | `FinishToStart` only in C (others deferred) |
| CreatedBy, CreatedAt | |

Constraints: unique active pair; cycle detection in domain (DFS/ancestor set); both tasks Viewable by mutator; Edit required.

No automatic date shifting (scheduling engine) in C — display + validate only; optional “block transition if predecessor not Terminal” as transition condition later.

---

## 5. Estimate allocation (C6)

### `content.TaskEstimateAllocations`

| Column | Notes |
|--------|--------|
| Id, TenantId, TaskId, UserId | Unique (TaskId, UserId) active |
| RoleSnapshot | role at allocation time (informational) |
| AllocatedMinutes | ≥ 0 |
| PercentOfOriginal | optional denorm; minutes are SoT |
| UpdatedAt, UpdatedBy, Revision | history via soft-replace or allocation history table |

**Rules:**

- Sum(AllocatedMinutes) ≤ OriginalEstimateMinutes (if Original null → allocations forbidden or all zero).
- Observer default: no row / 0.
- On assignee remove: soft-delete allocation or zero (explicit).
- On Original decrease below sum: reject estimate change or proportionally scale — **recommend reject** with `task.allocation_exceeds_estimate` until user fixes shares.
- ActualSpent remains from time entries (B5), not from allocations.

### Capacity (C6 lean)

Prefer **tenant settings** JSON (sandbox/tenant SQL settings) for default working hours (e.g. 8h × weekdays) before full personal calendars. Holidays: optional simple date list per tenant. Full HR calendar = later stage.

---

## 6. Opening balance / ActualSpent

Unchanged from B5/B6. Transitions must **not** rewrite ActualSpent. Optional transition action `clearRemaining` only touches RemainingEstimateMinutes.

---

## 7. Orphan / consistency checks (C7)

- Task StatusId must belong to pinned VersionId.
- No transitions referencing missing statuses.
- Allocation UserId ∈ active assignees (or allow leftover with warning — prefer enforce ∈ assignees).
- Dependency endpoints both non-archived (or allow archived read-only).

---

## 8. Migration safety sketch (implement in C1+, not C0)

1. Backup.
2. Create tables + RLS.
3. Seed default definition+v1 per space (or one tenant template cloned).
4. Backfill Tasks columns from enum.
5. Dual-write Lifecycle on every transition.
6. Never rewrite published version rows.

Idempotent seed: `IF NOT EXISTS` default definition per space.
