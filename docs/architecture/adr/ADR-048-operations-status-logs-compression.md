# ADR-048 — Operations: status page, error log, activity log, compressed page bodies (Phase 15)

**Status:** Accepted (2026-11-09)

## Decisions

### 1. Status page and health
* `GET /health/status` (anonymous, any host, `no-store`, bypasses tenant resolution like the other `/health/*` probes) returns the overall state, version, uptime, the components (`self`, `sql`, `sql_speed`, `workers`, `outbox`, `disk`, each with state, answer time, a short fixed description), process size and 60 minutes of traffic (requests, 5xx, average, p95 estimated from a histogram, per minute). The status-only checks never decide readiness (`/health/ready` is unchanged). **Public on purpose**: the page must work when nobody can sign in; the content is limited to what is harmless (no connection strings, host names, paths, users, request content — covered by a test). `Raavi:Health:PublicStatus=false` switches it off (404). Traffic counters are per process, in memory (`RequestMetrics`, one-minute buckets); `/health/*` requests are not counted.
* `workers` = newest `hangfire.Server.LastHeartbeat` (absent/old → Degraded with the fix: install the service); `outbox` = pending events and the age of the oldest; version comes from `release.json` (copied to the install root by `Update-Raavi.ps1`).
* The SPA page `/status` (public, bare, served by the web site, so it loads when the API is down) polls it, and when it cannot, classifies WHY (no network / gateway 502-504 / answer that is not the API / switched off / timeout) with what to check. The router sends the app to it when the very first request (host context) fails (previously: a redirect loop). A `ConnectionBanner` on every page appears when real requests fail (network error or 502/503/504), probes `/health/live` and disappears by itself. Charts are hand-written SVG (no chart library).

### 2. Error log
`system.ErrorLog` (global, no RLS, no personal content). Sources: unhandled server exceptions (`UnhandledExceptionLogger`, an `IExceptionHandler` that records and then lets the normal handler answer; domain/concurrency/cancellation are not errors) and browser errors (`POST /errors/client`, anonymous, rate limited 10/min/IP, size capped; the SPA reports at most 5 distinct errors per page load, never while the server is unreachable). Writing is off the request path (bounded channel, drop oldest, batch insert). Text is cut and secrets (`password=`, tokens, bearer values) are redacted before storage. Identical errors share a fingerprint (type, route with ids removed, message with digits removed) so they are counted together. System administrators search every field (`/system/errors`), group, open the stack, purge; 90-day retention.

### 3. Activity log and chart
`GET /tenant/activity-log` (workspace administrators): all content/space activity entries, searchable by kind, title, space key/name, person (names resolved through the directory), version, with kind/space/actor/time filters and paging. `GET /tenant/security-events/search`: security events with person names and the same kind of search. `GET /activity/stats` (any member): per-day-per-kind counts, per-hour counts and top people over the spaces/pages the caller may view (same visibility filter as the feed); counts and names only. Limit: only what the outbox projects into `content.Activity` is logged (page/space events); row-level changes of databases, slides and boards are not.

### 4. Database size and speed
* **Page bodies are stored gzip-compressed** (`PageVersions.ContentJson` is `varbinary(max)`, `GzipText` = gzip of UTF-16, byte-compatible with SQL Server `COMPRESS`). The dev database went from 114.7 MB to 10.4 MB for this table (the migration also rebuilds it). `LIKE` on bodies is no longer possible (the importer's placeholder pass now filters in memory). The migration switches the RLS policy off for the single migration transaction (RLS has no tenant bypass for the migrator, ADR-005), checks that every row was converted before dropping the old column, switches the policy on again, and rolls everything back on any failure — upgrades stop the application first.
* Nightly `DatabaseMaintenanceJob` (Workers, 03:30): purges finished outbox events (14 d), expired/revoked sessions (30 d), error log (90 d) in slices of 5 000 rows, then `sp_updatestats`; each step is independent and failure-tolerant (`Raavi:Maintenance`).
* Existing indexes were reviewed (outbox status/available, activity tenant/time, sessions); no blind indexes were added.

### 5. Site speed
API JSON compression (Brotli fastest, gzip fallback, HTTPS included; not for files). IIS keeps static compression and 30-day caching of hashed assets. The activity chart and the status page ask for small aggregated answers instead of lists.

### 6. Navigation
A real breadcrumb (`Breadcrumbs.vue`, `stores/crumbs.ts`): views that know more (space, page ancestors, database, board, deck) hand over their trail, other routes use the route table; phones fold the middle levels into a menu. It replaces the page view's own breadcrumb, which duplicated the space layout and fought it for the same space.

### 7. Themes, cropper, whiteboard
Two Material Design 3 themes (light/dark: tonal surfaces, rounded corners, pill buttons). The crop dialog gained a round mask with thirds, live previews at real sizes, rotate/mirror/reset, pinch and keyboard (and fixes a render loop of the first version: `clamp` assigned a new object inside the watcher of the same value). Whiteboard: templates (flowchart, kanban, SWOT, mind map), copy/paste, one-step layer order, align to grid, Tab navigation, zoom keys.

## Not covered
Request metrics are per API process (not shared between several processes). No alerting (the page is for people and for external monitors polling `/health/*`). The error log does not group across versions of the code. Compression of other large text columns (drafts, templates) was not done. The migration time on a large database was not measured (only the 5 000-row dev database).
