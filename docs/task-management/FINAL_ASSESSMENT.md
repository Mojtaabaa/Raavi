# Final Assessment — Stage A (Enterprise Task Management)

**Date:** 2026-11-12  
**Status:** Stage A approved; **Stage B (B1–B6) approved**; **Stage C C0 design complete** (`stage-c/`) — await PO for C1  
**Code/schema changes in Stage A:** none · **Stage B:** additive schema + API/FE (ADR-056) · **C0:** docs only

---

## 1. Existing Task capabilities

Raavi already has a **production-oriented Phase 9a task system** (ADR-026):

- Table `content.Tasks` / entity `WorkTask` inside **Wiki** module, RLS + space FK.
- Fields: title, notes, Open|Done, **single** assignee, due date, optional page `ContentId`, complete audit timestamps, revision/ETag.
- API: space list/filter, create, get/put, complete/reopen, delete, CSV export, **My tasks** (assignee across viewable spaces).
- Permissions: space View/Edit; assignee may complete; assignee must View space; opaque 404.
- UI: `/tasks` and `/spaces/:key/tasks` list + dialog (`TasksView.vue`).
- Tests: `TaskTests`, `tasks.test.ts`; export includes tasks.
- Side uses: Knowledge maintenance can create a space task; workspace export includes tasks.

**Separate:** TipTap checklist nodes in the editor are **not** WorkTasks.

---

## 2. Missing capabilities (vs PO brief)

Multi-assignee/roles · configurable workflow/statuses · subtasks/checklists · comments/attachments/watchers on tasks · estimates/time · dependencies · Kanban/Timeline/Workload · calendar overlay · recurrence/reminders/SLA · outbox/notifications for tasks · search indexing · AI task intelligence · treating Jira issues as first-class external tasks in task views · analytics · rich description · soft archive.

---

## 3. Modules to reuse

| Module | Reuse |
|--------|--------|
| Wiki authorizer / spaces / pages / labels | ACL + page links |
| Wiki outbox / activity / notifications | Extend with `task.*` |
| WorkflowEngine + approvals | Automation + approval **patterns** (not as page-status substitute) |
| Calendar | Views + recurrence/reminder **patterns**; task **overlays** |
| Integrations / PageIssueLinks | Jira SoT links + create/refresh clients |
| AI / Prompt Hub / RAG filters | Confirm-to-apply intelligence |
| Identity directory / FE pickers | Assignees |
| Hangfire Workers | Reminders, overdue, recurrence |
| Export | Extend payloads |
| i18n / display / Jalali | UX |

---

## 4. Proposed database changes (high level)

Additive columns on `content.Tasks`; new tables for assignees, checklists, watchers, comments/attachments, time, relations, workflows, external links, reminders/recurrence — all `ITenantOwned` + RLS. Backfill assignee → Responsible. **No destructive drops in B without approval.**

---

## 5. Proposed API changes

Keep 9a routes; add unified `/tasks` query, transition, assignees, sub-resources, board/timeline/workload/overlay helpers; AI draft endpoints with `confirmed`. Jira via external-links wrapping existing connector — **no invented Jira contracts**.

---

## 6. Frontend changes

Evolve `modules/tasks` (My/Team, detail, board, timeline, workload); Calendar overlay toggle; page “related tasks” + editor create; i18n fa+en. No second design system.

---

## 7. Integration dependencies

Workers process must run for scheduled features; Jira real-host verification still pending (fakes in CI); Templar blocked; OpenSearch/S3 deferred — plan SQL/local first.

---

## 8. Security concerns

Hard delete + missing outbox weaken audit story; 500-row cap; must not leak via facets/AI/related entities; Kanban must not client-side forge transitions; keep RLS and 404 semantics.

---

## 9. Implementation order

A ✅ → **PO gate** → B (foundation) → C (workflow/views) → D (knowledge/Jira) → E (advanced) → F (AI) → G (analytics/harden).  
First vertical: ADR extension + assignees dual-read + notifications.

Relative complexity: **B L · C XL · D L · E L · F L · G M–L**.

---

## 10. Document index

| # | Document |
|---|----------|
| 1 | [CURRENT_STATE_ASSESSMENT.md](CURRENT_STATE_ASSESSMENT.md) |
| 2 | [EXISTING_CAPABILITIES_MATRIX.md](EXISTING_CAPABILITIES_MATRIX.md) |
| 3 | [GAP_ANALYSIS.md](GAP_ANALYSIS.md) |
| 4 | [TARGET_PRODUCT_VISION.md](TARGET_PRODUCT_VISION.md) |
| 5 | [TARGET_ARCHITECTURE.md](TARGET_ARCHITECTURE.md) |
| 6 | [DOMAIN_MODEL.md](DOMAIN_MODEL.md) |
| 7 | [API_DESIGN.md](API_DESIGN.md) |
| 8 | [UI_UX_DESIGN.md](UI_UX_DESIGN.md) |
| 9 | [INTEGRATION_DESIGN.md](INTEGRATION_DESIGN.md) |
| 10 | [AI_TASK_INTELLIGENCE.md](AI_TASK_INTELLIGENCE.md) |
| 11 | [SECURITY_AND_AUTHORIZATION.md](SECURITY_AND_AUTHORIZATION.md) |
| 12 | [DATABASE_AND_MIGRATION_PLAN.md](DATABASE_AND_MIGRATION_PLAN.md) |
| 13 | [IMPLEMENTATION_ROADMAP.md](IMPLEMENTATION_ROADMAP.md) |
| 14 | [TEST_STRATEGY.md](TEST_STRATEGY.md) |
| 15 | [RISKS_AND_DEPENDENCIES.md](RISKS_AND_DEPENDENCIES.md) |
| 16 | This file |
| 17 | [stage-c/](stage-c/) — C0 Stage C architecture package |

---

## 11. Stop point / changelog

- **2026-11-12:** Stage B approved; C0 design package published. **No C1 code/schema until PO approval.**
- Stage A stop point (historical): no B until PO — completed.
