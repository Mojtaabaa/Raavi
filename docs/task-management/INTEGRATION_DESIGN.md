# Integration Design — Task Management (Stage A)

## 1. Wiki / knowledge

| Integration | Existing | Plan |
|-------------|----------|------|
| Link task → page | `ContentId` | Keep; add multi-links later |
| Tasks on page | None | Panel “Related tasks” query by ContentId |
| Editor → task | None | Command/selection → draft task API |
| Comment → task | None | Stage D |
| Decision/Incident | Page templates C1 | Link as pages |
| KnowledgeRelationships | Content↔Content | ADR needed before Task endpoints |
| Maintenance → task | Admin create task | Already works; keep |

## 2. Calendar

| Item | Plan |
|------|------|
| Overlay | Calendar queries task start/due in visible range |
| Reminders | Prefer `TaskReminders` + Hangfire; optional CalendarEventReminder if pinned |
| Meetings | Meeting notes → AI extract (Stage F); meeting entity already in Calendar |
| Templar | Disabled provider pattern exists — **blocked** without contract |

## 3. Workflow / automation

| Item | Plan |
|------|------|
| Content WorkflowEngine | Reuse for automation triggers `task.*` + actions Notify/AddLabel |
| Task status machine | Separate config (Kanban) |
| Approvals | Reuse ADR-032 patterns for Approver role |
| Stale sweep | Pattern for overdue task job |

## 4. Notifications

Reuse NotificationService + SignalR inbox. **New kinds** e.g. `task.assigned`, `task.due_soon`, `task.overdue`, `task.mentioned`. Respect mute/watch preferences; never notify users who cannot View the space.

## 5. Jira (Integrations)

| Capability | Repo status | Task plan |
|------------|-------------|-----------|
| Connectors allow-list SSRF | R | Reuse |
| Page link / create / refresh | Implemented in `PageIssueLinkService` | Reuse client |
| Issue as native task | Must not | External link entity |
| Unified board of native+Jira | New read model DTO | Stage D |
| ADR-043 “read-only” vs create | Create exists for pages | Document honestly in ops docs |

## 6. Search / export / webhooks

- Export already includes tasks JSON/CSV — extend fields when schema grows.
- Webhooks catalog: add `task.*` when outbox exists.
- OpenSearch module exists but deferred; SQL first.

## 7. AI module

Single door `CompleteAsync`; Prompt Hub contexts — add `task` / `tasks` providers reading via ACL-safe readers only.
