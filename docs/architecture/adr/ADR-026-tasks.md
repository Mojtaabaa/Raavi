# ADR-026 — Tasks (Phase 9a)

**Status:** Accepted (2026-11-08)

## Context

Phase 9 ("advanced content types") groups five unrelated features: tasks, calendar, databases, slides and whiteboards. They are delivered one at a time: **9a tasks**, then 9b calendar (Jalali/Gregorian, recurrence), 9c databases, 9d slides (PDF/PPTX export), 9e whiteboards (Konva vs Fabric evaluation, Yjs). Each must have persistence, tenant ownership, permissions, navigation and export.

## Decisions

1. **A task belongs to a space, not to the content tree.** Tasks are not nodes of the page tree (no versions, no hierarchy, no restrictions of their own); inline task lists in pages stay as they are. \`content.Tasks\` is tenant-owned and RLS-protected like every table, with a composite foreign key to its space. The optional link to a page of the same space (\`ContentId\`) deliberately has **no** foreign key, so purging a page never fails because of a task that mentions it.
2. **Permissions come from the space, evaluated per request.** Reading needs View; creating, editing and deleting need the space's Edit right on an active space; an **assignee may complete and reopen their own task** with View only. A task of a space the caller cannot view does not exist for them (404), also for its list and export.
3. **Assignees must be able to view the space.** The assignee is checked against the *assignee's* rights (an explicit principal through the same authorizer), so a task can never be handed to someone who could not open it (\`task.assignee_no_access\`), nor to a non-member (\`task.assignee_invalid\`). Rights can change later; "My tasks" only lists tasks of spaces the member can still view.
4. **"My tasks" spans spaces** (assigned to the caller, viewable spaces only, open first, by due date, undated last). Optimistic concurrency with ETag \`"r{n}"\` on update.
5. **Due dates are calendar dates** (\`date\`, no time zone), shown in the active calendar (Jalali in Persian). "Overdue" compares with the current UTC date.
6. **Export** is CSV (RFC 4180, UTF-8 with BOM so spreadsheets open Persian text correctly) of the space's tasks. Cells a spreadsheet would read as a formula (starting with \`= + - @\`, tab or CR) are prefixed with an apostrophe.

## Not in 9a

Assignment notifications (the notification model is page/comment based), recurring tasks and calendar views (9b), tasks inside the editor linked to this store, search indexing of tasks, labels.
