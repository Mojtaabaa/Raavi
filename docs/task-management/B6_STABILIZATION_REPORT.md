# B6 — Testing and Stabilization Report

**Status:** Complete — awaiting PO before Stage C  
**Date:** 2026-11-12  
**Scope:** Quality, security, data integrity, performance review, UX consistency for B1–B5. **No** new business features (workflow, Kanban, Gantt, allocation, Jira task sync, AI).

## 1. Capability matrix (B1–B5)

| Capability | Implemented | Automated tests | Notes / gaps |
|---|---|---|---|
| Enterprise task columns (Progress, ActualSpent, BlockComplete*) | Yes | TaskTests B1 paths | — |
| Multiple assignees + one Responsible | Yes | Assignees / history / conflict | Concurrent unique → mapped conflict |
| Assignment history (soft-remove) | Yes | history=true needs Edit | Viewers see active only |
| Parent/child depth ≤8, cycle guard | Yes | Subtasks + **new** cycle/ETag fact | Self-parent & ancestor cycle → `task.parent_invalid` |
| Promote / delete-with-children | Yes | promote + `task.has_children` | Archive leaves children |
| Checklists + mandatory complete | Yes | mandatory block complete | TipTap taskList unrelated |
| Comments + mentions + soft-delete | Yes | collab fact | Soft-deleted hidden; `includeDeleted` **Edit only** (B6) |
| Attachments (SQL bytes) + download ACL | Yes | upload/download/soft-delete 404 | Until Phase 7b S3 |
| Watchers + idempotent watch | Yes | duplicate watch OK | Auto-watch on create/comment |
| Activity.TaskId feed | Yes | comment/attachment/time/estimate kinds | — |
| Notifications (mention) + outbox idempotency | Yes | DispatchOutbox replay | Notify only with linked ContentId (ADR-056) |
| Original / Remaining estimate (manual) | Yes | update + activity | No auto Remaining from Actual |
| ProgressPercent manual vs ChecklistProgress | Yes | DTO both | Independent SoTs |
| Time entries + ActualSpent recompute | Yes | sum/edit/delete/reconcile flag | Opening balance seed in B5 migration |
| Opening balance distinct + no double count | Yes | IsOpeningBalance false on API logs; migrate seed | Edit opening needs Edit |
| Tenant isolation / RLS on task tables | Yes | cross-tenant 404s | B6 index on active time entries |
| ETag / If-Match on task & time & comment | Yes | 428/412 paths | FE sends `"r{n}"` |

### Doc ↔ code inconsistencies found and fixed in B6

| Issue | Resolution |
|---|---|
| Soft-deleted comments still listed by default (post-delete total) | Default list excludes `DeletedAt`; `includeDeleted` opt-in |
| `includeDeleted` available to View-only | **Forbidden** without Edit (parity with assignee history) |
| Time summary lacked explicit reconcile signal | `actualSpentMatchesEntries` on `TaskTimeSummaryDto` + FE warning |
| Active time-entry list lacked filtered index | Migration `TaskTimeActiveIndexB6` |
| WIKI_API.md omitted B4/B5 routes | Documented comments/attachments/watchers/time |

## 2. Migrations (B1–B5 + B6)

| Migration | Role | Destructive? |
|---|---|---|
| `TaskEnterpriseFoundation` / `TaskEnterpriseB1Compatibility` | Assignees + B1 columns | No (abort on duplicate assignee) |
| `TaskAssigneesPrimaryUnique` | One primary Responsible | No |
| `TaskChecklistsAndMandatoryComplete` | Checklists + policy flags | No |
| `TaskCollaborationB4` | Comments/Attachments/Watchers + Activity.TaskId | No |
| `TaskTimeEntriesB5` | Time table + RLS + opening-balance seed | No (Down drops table only) |
| `TaskTimeActiveIndexB6` | Filtered `IX_TaskTimeEntries_TenantId_TaskId_Active` | No |

**Safety:** No rewrite of applied migrations. EF history is append-only. Retry of a failed migration mid-transaction relies on SQL Server transaction rollback; opening-balance seed is guarded with `NOT EXISTS` for idempotent re-seed within the B5 script. **Backup before upgrade:** standard SQL backup of the tenant database; recovery = restore + re-run Migrator. No orphan-repair script shipped (none required after review); if `ActualSpentMatchesEntries` is false in production, recompute via logging a zero-duration no-op is **not** provided — escalate for a one-off SQL sum update under PO approval.

### Opening balance rules (verified)

- Flagged `IsOpeningBalance = 1`; API-created rows are always `false`.
- Included in ActualSpent sum once; soft-delete of normal entries does not alter opening rows.
- Edit/delete of opening balance requires space Edit; owners cannot silently rewrite legacy spent via opening rows.

## 3. Security review

| Threat | Result |
|---|---|
| Cross-tenant task / comment / attachment / time | 404 (tenant scope + RLS) |
| IDOR download soft-deleted attachment | 404 |
| IDOR delete/edit soft-deleted time entry | 404 |
| View-only assignee history / deleted comments | Forbidden |
| Reviewer logging time without Edit | Forbidden (unless Responsible/Contributor) |
| Mentions outside tenant/membership | Filtered server-side |
| Auth on all task routes | Group `RequireAuthorization`; EndpointAuthorizationTests guard |
| Frontend-only ACL | Not relied upon; services call `WikiAuthorizer` / space rights |

**No critical architecture conflict requiring stop.** Residual: in-app notifications without linked page remain deferred (ADR-056); attachment bytes still in SQL until 7b.

## 4. Business rules consistency

Backend and FE agree on: open-children / mandatory checklist complete blockers; one Responsible; cycle/depth; ActualSpent from entries; Remaining/Progress manual; checklist progress separate; watcher uniqueness; comment soft-delete redaction.

## 5. Performance notes

- Workspace / mine lists capped (500); comments paginated; time list Take(500); activity Take≤100.
- B6 filtered index supports active-entry sum/list by task.
- No new caching layer. N+1 risk mitigated by batch name lookups (`GetUserNamesAsync`) in collab/time DTOs.
- Formal multi-tenant load benchmark not run in this phase (environment: LocalDB integration tests only).

## 6. Frontend stabilization

- `TaskTimeSummary.actualSpentMatchesEntries` + mismatch banner (`tasks.timeMismatch` en/fa).
- Time panel loading/empty/form states unchanged; 409/412 surface via `errorMessage`.
- Tasks Vitest stub updated for reconcile flag. No broad UI refactor.

## 7. Test matrix

| Area | Coverage |
|---|---|
| Create/list/filter/complete/reopen | `Tasks_are_created_listed_…` |
| Assignee View-only complete | `Assignees_must_be_able_to_view_…` |
| ETag / archive | existing facts |
| Multi-assignee / history / conflict | assignee facts |
| Subtasks / checklist / promote | `Subtasks_checklists_…` |
| Cycle + stale ETag | **`Subtask_cycle_and_etag_conflict_…` (B6)** |
| Comments/mentions/watchers/attachments/outbox/tenant | collab fact (+ includeDeleted ACL, soft-delete download) |
| Time / ActualSpent / reconcile / cross-tenant | time fact (+ soft-deleted resurrect 404) |
| FE My Tasks / space create / roles / time panel | `frontend/tests/tasks.test.ts` |

## 8. Observability

Uses existing wiki outbox + activity projection; problem details with stable error codes (no secrets/body dump). Correlation via host middleware (unchanged). No new metrics sinks in B6.

## 9. Deployment / upgrade

1. Backup SQL databases.  
2. Run Migrator (applies through `TaskTimeActiveIndexB6`).  
3. Deploy API + SPA together (new DTO field is additive; older SPA ignores mismatch flag).  
4. Smoke: create task → log time → ActualSpent updates; soft-delete comment → default list empty; foreign tenant 404.

## 10. Known limitations → Stage C+

- Configurable workflow / Kanban / calendar overlay / Gantt / workload allocation  
- Auto Remaining = Original − Actual; time-derived Progress  
- Notifications without ContentId; S3 attachments (7b); OpenSearch (7c)  
- Formal perf harness beyond LocalDB integration tests  
- Admin “recompute ActualSpent” maintenance endpoint (not added; flag only)

## 11. Acceptance checklist

| Gate | Status |
|---|---|
| Backend build (`dotnet build Raavi.slnx`) | Pass (2026-11-12) |
| Frontend build (`npm run build`) | Pass (2026-11-12) |
| Backend filter `TaskTests\|TenantIsolationModelTests` | Pass — 13 tests, 0 failed |
| Frontend `tests/tasks.test.ts` | Pass — 4 tests |
| Migrations reviewed (non-destructive) | Pass |
| Tenant isolation / RLS reviewed | Pass |
| Critical/High defects open | None known after B6 fixes |

**PO:** Stage B foundation is stabilized for operational use. Await approval before Stage C.
