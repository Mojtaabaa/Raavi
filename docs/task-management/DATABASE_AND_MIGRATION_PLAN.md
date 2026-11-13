# Database and Migration Plan (Stage A — proposal only)

**No migrations executed in Stage A.**

## 1. Current table

`content.Tasks` — see migration `20261107202438_Tasks.cs` and snapshot. Clustered AK `(TenantId, Id)`, FK to Spaces, RLS.

## 2. Strategy

1. **Additive only** for B: new columns nullable / with defaults; new tables.
2. **Backfill** `TaskAssignees` from `AssigneeId` where not null.
3. **Status migration:** introduce `StatusCode` + default workflow; map enum Open/Done; keep enum column until dual-read removed (follow-up migration).
4. **No drop** of `AssigneeId` until FE/API consumers migrated (explicit PO approval).
5. **Archive:** add `ArchivedAt`; change DELETE API to archive by default.
6. Every new tenant-owned table: `ITenantOwned`, `AddRaaviDbContext` registration already in Wiki, `migrationBuilder.AddTenantIsolation`, composite FKs, idempotent RLS scripts if patched.
7. Indexes from query plan: assignee+role, due, space+status, parent, external (connector, issue id).

## 3. Proposed Stage B migration set (names illustrative)

- `TaskEnterpriseCore` — columns on Tasks + TaskAssignees + Checklists + Watchers  
- Later stages: Relations, Workflows, TimeEntries, ExternalLinks, Reminders, Recurrence  

## 4. Rollback

Forward-only EF migrations; rollback = deploy previous app that ignores new columns/tables. Do not destructive Down in production without approval.

## 5. Compatibility

- Export/import ZIP: extend serializers; old imports without new fields remain valid.
- Space import today may not import tasks (IMPLEMENTATION_STATUS) — document when added.
- Editor taskList untouched.

## 6. Data volume

List APIs must move from Take(500) to **cursor pagination** before large-tenant rollout (Stage C).
