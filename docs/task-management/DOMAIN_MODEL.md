# Domain Model — Task Management (Stage A proposal)

## 1. Evolve `WorkTask` (native)

Keep table `content.Tasks`. Additive fields (proposed):

| Field | Type | Notes |
|-------|------|-------|
| Priority | tinyint/enum | e.g. Lowest…Highest |
| TaskType | nvarchar | configurable code |
| ReporterId | Guid? | default CreatedBy |
| StartDate | date? | |
| OriginalEstimateMinutes | int? | |
| RemainingEstimateMinutes | int? | |
| ProgressPercent | tinyint? | optional manual |
| ParentTaskId | Guid? | same space; cycle-checked |
| WorkflowId | Guid? | |
| StatusCode | nvarchar | migrate Open/Done → codes |
| ArchivedAt | datetimeoffset? | soft archive |
| … | | keep Title, Notes, SpaceId, ContentId, Revision |

**Migration of Status:** map `Open→open`, `Done→done` into configurable statuses of system default workflow.

## 2. New aggregates (proposed)

| Table | Purpose |
|-------|---------|
| `content.TaskAssignees` | (TaskId, UserId, Role, IsPrimary, AssignedBy, AssignedAt, RemovedAt?) unique active (TaskId, UserId, Role) |
| `content.TaskChecklists` / `TaskChecklistItems` | Items with Done, optional Assignee, Due |
| `content.TaskWatchers` | User watch |
| `content.TaskComments` | Or reuse comment infra with TargetKind=Task — prefer dedicated for clarity |
| `content.TaskAttachments` | Metadata + blob key |
| `content.TaskTimeEntries` | User, minutes, date, note |
| `content.TaskRelations` | Task↔Task: Blocks, DependsOn, RelatesTo, Duplicates |
| `content.TaskWorkflows` / `Statuses` / `Transitions` | Configurable workflow |
| `content.TaskExternalLinks` | ConnectorId, ExternalId, Key, Snapshot JSON, LastSync* |
| `content.TaskReminders` | Schedule meta |
| `content.TaskRecurrence` | Series definition (bounded materialization) |

All: `ITenantOwned`, composite FKs to task/space as appropriate, RLS.

## 3. Assignment roles (initial)

`Responsible` | `Contributor` | `Reviewer` | `Approver` | `Observer`  
Exactly one primary Responsible when any Responsible exists (enforced in domain).

**Backfill:** existing `AssigneeId` → one `Responsible` (+ clear or keep denormalized AssigneeId as primary for compatibility during transition).

## 4. Relationships to knowledge

- Keep `ContentId` as “primary related page”.
- Additional pages/decisions: either multi-link table `TaskContentLinks` or extend KnowledgeRelationships with `TargetKind=Task` (**requires ADR** — today Content↔Content only).

## 5. External Jira

`TaskExternalLinks` stores connector + issue id/key + cached summary/status/assignee/due + sync state. Writes to Jira only through Integrations clients. Native status ≠ Jira status.

## 6. Explicit non-entities

- TipTap `taskItem` remains document content.
- CalendarEvent remains calendar SoT; task overlay is a query, not a mandatory dual write.
