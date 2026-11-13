# C0 — API Design (Stage C)

**Date:** 2026-11-12  
**Compatibility:** Keep all B1–B6 routes; additive endpoints and DTO fields only.

---

## 1. Task DTO extensions

Existing Task DTO gains:

```
workflowDefinitionId, workflowVersion,
status: { id, key, name, category, color },
lifecycle: "Open" | "Done",   // mirrors enum / filters
```

`status` string Open|Done on old clients: keep emitting top-level `status` as lifecycle for 9a.

---

## 2. Workflow administration

| Method | Path | Auth | Notes |
|--------|------|------|-------|
| GET | `/spaces/{key}/task-workflows` | View | List definitions enabled for space |
| GET | `/spaces/{key}/task-workflows/default` | View | Default bind + published graph |
| POST | `/spaces/{key}/task-workflows` | Administer | Create definition |
| GET/PUT | `/task-workflows/{id}` | View / Administer | Definition + draft; PUT ETag |
| POST | `/task-workflows/{id}/publish` | Administer | Validate → immutable version++ |
| POST | `/task-workflows/{id}/disable` · `/enable` | Administer | |
| GET | `/task-workflows/{id}/versions` | View | |
| GET | `/task-workflows/{id}/versions/{n}` | View | Full statuses+transitions snapshot |
| PUT | `/spaces/{key}/task-workflows/default` | Administer | Point space default at definition |

**Publish validation errors** (400 problem+json): missing initial, orphan transition, duplicate keys, no path to terminal (warn vs error — **error** if zero terminals), self-loop optional allow.

---

## 3. Transitions (only status mutation path)

| Method | Path | Auth | Body |
|--------|------|------|------|
| GET | `/tasks/{id}/transitions` | View | → `{ from, allowed:[{toStatusId,key,name}] }` computed for **caller** |
| POST | `/tasks/{id}/transitions` | per transition rule | `{ toStatusId \| transitionKey, comment? }` + **If-Match** |

Errors:

| Code | HTTP |
|------|------|
| `task.transition_forbidden` | 403/409 |
| `task.transition_unknown` | 400 |
| `task.open_children` | 409 |
| `task.mandatory_incomplete` | 409 |
| `precondition.failed` | 412 |

**Aliases (keep):**

- `POST /tasks/{id}/complete` → transition to configured primary terminal (RequiresCompleteGuards).
- `POST /tasks/{id}/reopen` → transition to initial (or configured reopen target).

Kanban/UI must not PATCH status on `PUT /tasks/{id}`.

---

## 4. Board (Kanban)

| Method | Path | Notes |
|--------|------|-------|
| GET | `/spaces/{key}/tasks/board` | `workflow=` optional; `assignee=`, `priority=`, `dueBefore=`, `dueAfter=`, `q=`, `includeArchived=` |
| | | Response: `{ workflowVersion, columns:[{ status, total, items:[CardDto], nextCursor? }] }` |
| | | Cap per column (e.g. 50) + cursor; never unbounded |

CardDto: subset of Task + `allowedTargetStatusIds?` (hint only).

---

## 5. Unified list spine (shared)

| Method | Path | Notes |
|--------|------|-------|
| GET | `/spaces/{key}/tasks` | Extend filters: `statusKey=`, `lifecycle=`, `parent=`, cursor `after=` — migrate off silent Take(500) |
| GET | `/tasks/mine` | Same filters; optional `role=` |

---

## 6. Calendar overlay

**Preferred:** register `ICalendarOverlaySource` (Application abstraction already consumed by `CalendarService.OccurrencesAsync`) so existing:

| Method | Path | Notes |
|--------|------|-------|
| GET | `/calendar/occurrences?from=&to=&…&sourceKind=` | Includes Task overlays when registered; filter `sourceKind=Task` |
| GET | `/spaces/{key}/calendar/occurrences` | Space-scoped merge |

Optional C4 filters (assignee/lifecycle) may extend overlay query args or stay as FE post-filter of task-sourced rows — prefer server filter inside the Task overlay implementation (View ACL already required).

**Avoid** a second `/calendar/task-occurrences` unless overlay registration cannot carry filters; Wiki implements the source via Application interface (same pattern as Integrations→Jira), not a Calendar↔Tasks project cycle.

Reschedule: existing `PUT /tasks/{id}` dates + If-Match (Edit or policy).

---

## 7. Timeline

| Method | Path | Notes |
|--------|------|-------|
| GET | `/spaces/{key}/tasks/timeline?from=&to=&root=` | Hierarchical nodes overlapping window + undated bucket meta |
| GET/POST | `/tasks/{id}/dependencies` | List / add DependsOn |
| DELETE | `/task-dependencies/{id}` | |

Cycle → `task.dependency_cycle` 400.

Schedule drag: PUT dates (not transition).

---

## 8. Workload & allocation

| Method | Path | Notes |
|--------|------|-------|
| GET | `/spaces/{key}/workload?from=&to=&userIds=` | Per-user capacity, allocated minutes, actual spent, overload flag |
| GET/PUT | `/tasks/{id}/allocations` | Replace set with ETag on task or allocation revision |
| GET | `/tasks/{id}/allocations/history` | Edit only (optional C6) |

Errors: `task.allocation_exceeds_estimate`, `task.allocation_user_invalid`.

---

## 9. Idempotency & concurrency

- Transition: If-Match required; duplicate POST with same revision after success → 412 or return current (document: **412**).
- Publish workflow: ETag on definition; concurrent publish → 412.
- Board reads are eventually consistent with last write; no board-level ETag required.

---

## 10. Error disclosure

Stable codes only; no workflow draft JSON or other tenants’ names in problems. Foreign ids → 404.
