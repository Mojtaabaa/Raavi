# B3 report — Subtasks and Checklists

**Date:** 2026-11-12  
**Status:** Complete — awaiting PO before B4

## Delivered

### Subtasks
- `ParentTaskId` hierarchy (max depth 8), cycle checks, same-space parent only.
- `GET /tasks/{id}/children`, `POST /tasks/{id}/promote` (clear parent, keep data).
- Delete parent blocked with `task.has_children` (FK Restrict + API).
- Archive parent does not cascade; children stay linked.
- `BlockCompleteWhenOpenChildren` unchanged (`task.open_children`).
- DTO: `childCount`, `openChildCount`.

### Checklists (not TipTap)
- Tables `content.TaskChecklists` / `TaskChecklistItems` + RLS (M2 migration).
- Multi-checklist per task; items: done/required, DoneAt/DoneBy, reorder API.
- `BlockCompleteWhenMandatoryIncomplete` (default true) → `task.mandatory_incomplete`.
- DTO: `checklistProgressPercent` from item completion.

### Frontend
- Nested list (roots + children), add subtask, promote, checklists in edit dialog.
- i18n fa/en; TipTap task lists remain separate.

### Tests
- Backend: subtasks/promote/mandatory complete.
- FE fixtures updated for new fields.

## Policies

| Action | Behavior |
|--------|----------|
| Complete | Blocks on open children and/or incomplete required items when flags on |
| Delete parent | Conflict until children removed/promoted |
| Archive | Soft-hides parent only |
| Promote | Clears ParentTaskId |

## Deferred (B4+)
Comments/attachments/watchers; time tracking; deeper tree virtualization for huge spaces.
