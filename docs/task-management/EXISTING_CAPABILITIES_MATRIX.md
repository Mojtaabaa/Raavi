# Existing Capabilities Matrix — Task Management (Stage A)

Legend: **R** = production-ready · **I** = incomplete · **P** = partial · **N** = missing · **C** = concern (security/architecture) · **S** = separate related capability (reuse carefully)

| Area | Capability | Class | Evidence |
|------|------------|-------|----------|
| Core | Space-owned task entity | R | `WorkTask`, `content.Tasks` |
| Core | Title / notes / Open\|Done | R | Domain + API |
| Core | Single assignee | R | `AssigneeId` |
| Core | Due date (date-only) | R | `DueDate` |
| Core | Optional page link | R | `ContentId` no FK |
| Core | Complete / reopen | R | Endpoints + assignee rule |
| Core | ETag concurrency | R | `Revision` + If-Match |
| Core | Hard delete | R | DELETE |
| Core | Soft archive / version history | N | — |
| Core | Priority / type / start / estimate | N | — |
| Core | Multi-assignee + roles | N | — |
| Core | Parent / subtasks | N | — |
| Core | Checklist entity | N | Editor `taskItem` is S |
| Core | Time entries / progress | N | — |
| Core | Watchers | N | Page watchers exist (S) |
| Core | Task comments / attachments | N | Page comments/attachments (S) |
| Core | Labels on tasks | N | Content labels (S) |
| Permissions | Space View/Edit gating | R | `TaskService` + `WikiAuthorizer` |
| Permissions | Assignee View check | R | `GetSpaceRightsAsync(principal)` |
| Permissions | Tenant RLS | R | Migration + `ITenantOwned` |
| Permissions | Task-specific ACL | N | Inherits space only |
| API | Space list / mine / CRUD | R | `TaskEndpoints` |
| API | Faceted query / saved views | N | status+assignee only |
| API | Bulk ops | N | — |
| UI | Space + My lists | R | `TasksView.vue` |
| UI | Kanban / Gantt / Workload | N | — |
| UI | Calendar overlay of tasks | N | Calendar is separate (S) |
| UI | Task detail workspace | I | Dialog only |
| Notify | Assignment / due / overdue | N | Page/comment/approval kinds (S) |
| Events | Outbox task.* | N | Wiki content events (S) |
| Workflow | Task status machine | N | Content workflows only (S) |
| Workflow | Page approvals pattern | S | ADR-032 reusable pattern |
| Calendar | Events + recurrence + reminders | S | ADR-027; no task overlay |
| Search | Index WorkTasks | N | `ContentIndexSource` pages only |
| AI | Task NL create / assistant | N | Page AI + RAG + Prompt Hub (S) |
| Jira | Issue as WorkTask | N | — |
| Jira | Page↔Issue link + create | S | `PageIssueLinkService` |
| Knowledge | Task↔page edge store | P | Optional `ContentId`; KR is Content↔Content (S) |
| Export | CSV + workspace ZIP | R | TaskService + ExportService |
| i18n / RTL / Jalali | List UI | R | Existing stacks |
| Tests | API + FE unit | R | `TaskTests`, `tasks.test.ts` |

## Reusable building blocks (not task-specific)

| Building block | Owner | Reuse for Tasks |
|----------------|-------|-----------------|
| `IPermissionEvaluator` / `WikiAuthorizer` | Wiki | Space + content View for links |
| Outbox + ActivityProjection | Wiki | Emit `task.*` when added |
| NotificationService + kinds | Wiki | New kinds for assignment/due |
| Hangfire / `ITenantJob` | Infra/Workers | Reminders, recurrence, overdue |
| Calendar boards/events/reminders | Wiki | Overlay or generate events carefully |
| WorkflowEngine | Wiki | Extend triggers/actions **or** task-specific workflow tables |
| Approvals (page) | Wiki | Pattern for task approval steps |
| KnowledgeRelationships | Wiki | Content↔Content; extend for Task endpoints carefully |
| PageIssueLinks / connectors | Integrations | External issue SoT |
| AiService.CompleteAsync + Prompt Hub | AI | Confirm-to-apply task drafts |
| Labels | Wiki | Either task-label table or link via page |
| Principal directory / pickers | Identity + FE | Assignees |
| ETags helper | Infra | Keep on updates |
| Display/timezone/Jalali | Tenancy + FE | Dates |
