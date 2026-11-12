# Target Architecture — Task Management (Stage A)

## 1. Placement in modular monolith

**Recommendation (Stage A):** Keep persistence and services in **`Raavi.Wiki`** (current home) for Stages B–C; introduce Application abstractions (`ITaskQuery`, `ITaskCommands`, `ITaskAuthorizer`) so AI/Integrations/Workers never take a project reference on Wiki.

Extract a `Raavi.Tasks` feature module **only if** Wiki assembly weight or boundary tests force it — that is a later ADR, not Stage B prerequisite.

```
Application Abstractions (ITask*, DTOs)
        ↑ implemented by
Wiki Task* services / WorkTask (+ new tables in content schema)
        ↑ used by
Api endpoints · Workers jobs · FE via HTTP
Integrations (Jira links) via Application contracts
AI via ITaskTextReader / confirm gates (new)
```

## 2. Ownership model

| Kind | SoT | Raavi stores | Raavi may write |
|------|-----|--------------|-----------------|
| Native task | Raavi | Full row + children | Full lifecycle under ACL |
| External Jira issue | Jira | Link + cached snapshot + sync meta | Link CRUD; field refresh via connector; no silent local status override |
| Editor taskList | Document JSON | Inside page body | Editor only |

Do **not** unify Native and Jira into one mutable entity.

## 3. Authorization

- Continue **space View** as existence gate (404).
- Edit / transition / assign: space Edit **or** task-role grants (configurable).
- Related page/issue: evaluate **both** ends via `IPermissionEvaluator` / View filters.
- AI retrieval: same View filter before model context.

## 4. Events

Add outbox types (same SaveChanges as mutations), e.g. `task.created|updated|assigned|completed|reopened|archived|deleted|transitioned`. Project to Activity + Notifications + Workflow hooks.

## 5. Workflow strategy

**Two layers (do not conflate):**

1. **Task status workflow** — statuses + transitions owned by task configuration (new tables). Powers Kanban columns.
2. **Automation rules** — prefer extending existing `WorkflowEngine` with `task.*` triggers and safe actions (notify, add label on linked page, create calendar reminder) **or** a thin TaskAutomation service that reuses Hangfire/outbox. Page content workflows stay for pages.

Page **Approvals** (ADR-032) remain the pattern for multi-approver steps, not a second engine.

## 6. Calendar strategy

Prefer **Task date overlays** on existing Calendar views (query tasks by start/due in range) over cloning every task into `CalendarEvent` (avoids duplicates). Optional “pin to calendar” creates an event with back-link if users need attendees/reminders from the event model.

## 7. Search

Short term: SQL filters. Medium: extend index or dedicated `task` document type with ACL at query time (ADR-049 pattern). Never unfiltered AI passages.

## 8. FE structure

Keep `frontend/src/modules/tasks/`; add views/components under it; shared query composable; reuse PrincipalPicker, calendar date controls, i18n catalogs.
