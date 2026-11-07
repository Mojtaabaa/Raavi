# ADR-009: Content Storage

**Status:** Accepted · **Date:** 2026-11-06

## Context
Eight content types (page, live doc, blog post, whiteboard, database, folder, shortcut, slide deck) share navigation, permissions, search, lifecycle and export, but have different bodies.

## Decision
- One generic **`content.Content`** table for the tree/identity/lifecycle of every content type (`ContentType` key), plus type-specific tables for bodies (`PageVersions`, `Whiteboards`, `UserDatabases`, `SlideDecks`, …).
- `ContentClosure` table for ancestor queries (breadcrumbs, inherited restrictions, subtree moves/permissions).
- Each type implements **`IContentProvider`** (contract in code since Phase 0): capabilities, search extraction, export, plus (Phase 3) create/validate/move/archive/delete hooks and version handling. A registry resolves providers; unknown types are errors.
- Page bodies: canonical Raavi Document JSON (ADR-010) in `PageVersions.ContentJson` with `SchemaVersion` and SHA-256 `ContentHash`; HTML render cache stored separately (`ContentRenderCache`, invalidated by version/schema change).
- Live documents = pages with continuous collaboration and no explicit publish step (auto-published snapshots on idle).
- Shortcuts store a target (content id or URL) and are ACL-checked against their target when followed.
- Large binary content (attachments, snapshots, exports) in object storage (ADR-014).

## Alternatives considered
- Separate tables per content type for tree data — rejected: duplicated tree/ACL/search logic and `switch` statements.
- Storing HTML as canonical — rejected: lossy, XSS-prone, hard to migrate.

## Consequences
`Content` is a hot table: clustered on `(TenantId, Id)` with covering indexes for `(TenantId, SpaceId, ParentId, SortOrder)`.
