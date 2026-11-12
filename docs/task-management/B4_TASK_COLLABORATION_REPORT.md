# B4 — Task Collaboration Report

**Status:** Complete — awaiting PO before B5  
**Date:** 2026-11-12  
**Related:** [ADR-056](../architecture/adr/ADR-056-enterprise-task-foundation.md), [B1_DATA_MODEL_AND_MIGRATION_PLAN.md](B1_DATA_MODEL_AND_MIGRATION_PLAN.md) (M3)

## Architecture decisions

1. **No parallel engines.** Page `Comments` / `Attachments` / `Watches` stay `ContentId`-bound. Tasks use dedicated `content.TaskComments`, `TaskAttachments`, `TaskWatchers` that reuse the same patterns: `RaaviDocument` + `Comment.MentionedUsers`, `Attachment.Sniff` / `CleanFileName` / `Raavi:Attachments:MaxBytes`, `WatchState`, wiki outbox + `ActivityProjection`, `NotificationFanOut` (extended).
2. **SQL bytes until 7b.** Same as page and whiteboard attachments — not a new storage architecture. Soft-delete clears `Data` and sets `DeletedAt`; hard task delete cascades rows.
3. **Notifications (ADR-056 §7 preserved).** In-app fan-out runs only when the task has a linked page (`ContentId`). Notify outbox types use the `notify.task.*` prefix so they are not projected as activity. Mentions/replies/watchers/attachments still emit activity via `task.*` WikiEvents always.
4. **Activity TaskId.** Additive nullable `content.Activity.TaskId` + `WikiEvent.TaskId` so task history is queryable without title matching.
5. **Creator auto-watch.** Matches page auto-watch on authoring/commenting; muted state prevents re-add.
6. **No malware scanner** in the product yet — deferred with Phase 7b.

## Database (migration `TaskCollaborationB4`)

| Change | Notes |
|--------|--------|
| `Activity.TaskId` | nullable + index `(TenantId, TaskId, OccurredAt)` |
| `TaskComments` | soft-delete, revision concurrency, parent/thread, RLS |
| `TaskAttachments` | `varbinary(max)` Data, optional `CommentId`, soft-delete, RLS |
| `TaskWatchers` | unique `(TenantId, TaskId, UserId)`, Watching/Muted, RLS |

Non-destructive / forward-compatible. Down drops new tables and the Activity column (not for production without PO).

## Backend / API

- `TaskCollaborationService` + endpoints under `/api/v1/tasks/{id}/comments|attachments|watchers|watch|activity`, `/task-comments/{id}`, `/task-attachments/{id}`.
- Authz: space View for read/download; Comment or Edit to write; author or Edit to delete; Edit to add watchers for others.
- Mentions: tenant members only; edit notifies **added** mentions only.
- Optimistic concurrency on comment edit via `If-Match: "r{revision}"`.

## Frontend

- `TaskCollaborationPanel` in the task edit dialog: comments (compact editor + @mentions), attachments, watchers, activity; en/fa i18n; RTL via `dir="auto"`.

## Security

- View task required for all subresources (foreign id → 404).
- Download re-checks space View (not only upload).
- Tenant RLS on new tables; no guessable unauthenticated URLs.
- Soft-deleted attachment bytes cleared.

## Tests

- `TaskTests.Collaboration_comments_attachments_watchers_mentions_and_activity` (+ B1–B3 regression in the same class).

## Remaining limitations

- In-app notifications for tasks **without** a linked page remain deferred (ADR-056).
- No object-storage / malware scan (7b).
- Comment edit history is `EditedAt` only (same as pages).
- Activity for pre-B4 task events has null `TaskId` until new events are written.
- B5 time tracking not started.
