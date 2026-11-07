# ADR-019: Versioning and Publishing

**Status:** Accepted · **Date:** 2026-11-06

## Context
Users need drafts, autosave, explicit publishing, history, diff and restore — without a version per keystroke. Imported history must coexist.

## Decision
- Content status: `Draft`, `Published`, `Archived`, `Deleted` (trash). A published page can have a pending shared draft.
- **Draft** = collaboration document (ADR-011), autosaved continuously; "Save draft" creates a named draft checkpoint (`VersionType=Draft`, not visible to readers).
- **Publish** creates an immutable `PageVersion` (`VersionType=Published`) with change summary and author(s) (all contributors since last publish recorded in `PageVersionContributors`), updates `PublishedVersionId`, emits outbox events.
- **Scheduled publishing**: a publish request with `PublishAt` stored and executed by a tenant job; content shows "scheduled".
- **Approval** (foundation in Phase 5, full workflows in Phase 10): if the space requires approval, publish creates a `PublishRequest` that reviewers approve; approval performs the publish.
- **Compare**: structural diff on Document JSON (node-level, then text-level within changed nodes) rendered side-by-side/inline; works across schema versions by migrating both sides to current.
- **Restore** creates a new version (`VersionType=Restore`, `RestoredFromVersionId`) — history is never rewritten.
- **Imported versions** (`VersionType=Import`) keep `SourceVersionIdentifier`, original author/time; shown in the same history UI with an "Imported" badge.
- Review date and expiration date per content; reminders via notifications; expired content flagged (not hidden) unless tenant policy says otherwise.
- Trash: soft delete with retention; purge by retention job or admin.

## Phase 5 outcome (2026-11-07)

Implemented: `ContentStatus.Draft` (never published, creator-only), one shared `PageDraft` per page (autosave, If-Match on its revision, contributor list), `Publish` as its own permission action, `PageVersionContributors`, `VersionType.Restore` with `RestoredFromVersionNumber`, scheduled publishing (delayed tenant job that re-validates schedule and the scheduler's rights — membership read fresh), `PublishRequest` snapshots for spaces with `RequirePublishApproval` (space administrators publish directly and approve), server-side structural diff, trash with restore and purge. Not yet: review/expiry dates, retention job, named checkpoints, compression (no measurements yet; versions are stored uncompressed).

## Consequences
Version rows can be large; `ContentJson` is stored compressed (`COMPRESS()`/`DECOMPRESS()` — or application-side gzip) for versions older than the current one (decision with benchmark in Phase 5).
