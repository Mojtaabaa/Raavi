# Gap Analysis — Enterprise Task Management (Stage A)

Maps PO requirements to repository reality. **Reuse** / **Modify** / **New** / **Defer**.

## 1. Core native task

| Requirement | Gap | Action |
|-------------|-----|--------|
| Rich fields (priority, type, start, estimate, progress, reporter, archive) | Missing on `WorkTask` | **Modify** schema + API (additive columns) |
| Soft archive vs hard delete | Hard delete only | **Modify** (align with content archive conventions) |
| Unique human-readable key | GUID only | **Defer** or **New** optional `TaskKey` per space |
| Organization ID separate from tenant | Raavi uses Tenant (= workspace) + Space; no org layer | **Reuse** Tenant as org; do not invent OrganizationId |
| Description rich text | Plain `Notes` nvarchar(4000) | **Modify** → Raavi Document JSON or capped HTML later; Phase B: keep text, raise limit carefully |
| Concurrency | Already Revision/ETag | **Reuse** |

## 2. Assignees

| Requirement | Gap | Action |
|-------------|-----|--------|
| Multiple assignees + roles | Single `AssigneeId` | **New** `TaskAssignees` table; migrate current assignee → Responsible |
| Assignment history | None | **New** |
| Notifications on assign | None | **New** kinds + **Reuse** NotificationService |
| Workload split of estimate | No estimates | **New** with allocation rules (Stage C/E) |

## 3. Subtasks / checklists

| Requirement | Gap | Action |
|-------------|-----|--------|
| Parent/child WorkTasks | None | **New** `ParentTaskId` + cycle checks |
| Checklists | Only editor nodes | **New** checklist tables; do **not** bind TipTap lists automatically |
| Progress from children | None | **New** |

## 4. Collaboration

| Requirement | Gap | Action |
|-------------|-----|--------|
| Comments on tasks | Page-only | **New** task comments **or** thin wrapper; prefer dedicated table to avoid forcing ContentId |
| Attachments | Page attachments | **New** task attachments (reuse storage abstractions when 7b lands; local store today) |
| Mentions / watchers | Page patterns | **Reuse** mention pipeline patterns; **New** task watchers |
| Activity / audit | No task outbox | **Modify** emit outbox + ActivityProjection |

## 5. Workflow

| Requirement | Gap | Action |
|-------------|-----|--------|
| Configurable task statuses/transitions | Open/Done only; WorkflowEngine is **content** events | **New** task workflow model **or** carefully extend engine with `task.*` triggers — **do not** overload page workflows as status machine |
| Transition authz / conditions | N/A | **New** |
| Page approvals as pattern | ADR-032 | **Reuse** pattern for Approver role steps |
| Inheritance Space → Tenant → System | Content workflows are per-space | **New** precedence rules for task workflows |

## 6. Views

| View | Gap | Action |
|------|-----|--------|
| My Tasks sections | Mine = assignee only | **Modify** query for roles once multi-assignee exists |
| List advanced | Basic list | **Modify** FE + unified query API |
| Kanban | Missing | **New** FE + transition API |
| Calendar | Tasks not on calendar | **Reuse** Calendar UI; **New** overlay query (prefer overlay over duplicating events) |
| Timeline/Gantt | Missing | **New** (Stage C); light dependency viz |
| Workload | Missing | **New** (Stage C); needs capacity source |

## 7. Relationships & knowledge

| Requirement | Gap | Action |
|-------------|-----|--------|
| Task↔Task deps | Missing | **New** (Stage E) |
| Task↔Page | `ContentId` only | **Modify**/extend; optionally KR with Task as endpoint (**New** contract — KR today is Content↔Content) |
| Decision/Incident | Templates only (C1) | **Reuse** pages + links |
| Meeting → actions | Calendar meetings exist | **New** extraction later (AI Stage F) |
| Create task from editor selection | Missing | **New** FE + API (Stage D) |

## 8. Jira

| Requirement | Gap | Action |
|-------------|-----|--------|
| Native vs External ownership | PageIssueLink already Jira-owned fields | **Reuse**; **New** optional Task↔Issue link or unified inbox DTO |
| Treat Jira issue as WorkTask | Must not | **Defer**/avoid |
| Create/link/refresh | Exists for **pages** | **Reuse** connector; **New** task-side link API |
| Read-only connector ADR-043 vs CreateIssue | Create from Raavi exists on pages | Document real capability; no invented APIs |

## 9. AI

| Requirement | Gap | Action |
|-------------|-----|--------|
| NL create / extract / recommend | None for tasks | **Reuse** `AiService.CompleteAsync`, Prompt Hub, confirm-to-apply (as Editor AI) |
| RAG over tasks | Tasks not indexed | **Modify** index or query SQL with ACL |
| Bypass auth | Must not | **Reuse** View filters |

## 10. Search / analytics / SLA / recurrence

| Area | Gap | Action |
|------|-----|--------|
| Smart search tasks | Not indexed | **New**/Modify indexer (Stage E/G) |
| Analytics | None | **New** Stage G |
| Recurring tasks | None (calendar recurrence is for events) | **New** Stage E; reuse recurrence ideas from Calendar, not event table |
| Reminders | Calendar event reminders only | **Reuse** Hangfire; **New** task reminder rows |
| SLA engine | None | **Defer** extension points only |

## 11. Security gaps / concerns

| Item | Class |
|------|-------|
| Hard delete loses history | C — prefer archive before enterprise audit claims |
| No task outbox → weak audit | C |
| List capped 500 without cursor | C for large spaces |
| Creating tasks from maintenance with Edit | OK; ensure same rules |
| Cross-tenant | R (RLS) — keep |
