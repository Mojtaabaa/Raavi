# ADR-037 — Workspace data export (Phase 12a)

**Status:** Accepted (2026-11-08)

## Context

Phase 12 ("Enterprise readiness") starts with data portability: `MULTI_TENANT_ARCHITECTURE.md` §7 describes an export job producing an archive in object storage and, separately, a two-step tenant deletion with a purge job. Object storage (7b) is not available yet, and a purge that deletes rows across every module is destructive and needs its own design and a second-administrator confirmation. 12a therefore delivers the part that is useful now and safe: **export**.

## Decisions

1. **On-demand archive, nothing stored.** `GET /tenant/export` (tenant administrators) builds a ZIP into a temporary file (`DeleteOnClose`), sends it as a download and the file disappears with the response. No job table, no expiry links, no object storage. The cost is that a very large workspace can exceed request time limits; the job-and-link variant belongs to the object-storage slice and can reuse `ExportService.WriteAsync` unchanged.
2. **Scope = what the exporting administrator may view, not a tenant dump.** The export follows the same permission evaluation as browsing: spaces with View access, current pages that pass space and page restrictions (`FilterViewableAsync`), their labels, non-deleted comments and attachments; then tasks, calendar events, databases with rows, slide decks and whiteboards of those spaces (JSON; SVG for boards). A tenant administrator is not automatically allowed to read restricted pages or private spaces in the product, so the export must not become a back door to them. The manifest says so (`scope`) and counts the pages left out (`pagesOmittedNoAccess`). A *complete* dump for off-boarding or legal hold is a different, system-administrator operation that needs a second confirmation (not built).
3. **Left out on purpose:** drafts, archived and trashed content, page history/versions, permissions and restrictions, watches, notifications, approvals, workflow runs, AI usage, webhooks, sessions and every secret.
4. **Format (version 1):** `manifest.json` (tenant slug, time, exporter id, counts, attachment bytes, omissions), `users.json` (ids → display names of the people referenced), `spaces/{key}/space.json`, `pages/{id}.json` (title, parent, labels, body as Raavi Document JSON, comments with their Document bodies, attachment list), `attachments/{pageId}/{id}-{name}`, `tasks.json`, `events.json`, `databases/{id}.json`, `slides/{id}.json`, `whiteboards/{id}.json|.svg`. File names inside the archive are sanitised (no separators, no `..`, no control characters, ≤ 120 characters), so a hostile attachment name cannot escape its folder when unpacked. Attachments are capped at 1 GB in total; those beyond the cap are listed as omitted (`size_cap`).
5. **Load control.** One export per workspace at a time (`409 export.in_progress` otherwise). The response is `no-store`.
6. **Not yet audited in a dedicated log:** the exporter's id and time are in the manifest only; a tenant audit trail does not exist in the Wiki module yet.

## Deferred

Tenant deletion with retention window and purge job (destructive, second-administrator confirmation, certificate in the system audit log), full-tenant dump by the installation administrator, job/link delivery via object storage, import of an export, incremental exports, export of history/versions, audit-log entry per export.
