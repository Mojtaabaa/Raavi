# Implementation Status

**Last updated:** 2026-11-08 (Phase 9b: calendar)

Statuses: Not Started · Planned · In Progress · Implemented · Tested · Blocked · Deferred. "Tested" requires executed test evidence (listed).

## Phase 9 — Advanced content types (split: 9a tasks and 9b calendar done; 9c databases, 9d slides, 9e whiteboards pending)

### 9b — Calendar (ADR-027)

```
cd backend && dotnet test Raavi.Tests    → Passed: 372, Failed: 0   (new: CalendarRecurrenceTests 10 pure, CalendarTests 4 API; isolation model test covers content.CalendarEvents)
EF: has-pending-model-changes (Wiki)     → none; new migration Calendar (RLS + composite FK)
cd frontend && npm run build && npm test → ok; 13 files, 109 tests (new: calendar.test.ts 6); vue-tsc clean
```

Space calendars with all-day and timed events (stored as wall-clock + IANA zone), daily/weekly/monthly/yearly recurrence with interval, weekdays, end date or count, evaluated in the Gregorian or the Persian calendar for monthly/yearly rules; cancelling single occurrences; ETag concurrency; permissions from the space; cross-space feed (`/calendar`) limited to viewable spaces; iCalendar export (Persian month/year series as explicit RDATE lists). UI: nav "Calendar" and a per-space link, month grid in Jalali or Gregorian (toggle, tenant default), RTL-aware navigation, create/edit dialog with the date shown in the active calendar next to the native date input. Verified through unit/API/component tests; **not exercised in a real browser** (no dev session this step).

**Not in 9b:** editing one occurrence separately, attendees/reminders, iCalendar import/subscription, browser-side time-zone conversion, a Jalali date picker (the native `<input type=date>` is Gregorian; the Jalali date is shown beside it), search indexing.

### 9a — Tasks (ADR-026)

```
cd backend && dotnet test Raavi.Tests    → Passed: 358, Failed: 0   (new: TaskTests 4; isolation model test covers content.Tasks)
EF: has-pending-model-changes (Wiki)     → none; new migration Tasks (RLS + composite FK)
cd frontend && npm run build && npm test → ok; 12 files, 103 tests (new: tasks.test.ts 3); npm audit (production) 0
```

Space tasks with assignee, due date, notes and optional page link; permissions from the space (Edit to write, the assignee may complete/reopen their own task, assignees must be able to view the space, a task of an invisible space does not exist); "My tasks" across spaces; ETag concurrency; CSV export (UTF-8 BOM, formula-safe). UI: nav "Tasks" (`/tasks`) and a "Tasks" link per space (`/spaces/:key/tasks`) with create/edit dialog, open/done filter, overdue tag, dates in the active calendar. Verified through API/unit tests; **not exercised in a real browser** (no dev session).

**Not in 9a:** assignment notifications, recurring tasks and calendar views (9b), search indexing, linking inline editor task lists to this store, labels. Each of 9b–9e is a separate step.

## Phase 8 — Confluence migration (IN PROGRESS; source chosen: REST first, XML later)

### 8a-1 — REST source adapter and storage-format converter (done)

```
cd backend && dotnet build Raavi.slnx            → Build succeeded (warnings as errors)
cd backend && dotnet test Raavi.Tests            → Passed: 328, Failed: 0   (new: StorageFormatConverterTests 11, ConfluenceRestSourceTests 14 incl. 8 host-policy cases)
live (RAAVI_CONFLUENCE_TEST_URL/TOKEN set)       → Confluence 10.2.13 build 23551: 11 spaces, 327 versions (first 12 pages of each space, all versions) fetched via REST, converted and validated by the real editor schema: 0 failures
```

Live findings on the customer's test server (Data Center 10.2.13; resolves Q2 for this build): `/rest/api/content/{id}/version` returns 404, the **experimental** endpoint lists all versions (number, author, time, message) and `?status=historical&version=N` returns historical bodies; version/build come from `/rest/applinks/1.0/manifest` (`/rest/api/settings/systemInfo` is 404); `/rest/api/search/user` is 404; `type=attachment` listing returns nothing (attachments must be read per page). Dataset: 782 current pages, 1668 versions, 4 distinct authors, depth ≤ 5, 24 labels, no blog posts, comments or restrictions seen. Unsupported macros in this data (kept as escaped text, counted): `ac:macro` (old-style, 40), `contentbylabel`, `roadmap`, `children`.

| Component | Status | Notes |
|---|---|---|
| `StorageFormatConverter` (XML parse, DTD prohibited, no resolver; fixed entity table) | Tested + live | headings, lists (loose text/nesting), tables with spans, code/noformat, panels (info/note/warning/tip/panel), expand, status, toc, task lists, layouts flattened, inline-comment markers, images lifted to blocks, links (page → `raavi:confluence-page?space=&title=` placeholder for the link pass, unsafe hrefs dropped), unknown macros → `unsupported` node with escaped source + counted + warning, unparseable bodies kept escaped |
| `ConfluenceRestSource` (read-only GET, bearer PAT, throttling, 429/5xx back-off, runtime version-endpoint probe) | Tested + live | token never in errors/logs |
| `SourceHostPolicy` (allow-list, loopback/link-local/metadata always refused, re-checked at connect time) | Tested | `Raavi:Migration:AllowedSourceHosts` (empty = deny all) — **not yet wired to configuration** |

### 8a-2 — Importer, job machinery and administration API (done; no UI yet)

```
cd backend && dotnet build Raavi.slnx            → Build succeeded (warnings as errors)
cd backend && dotnet test Raavi.Tests            → Passed: 341, Failed: 0 (twice in a row)   new: ConfluenceMigrationTests 11, Hangfire serialization regression 1, fan-out regression 1; push test made race-proof
EF: has-pending-model-changes (Wiki, Migration)  → none; new migrations Wiki `ImportProvenance` (additive columns + filtered unique indexes), Migration `InitialMigration` (schema migration, RLS)
live, real stack (API + Workers + Hangfire + LocalDB) against Confluence 10.2.13, via the HTTP API as the dev tenant admin:
  dry run   → 11 spaces, 782 pages, 1668 versions read and converted, 0 failures
  execution → 782 pages, 1669 versions imported, 261 links resolved / 9 unresolved, validation 0 issues
  re-run    → imported exactly 1 new version (a page edited in Confluence meanwhile), nothing duplicated
  database  → 782 unique pages, 1670 imported versions, no numbering gaps, current version = newest everywhere, no leftover link placeholders, no tokens left on finished jobs
```

**Acceptance (roadmap):** *spaces, pages, all versions, authors, timestamps, links migrated; re-run creates no duplicates* — met for spaces, pages, versions (original time, comment, source author kept as source identity), hierarchy (parents before children), labels, home pages and links; authors are kept as *source identities*, not mapped to Raavi users (see below). Attachments were **not** imported (51 references seen; images are imported as image nodes without a file).

**Defects found and fixed while running it end to end:**
- *Tenant jobs never executed on a real Hangfire server (since Phase 3):* `TenantJobRunner.RunAsync<TJob,TArgs>` is a generic method and Hangfire does not store method type arguments, so jobs could be enqueued but never deserialized ("does not contain a method with signature"). This also affected Phase 5 scheduled publishing, whose test only used a recording fake. Fixed by making the runner a generic class; the regression test round-trips a scheduled job through Hangfire's real serializer.
- *Notification fan-out (Phase 7a)* added an auto-watch row before checking that the page still exists → a `content.created` event for a purged page failed on a foreign key and retried forever. Fixed (+ test).
- *Push race (Phase 7a):* a notification created between a client connecting and the push service's first look at that user became the silent baseline and was never pushed. The service now also signals a user it sees for the first time; the test now asserts the client's real behaviour (re-reading its unread count on every signal).
- The generated migration base class `Migration` clashes with the `Raavi.Migration` namespace; the generated Migration-module migration declares its base type fully qualified (do the same for future ones).

| Component | Status | Notes |
|---|---|---|
| `IContentImporter` / `ContentImporter` (Wiki) | Tested + live | page-atomic, schema-validated, idempotent by unique source keys; no wiki events emitted; link pass |
| `MigrationDbContext` (Jobs, Events), RLS | Tested | token stored only protected, erased when the job ends |
| `ConfluenceMigrationJob` (Hangfire tenant job): dry run, execute, pause/cancel/resume, per-page failure isolation, space-key conflicts, report | Tested + live | owner must still be a tenant administrator when it runs |
| Admin API `/api/v1/migrations/confluence` (docs/api/MIGRATION_API.md) | Tested + live | tenant administrators only; allow-listed hosts only |

### 8a-3 — Migration administration UI (done; browser check pending)

```
cd frontend && npm run build   → vue-tsc + vite build succeeded
cd frontend && npm test        → 10 files, 96 tests passed (new: migration.test.ts 6)
```

`/admin/migrations/confluence` (tenant administrators; nav item "Migration"): job list, "New migration" dialog (address, token in a password field with `autocomplete=off`, dry run **by default** or import, optional space list and key mapping), job detail with progress bar and counters (polled every 3 s while a job is active), pause / cancel (confirmed) / start / resume / run again — a new token is requested only when the stored one was erased (finished, cancelled or failed jobs) — and the report: totals, per-space table, key conflicts, unsupported macros, localized warnings, unmapped authors, link-pass result, validation, and the event log. Source/migration error codes have fa/en messages (raw server text is never shown). The token is never rendered or kept after the dialog closes (tested). Tests: parsing of the space list and key map, list → detail with report, dry-run creation payload (token only in the request body), start of a ready job and the active-state controls, localized host-not-allowed error, administrator-only route.

Browser check (2026-11-07, Persian UI, dev stack, the real jobs of the earlier live import): the page lists the four jobs, the detail shows progress 100 %, counters (11/11 spaces, 782/782 pages, 1669 versions, 0 failed, 36 warnings), the "token needed again" prompt for a finished job, and the full report (validation passed, unsupported macros, localized warnings, unmapped authors, 261 links resolved); the create dialog opens at 576 px with the token in a password field (autocomplete off), dry run preselected and the submit disabled until address and token are filled. Not exercised in the browser: submitting a new job and live polling of a running job (covered by unit tests; the live run was driven through the API earlier). One job from before the Hangfire fix is still shown as Queued (its Hangfire job can never run); cancelling it only sets a flag a worker would honour, so it stays Queued.

### 8c — Comments (done; verified with fakes, the real server has none)

```
cd backend && dotnet test Raavi.Tests   → 354 passed, 0 failed (new: comment import 1)
cd frontend && npm run build && npm test → ok, 100 tests
EF: has-pending-model-changes (Wiki)    → none; new migration ImportedComments (AuthorId nullable, SourceAuthorName, source id + filtered unique index)
```

Page and inline comments with replies flattened into threads, quoted text as inline anchor, resolved state, original times, source author names for people who are not members (no invented users), no notifications, idempotent re-runs (ADR-025 §15). The REST adapter reads `child/comment?depth=all` with body, history, inline properties, resolution and ancestors. **Not verified against real comments**: the test server has none, so only the request shape/empty result was checked live; field names (`extensions.location`, `inlineProperties.originalSelection`, `resolution.status`, `history.createdDate`) follow Confluence's documented REST shape.

### 8b — Attachments, people and restrictions, dynamic lists (done; see limits)

```
cd backend && dotnet test Raavi.Tests   → 353 passed, 0 failed (new: attachments 3, people/restrictions 2, macros 3, ids 1, live byte-exact download 1 [gated])
cd frontend && npm run build && npm test → build ok; 100 tests (new: dynamic-lists 2; migration page extended)
live on the real server (Confluence 10.2.13), dry run → 61 files (2.7 MB), 0 restricted pages, authors listed with e-mail suggestions (none matched the dev workspace)
live import with attachments + re-convert → all 61 files stored (0 duplicates), images point at their files (17 current versions), children ×1 and contentByLabel ×20 converted, unsupported macros down from 4 kinds to 2 (`ac:macro` 40 old-style, `roadmap` 1), 0 failures
```

New editor nodes (shared manifests + fixtures, backend renderer, frontend views, slash commands, fa/en): `raavi.children`, `raavi.contentByLabel` (ADR-025 §14). Defects found and fixed on the way: attachment ids collided across workspaces (tenant now part of the hash; found by tests); the real `contentbylabel` macro names the page's own space by key and carries `type=page` — the first converter rejected both (found by comparing with real data); four JPEGs were imported at twice their size although the adapter now downloads every file byte-exactly (live test) — the source reports inconsistent sizes for them; import now flags size disagreements and re-convert replaces changed bytes.

| Feature | Status | Notes |
|---|---|---|
| Attachments (latest version) with deterministic ids, size limit, failure isolation, refresh | Tested + live | bytes in SQL until S3 exists; images link to their file |
| Authors and restriction subjects → members (explicit map / verified e-mail on opt-in, suggestions in the dry run); groups by name | Tested | live data has no restrictions and e-mails of another domain, so only the suggestion path ran live |
| Page restrictions (narrowing only, importer always included, never overwritten) | Tested | not exercised on real data (the test server has none) |
| `children` / `contentbylabel` as dynamic lists | Tested + live conversion | browser rendering of the lists: see below |
| Space permissions | **Not imported** | REST does not expose them on this server; needs the XML reader |

**Still not done:** space permissions (XML backup reader — needs a real Space/Site XML export to verify; none available), blog posts, attachment versions (latest only), attachments of other pages shown in an image, `include`/`excerpt-include`/`roadmap` and the 40 old-style `ac:macro` elements (kept as unsupported text), user/group *creation* from the source, content-hash/hierarchy validation (page counts only), search reindex after import, S3 storage.

## Phase 7a — Comments, watching, notifications (Phase 7 is split: 7b media/S3 and 7c OpenSearch follow)

### Test evidence (executed 2026-11-07, end of Phase 7a)

```
cd backend && dotnet build Raavi.slnx          → Build succeeded (warnings as errors)
cd backend && dotnet test Raavi.Tests          → Passed: 304, Failed: 0   (new: CommentTests 4, NotificationEmailTests 4; RaaviDocumentTests extended)
cd backend && dotnet ef migrations has-pending-model-changes (Wiki) → none (new migration Comments)
cd frontend && npm run build                   → vue-tsc + vite build succeeded
cd frontend && npm test                        → 9 files, 90 tests passed (new: comments.test.ts 6)
npm audit --omit=dev (frontend)                → 0 vulnerabilities
```

**Acceptance.** *Comments respect view/comment rights*: API tests (reader may read but not write; outsider gets 404 for page and comments; edit needs the author and If-Match; reactions limited to the allow-list). *Notifications never reveal content the recipient cannot view*: after a page is restricted a new mention of a now-excluded user creates no notification or e-mail, and the user's older notifications about it disappear from the inbox. *Delivery*: mention/participant/watcher fan-out (actor excluded, muted watchers excluded, mentions always reach), e-mail only to verified addresses of members who did not opt out, replays do not duplicate, publishing notifies watchers, and the SignalR hub signals inbox changes (WebSocket test against the in-process server) and closes cross-origin connections.

Browser check (2026-11-07, dev: API + Workers + Vite, LocalDB, Persian UI, accounts sara/ali): Ali (API) commented mentioning Sara → Sara's bell badge went to 1 **without reload** (outbox → fan-out → push → refresh), the inbox showed the Persian text, excerpt and Jalali date, clicking opened the page with the thread focused (also when already on that page), the badge cleared, and a Persian e-mail (RFC 2047-encoded subject, `Auto-Submitted`) was written to the pickup folder. Inline comment creation from a text selection (selection bar → quote → composer) was verified up to submit; the dev session had expired by then, so the submit itself was covered only by the unit/API tests, not in the browser.

Defects found and fixed: the wiki-schema v1→v2 migration rejected empty v1 documents (Phase 3 space home pages were unreadable since Phase 4 — `block+`) → empty documents become one empty paragraph (regression test); `CommentReactions` lacked a TenantId-leading index (caught by the isolation model test; migration regenerated before any deployment); long-polling/SSE replace the connection's request scope (no tenant), so the hub is WebSocket-only; the page DTO did not expose `canComment`; a notification link to the page already open only changed the hash (panel now reloads); the compact comment editor inherited the 20 rem page-editor height.

| Feature | Status | Notes |
|---|---|---|
| Page and inline comments, flat threads, edit/delete (soft), resolve/reopen | Tested | ADR-024; bodies are schema-validated Raavi documents |
| Inline anchors as text-quote selectors with ProseMirror decorations; orphaned threads | Tested | deviates from "Yjs relative positions" (ADR-024 §1); no automatic re-anchoring |
| Reactions (allow-list), mentions (members only) | Tested | |
| Watching (auto-watch, explicit mute) | Tested + browser | |
| Notification fan-out with fresh View check, inbox, read/read-all, unread badge | Tested + browser | outbox handler in Workers |
| Push via SignalR `/hubs/events` | Tested + browser | WebSocket-only, same-origin, periodic revalidation; no Redis backplane needed |
| E-mail (MailKit SMTP / pickup / disabled), per-user opt-out, fa/en templates | Tested + pickup file inspected | **no real SMTP relay was tried**; `sandbox/config/notifications.json` |
| Bell, comments panel, selection bar, profile e-mail preference | Tested + browser | |

Limitations: SMTP delivery is untested against a real server; push latency ≈ 2–4 s; one e-mail per notification (no digest); comments are not yet part of search (7c) and not yet covered by retention/export; orphaned inline threads are not re-anchored; `Redis` is not needed but a per-event push (instead of the 2 s comparison) would require it.

## Phase 6 — Real-time collaboration

### Test evidence (executed 2026-11-07, end of Phase 6)

```
cd backend && dotnet build Raavi.slnx          → Build succeeded (warnings as errors)
cd backend && dotnet test Raavi.slnx           → Passed: 296, Failed: 0   (new: CollaborationTests 5)
cd backend && dotnet ef migrations has-pending-model-changes (Wiki) → none (new migration Collaboration)
cd collaboration && npm run typecheck           → no errors
cd collaboration && npm test                    → 9 passed (unit 3: all 36 fixtures JSON→Yjs→JSON, tickets, signatures; integration 6 with real WebSocket clients)
cd collaboration && npm run load -- 100 20      → 100 editors, 2000 concurrent edits: connected 721 ms, converged 3448 ms, all clients identical, persisted state complete
cd frontend && npx vue-tsc --noEmit             → no errors
cd frontend && npm run build                   → succeeded
cd frontend && npm test                        → 8 files, 84 tests passed, exit code 0 (new: collaboration.test.ts 2)
npm audit --omit=dev (frontend, collaboration) → 0 vulnerabilities
```

**Acceptance.** *Concurrent editing without losing acknowledged changes*: integration and load tests check that every client converges to the same document containing every edit, and that the persisted state (snapshot + updates written through the API) reproduces it; a server restart reloads from storage. *Revocation closes sessions*: integration test (server closes the revoked user's connection with `raavi:revoked`, other editors continue, later updates from the revoked client are not applied) and API test of the re-check (session revoked / member removed → `none`, edit restriction → `view`).

The load test runs the real collaboration server, Yjs clients over loopback WebSockets and the real persistence/conversion path against an in-process mock of the internal API; a full-stack load test (API + SQL Server) has not been run.

Browser check (2026-11-07, dev: API + collaboration server + Vite, LocalDB, Persian UI, two tabs): editor joined live mode (document seeded server-side from the published version), the second tab showed the first as a peer, text typed in one tab appeared in the other with a remote caret, the shared draft was persisted through the API within the debounce (readers unchanged), publishing from live mode (`If-Match: *`, API flushes the server first) produced version 4 with the live edits, and discarding the draft while a tab was editing reset that tab to the published content.

Defects found and fixed: endpoint filters run after model binding, so POST signatures were verified against an empty body → verification moved to a middleware before routing; discard while editing re-synced the discarded text because the remounted editor reloaded it from IndexedDB before the cache was cleared → cache cleared before the reset is signalled; the editor toolbar assumed local undo/redo commands; unreachable collaboration-server URLs slowed API tests (Windows connect timeout) → tests run without a server URL; server switched from `y-prosemirror` to `@tiptap/y-tiptap` (the library the editor uses).

| Feature | Status | Notes |
|---|---|---|
| `collaboration/` Hocuspocus 4 server (Node 24, TypeScript via type stripping) | Tested | schema from the shared manifests; no database access |
| ES256 tickets (≤ 60 s, single-use jti, bound to user, session, tenant, document) | Tested | public key via signed internal JWKS; Development uses an ephemeral key |
| Internal persistence API (state, updates, snapshot + compaction, check), HMAC service signatures | Tested | `/internal/collaboration/*` bypasses host-based tenant resolution; tenant from the document address |
| Shared draft kept in step with the live document; publish flushes first | Tested + browser | API validates every stored body against the editor registry |
| Presence and remote carets | Browser | CollaborationCaret + awareness |
| Revocation: periodic re-check + immediate re-check on member suspension/removal, group removal, space permission/restriction changes, archive/trash | Tested | logout and tenant suspension are caught by the periodic re-check (≤ 60 s) |
| Reset on discard/restore (local cache cleared) | Tested + browser | |
| Offline cache (y-indexeddb), cleared on sign-out | Implemented | offline editing and merge not exercised in the browser |
| Undo/redo of own changes (Yjs) | Implemented | not separately tested |
| Load test (100 editors) | Tested | in CI as well |

### Still open after Phase 6
- Multi-node: Hocuspocus Redis extension is installed but not wired/tested; deploy one collaboration node.
- Snapshots are stored in SQL Server (`varbinary`); large snapshots move to object storage with Phase 7.
- Draft activity view (collaboration history) and retention job for compacted state — not implemented.
- A client that pushes content outside the editor registry (bypassing the UI) causes a reset of that live document (its content is refused by the API).

## Phase 5 — Versioning & publishing

### Test evidence (executed 2026-11-07, end of Phase 5)

```
cd backend && dotnet build Raavi.slnx          → Build succeeded (warnings as errors)
cd backend && dotnet test Raavi.slnx           → Passed: 291, Failed: 0   (new: PublishingTests 7)
cd backend && dotnet ef migrations has-pending-model-changes (Wiki) → none (new migrations Publishing, ContributorIndex)
cd frontend && npx vue-tsc --noEmit             → no errors
cd frontend && npm run build                   → succeeded
cd frontend && npm test                        → 7 files, 82 tests passed, exit code 0
```

**Acceptance (published versions remain distinct and recoverable):** every publish, approval and restore adds an immutable version; restore creates a new `Restore` version and leaves older versions readable (tested); drafts never change what readers see (tested); purge is the only destructive operation and is limited to space administrators on trashed content.

Browser check (2026-11-07, dev, LocalDB, Persian UI): migration backfill verified (Publish grants created next to Edit grants, PublishedAt and contributors filled); draft autosave while readers kept the published version; publish dialog with change summary → version 3; history page with word-level diff; approval toggle saved with If-Match; trash list, restore (tree refreshed) and purge of a test page.

Defects found and fixed during Phase 5: background jobs used the 30 s membership cache of the worker process, so a removed member's scheduled publish could still run → jobs acting for a user read membership fresh; a missing TenantId-leading index on PageVersionContributors (caught by the isolation model test); diff lines inherited the direction of a Persian screen-reader label (dir moved to the text); settings tabs loaded their lists before the tab was opened (now loaded on open).

| Feature | Status | Notes |
|---|---|---|
| Shared draft per page, autosave with If-Match on the draft revision | Tested + browser | `PageDrafts`; contributors tracked |
| Private unpublished pages (Status `Draft`, creator only), "My drafts" | Tested | discarding removes the page |
| Publish (immutable version + contributors), separate `Publish` right | Tested + browser | migration grants Publish wherever Edit was granted |
| Preview of the draft | Browser | read-only editor |
| Scheduled publishing (tenant job, re-checks the scheduler's rights at run time, cancellable) | Tested | Hangfire delayed job via `ITenantJobScheduler.Schedule` |
| Approval foundation (space setting, snapshot requests, approve/reject/supersede) | Tested + browser (setting) | full workflows: Phase 10 |
| History, version view, structural + word-level diff (also against the draft) | Tested + browser | `DocumentDiff` (LCS over blocks, then words) |
| Restore as a new version (to the draft in approval spaces) | Tested | |
| Trash: list roots, restore subtree (top level when the parent is gone), purge | Tested + browser | |

### Still open after Phase 5
- Review/expiry dates and reminders (ADR-019) — not implemented.
- Trash retention job (automatic purge after N days) — not implemented; purge is manual.
- Version compression — not implemented; decide with real data (ADR-019 consequence).
- Named draft checkpoints (`VersionType.Draft`) — reserved, not used.
- Live co-editing of the shared draft arrives with Phase 6; until then concurrent editors get a 412 and reload.

## Phase 4 — Editor & extension registry

### Test evidence (executed 2026-11-07, end of Phase 4)

```
cd backend && dotnet build Raavi.slnx          → Build succeeded (warnings as errors)
cd backend && dotnet test Raavi.slnx           → Passed: 284, Failed: 0   (new: 68 — RaaviDocument/registry 84 incl. 36 fixture round trips (replacing 19 Phase 3 cases), EditorApi 3)
cd backend && dotnet list ... --vulnerable      → no vulnerable packages
cd backend && dotnet ef migrations has-pending-model-changes (Wiki) → none (new migration EditorAttachments)
cd frontend && npx vue-tsc --noEmit             → no errors
cd frontend && npm run build                   → succeeded (Mermaid, KaTeX and cytoscape in lazy chunks)
cd frontend && npm test                        → 7 files, 81 tests passed, exit code 0 (new: editor.test.ts 41 incl. 36 fixture round trips)
cd frontend && npm audit --omit=dev            → 0 vulnerabilities (dev-only: js-beautify/glob/minimatch advisories via @vue/test-utils)
```

**Acceptance (round trip per extension):** every manifest has a shared fixture in `schema/editor/fixtures`. The backend validates + canonicalizes each fixture to an identical document and renders it; the frontend loads each fixture into a real Tiptap editor and gets the identical JSON back; an API test saves a document containing all 36 fixtures and reopens it unchanged.

Browser check (2026-11-07, dev, LocalDB, Persian UI): opened the Phase 3 page in the editor (v1 body upgraded on read), inserted a warning panel via the Persian slash search (`/کادر`), a KaTeX formula block and a Mermaid diagram (rendered in a sandboxed iframe), saved (version 2) and checked the read view and `GET /content/{id}/html` (all text escaped). Image/file upload was verified by API tests only (the browser automation cannot drive the file chooser).

Defects found and fixed: vue-i18n parses `@` as linked-message syntax (editor placeholder escaped); the global security-header middleware overwrote endpoint CSPs (endpoints may now set a stricter policy); ETag construction on downloads; Mermaid 12 pulls in `elkjs` (EPL-2.0/GPL-3.0) → pinned Mermaid 11.17.2 with KaTeX overridden to 0.19.1 (fixes GHSA-238p-pmpm-9mq7 in Mermaid's nested KaTeX).

| Feature | Status | Notes |
|---|---|---|
| Shared extension manifests (`schema/editor/extensions`, 36) + registry info (schemaVersion 2) | Tested | embedded in `Raavi.Wiki`, imported by the SPA via `@schema` |
| Server registry: content expressions, strict attributes, defaults, marks, link allow-list, size/depth limits | Tested | `EditorSchema`; authoritative on every save |
| Lazy schema migration (v1 → v2) | Tested | versions stay immutable; upgraded on read, persisted on next save |
| Whitelist HTML renderer + HtmlSanitizer | Tested | `GET /content/{id}/html`; mention names resolved per viewer; TOC anchors |
| Plain-text extraction per extension | Tested | used by the search provider (Phase 7) |
| Frontend registry (duplicate/missing checks, server version check) | Tested | editing refused when the registry differs from `/editor/schema` |
| Core formatting: headings, lists, task lists, quote, code block, rule, links, colour, highlight, alignment (logical), per-paragraph `dir` | Tested | Tiptap 3.33 open-source extensions |
| Raavi nodes: table, panel, expand, status, TOC, Mermaid, inline/block math, mention, image, attachment, unsupported | Tested (round trip) + browser check (panel, math, Mermaid) | Mermaid `securityLevel: 'sandbox'`; KaTeX `trust: false` |
| Slash commands (fa/en keywords), @mentions (tenant directory), toolbar, table tools, link dialog | Tested (component) + browser check | |
| Attachments-lite (SQL storage, magic-byte sniffing, View-checked download, size limit) | Tested | `content.Attachments`; moves to object storage in Phase 7 |

### Still open after Phase 4
- `raavi.include`, `raavi.excerpt`, `raavi.smartLink`, `raavi.jira`, `raavi.chart`, emoji and Monaco code editing are not implemented (see FEATURE_MATRIX).
- No HTML render cache table yet (rendering is on demand); no background re-migration job (versions are immutable; lazy migration is sufficient until a destructive schema change).
- Production CSP for the SPA must allow `frame-src data:` for Mermaid's sandboxed iframes (DEPLOYMENT_GUIDE).
- Editor bundle ≈ 0.5 MB (Tiptap + ProseMirror); split further if page-view performance requires.

## Phase 3 — Spaces & wiki core

### Test evidence (executed 2026-11-07, end of Phase 3)

```
cd backend && dotnet build Raavi.slnx          → Build succeeded (warnings as errors)
cd backend && dotnet test Raavi.slnx           → Passed: 216, Failed: 0   (new: 39 — RaaviDocument 19, SpacePage 8, Permission 7, WikiIsolation 4, DependencyRules +1 for Raavi.Wiki)
cd backend && dotnet list ... --vulnerable      → no vulnerable packages
cd backend && dotnet ef migrations has-pending-model-changes (Wiki) → none
cd frontend && npm run build                   → vue-tsc + vite build succeeded
cd frontend && npm test                        → 6 files, 40 tests passed, exit code 0 (new: wiki.test.ts 8)
cd frontend && npm audit --omit=dev            → 0 vulnerabilities
```

TenantIsolationModelTests now covers three modules (20+ tenant-owned entities, each with query filter, TenantId-leading index and an enabled RLS predicate set). HostCompositionTests validates the worker graph with Identity core + Wiki + the outbox poller.

Browser check (2026-11-07, dev over http, LocalDB): signed in as the dev tenant admin, created a space (key suggested from the name), created a Persian page (rendered RTL per paragraph; `<script>` text shown literally), added a label, quick find (Ctrl+K) in the Persian UI with Jalali dates, space settings permission matrix. The activity feed stayed empty in that run because the Workers host (outbox poller) was not started — activity projection itself is covered by tests. Found and fixed: link-styled buttons were underlined (global style for `a.p-button`). Vite's first-run dependency optimisation reloaded the page twice (dev-only).

| Feature | Status | Notes |
|---|---|---|
| Spaces (types, unique per-tenant keys, home page, archive/restore, settings) | Tested | New module `Raavi.Wiki` (ADR-023) |
| Space creation rights (tenant-scope grants; admins only by default) | Tested | `GET/PUT /tenant/permissions`, admin page "Global permissions" |
| Content node + closure table, content provider registry (page, folder) | Tested | unknown types rejected |
| Page CRUD with versions (each save = `PageVersion`, no-op saves skipped) | Tested | Phase 3 body = Raavi Document schema v1 (paragraphs, text, hard breaks), validated and canonicalized server-side |
| Tree navigation, breadcrumbs, move (incl. cross-space subtree), reorder, cycle prevention | Tested | set-based closure re-linking inside a transaction |
| Archive / unarchive / trash (subtree) | Tested | trash restore & purge: Phase 5 |
| ETag / If-Match (spaces, content, templates) | Tested | 428 without If-Match, 412 on stale revision; sibling renumbering does not change revisions |
| PermissionEvaluator v1 | Tested | space grants allow/deny (deny wins), inherited View restrictions, node-only Edit restrictions, groups take effect on the next request, tenant-admin recovery without View bypass, "not found" for anything invisible |
| Labels (normalized), label pages, permission-filtered suggestions | Tested | |
| Templates (space + global) | Tested | |
| Quick find by title (spaces + pages, permission-filtered) | Tested | full-text search: Phase 7 |
| Transactional outbox + dispatcher + activity projection | Tested | global `content.Outbox`, claim with UPDLOCK/READPAST, per-tenant scope, deferral for inactive tenants, back-off retries |
| Principal directory (`IPrincipalDirectory`, `GET /directory`) | Tested | tenant-scoped users and groups only |
| App shell: spaces nav, space sidebar with lazy page tree, breadcrumbs, command palette, page view/edit, move/restriction dialogs, settings | Tested (component) + browser check | page body editing is a plain-text placeholder until the Phase 4 editor |

### Still open after Phase 3
- Rich editor (Phase 4); version diff/restore, drafts and trash restore (Phase 5).
- Content-scope grants, shared links, anonymous access, nested groups and cached ACL version stamps (ADR-012) are not implemented yet.
- Blog posts and shortcuts (feature matrix) were not part of this phase's delivered scope; only `page` and `folder` providers exist.
- Activity requires the Workers host (outbox poller) to be running in deployments.

## Phase 2b — External identity providers

### Test evidence (executed 2026-11-06, end of Phase 2b)

```
cd backend && dotnet build Raavi.slnx          → Build succeeded (warnings as errors)
cd backend && dotnet test Raavi.slnx           → Passed: 177, Failed: 0   (new: ExternalLogin 11, ProviderConfiguration 22)
cd backend && dotnet list ... --vulnerable      → no vulnerable packages
cd backend && dotnet ef migrations has-pending-model-changes (Identity) → none
cd frontend && npm run build                   → succeeded
cd frontend && npm test                        → 5 files, 32 tests passed, exit code 0
cd frontend && npm audit --omit=dev            → 0 vulnerabilities
```

External systems in tests are in-process fakes: an OpenID Provider (discovery, JWKS, RS256 id_tokens), a Jira Data Center (OAuth 2.0 token endpoint, `/myself`, `/user?expand=groups`, `/serverInfo`) — both enforcing client credentials, redirect URI and PKCE — and an LDAP directory. **No real IdP, Jira or Active Directory was available**; real-instance verification remains open (Q1).

Browser check (dev, http): migrator backfilled the membership index on the existing dev database with RLS re-enabled afterwards; "Sign-in methods" admin page, "My account" page and guest-only redirect of `/login` for a signed-in user verified. Redirect providers were not exercised in the browser (none configured in dev).

Defects found and fixed during Phase 2b verification: tenant provider policy `PUT` left omitted providers enabled (now a full replacement); Vitest exited 1 on an unhandled `matchMedia` rejection from PrimeVue Select under jsdom (polyfilled in `tests/setup.ts`); test environment inherited the dev public port.

| Feature | Status | Notes |
|---|---|---|
| Redirect flow (state, PKCE S256, nonce, browser binding, single-use, tenant re-check) | Tested | `ExternalLoginFlow`, `PendingLoginStore`; forged/misbound/replayed callbacks rejected |
| System-host relay with one-time hand-off | Tested | Jira default; hand-off single-use, browser-bound |
| OIDC provider | Tested (fake IdP) | wrong nonce / foreign signing key / wrong PKCE verifier rejected |
| Jira Data Center OAuth 2.0 provider + connection test | Tested (fake Jira) | subject = key; e-mail untrusted; version gate 8.22 |
| LDAP / Active Directory provider | Tested (fake directory) | filter escaping, empty-password guard; real LDAP connection untested |
| Identity mapping, provisioning modes, e-mail collision refusal | Tested | |
| Domain and group gates, external group mirroring + sync | Tested | |
| Account linking / unlinking | Tested | identity-in-use and last-method protections |
| Workspace switching (membership index + backfill) | Tested | |
| Provider policy admin UI, profile UI, provider buttons, workspace switcher | Tested (component) + browser check | |

### Still open after Phase 2b
- Verify against the customer's Jira (Q1) and Active Directory; adjust ADR-007 with the findings.
- Pending-login and hand-off storage uses the distributed cache: with several API nodes a Redis-protocol L2 (`ConnectionStrings:Redis`) is required (in-memory works only for single-node).

## Phase 2a — Identity core

### Test evidence (executed 2026-11-06, end of Phase 2a)

```
cd backend && dotnet build Raavi.slnx          → Build succeeded (warnings as errors)
cd backend && dotnet test Raavi.slnx           → Passed: 144, Failed: 0   (SQL tests on LocalDB 2016, compat 130)
cd backend && dotnet list ... --vulnerable      → no vulnerable packages
cd backend && dotnet ef migrations has-pending-model-changes (Tenancy, Identity) → none
cd frontend && npm run build                   → vue-tsc + vite build succeeded
cd frontend && npm test                        → 4 files, 27 tests passed
cd frontend && npm audit --omit=dev            → 0 vulnerabilities; licence scan 81 prod packages, all permissive
```

New backend test classes: TenantAuthentication 11 · SystemAdministrator 4 · IdentityConfiguration 15 · DatabaseRoles 1 (TenantIsolationModel now covers both modules: 10 tenant-owned entities).

Browser smoke test (2026-11-06, dev mode over http): tenant admin accepted an invitation on `acme.raavi.localhost` (token stripped from the URL, signed in, members page, session/antiforgery cookies not readable by JavaScript); system administrator enrolled TOTP via a locally rendered QR code on `admin.raavi.localhost`, signed out, signed in again with password + fresh TOTP code, saw the workspace list (Persian, Jalali dates).

Defects found and fixed during Phase 2a verification: identity RLS registration missing (generated migration used CRLF and a scripted edit silently missed — caught by `TenantIsolationModelTests`); double authentication challenge crashing multi-scheme policies; antiforgery 500 on plain-http dev servers (→ dev-only insecure mode with production guard); password-strength overlay covering the submit button; OTP boxes without accessible names; new accounts not recording their first sign-in.

| Feature | Status | Backend | Frontend | DB | Tenant isolation | Tests | Known issues / deps |
|---|---|---|---|---|---|---|---|
| Global users, e-mail validation, password policy + PBKDF2 hashing | Tested | `Raavi.Identity/Domain/User.cs`, `PasswordService` | — | `[identity].Users` | global, never listed cross-tenant | IdentityConfiguration | — |
| Local sign-in, lockout, timing equalization, generic errors | Tested | `LocalAuthenticationProvider`, `AuthService` | LoginView | — | membership required | TenantAuthentication | — |
| Server-side sessions (host-only cookie, idle/absolute, stamp, revocation, fixation) | Tested | `SessionService`, `RaaviSessionAuthenticationHandler` | auth store | AuthenticationSessions | session bound to one tenant/system host | TenantAuthentication, IdentityConfiguration | cache convergence ≤ 15 s across nodes |
| Antiforgery (incl. login CSRF) | Tested | `AntiforgeryProtection` | http client auto-token | — | — | TenantAuthentication, SystemAdministrator, frontend http tests | — |
| Memberships, TenantAdministrator role, last-admin protection | Tested | `TenantIdentityAdminService`, `MembershipService` | MembersView | TenantMembers, TenantRoleAssignments (RLS) | yes | TenantAuthentication | — |
| Invitations (hashed token, fragment link, single use, new/existing account) | Tested | `AuthService`, `TenantIdentityAdminService` | AcceptInvitationView, MembersView | TenantInvitations (RLS) | yes | TenantAuthentication | Admin-visible link until e-mail delivery (Phase 7) |
| Groups | Tested | same | GroupsView | TenantGroups, TenantGroupMembers (RLS + composite FK) | yes | TenantAuthentication | external group sync → 2b |
| Tenant provider policies | Tested | `TenantProviderPolicyService` | — (API only) | TenantAuthenticationProviders (RLS) | yes | TenantAuthentication | admin UI → 2b |
| Provider registry (fail-fast) | Tested | `AuthenticationProviderRegistry` | — | — | — | IdentityConfiguration | only Local type in this build |
| System administrators: invitation, TOTP enrollment, MFA sign-in, replay protection | Tested | `SystemIdentityService`, `TotpService` | SystemSetupView, SystemLoginView | SystemInvitations, SystemRoleAssignments | system host only | SystemAdministrator (incl. RFC 6238 vector) | — |
| System workspace admin UI | Implemented | existing tenancy API | SystemTenantsView, DiscoveryView | — | — | browser smoke test | no component tests yet |
| Security events (tenant + system) | Tested | `SecurityEventWriter` | — (API only) | TenantSecurityEvents (RLS), SystemSecurityEvents | yes | TenantAuthentication (no secrets in details) | — |
| Router guards (host kind, roles, safe redirects), fragment-token handling | Tested | — | `router/index.ts` | — | — | frontend identity tests | — |
| SQL runtime role / login scripts | Tested (roles) / Implemented (logins) | — | — | `deployment/scripts/sql` | runtime role cannot alter RLS | DatabaseRoles | `02-logins.sql` not executed (LocalDB, no SQL logins) |
| Dev-only insecure-http cookie mode with production guard | Tested | `RaaviAuthenticationOptions` | — | — | — | IdentityConfiguration | — |

### Deferred to Phase 2b
OIDC, LDAP/AD, Jira (spike + provider), external identity linking, provisioning for external users, group sync, system-host callback relay, tenant switching (membership index), provider policy admin UI.

## Phase 1 test evidence (executed 2026-11-06)

```
cd backend && dotnet build Raavi.slnx          → Build succeeded (warnings as errors)
cd backend && dotnet test Raavi.slnx           → Passed: 113, Failed: 0   (SQL tests on LocalDB = SQL Server 2016 13.0)
cd backend && dotnet list ... --vulnerable      → no vulnerable packages
cd backend && dotnet ef migrations has-pending-model-changes → none
cd frontend && npm run build                   → vue-tsc + vite build succeeded
cd frontend && npm test                        → 3 files, 16 tests passed
cd frontend && npm audit --omit=dev            → 0 vulnerabilities
```

Manual smoke tests (2026-11-06): migrator applied twice (idempotent) to a fresh LocalDB database; API started and served `/health/ready` 200, system-host context, 401 for system API without key, tenant creation with key, tenant-host context, 404 for unknown tenant; Workers started Hangfire server and registered `system:domain-verification`; SPA verified in a browser on `acme.raavi.localhost` (RTL Persian, English LTR switch, tenant brand color, welcome message, Jalali date) and `ghost.raavi.localhost` (workspace-not-found page).

Backend test classes: DependencyRules 9 · SandboxConfiguration 6 · TenantHost 33 · PersianTextNormalizer 12 · HttpFoundation 5 · RowLevelSecurity 7 · TenantIsolationModel 1 · TenantScopedServices 5 · TenancyApi 9 · TenantResolver 12 · TenantDomainModel 13 · HostComposition 1.

**Not executed:** CI workflow (no remote), Docker Compose (Docker not installed), SQL Server 2022 run of the DB tests (CI service container configured; local runs used 2016 LocalDB with compatibility level 130).

## Phase 1 — System foundation & multi-tenancy

| Feature | Status | Backend | Frontend | DB | API | AuthZ | Tenant isolation | Tests | Known issues / deps |
|---|---|---|---|---|---|---|---|---|---|
| Tenant model (code, slug, names, status, lifecycle) | Tested | `Raavi.Tenancy/Domain/Tenant.cs` | — | `system.Tenants` | `/system/tenants*` | system API key | system-level | TenantDomainModel, TenancyApi | — |
| Tenant provisioning (idempotent, tenant-scoped seeding) | Tested | `TenantAdministrationService` | — | Settings + Branding | POST create / provision | system | writes via explicit tenant scope | TenancyApi, RLS tests | — |
| Suspend / activate / deactivate | Tested | same | — | — | POST suspend/activate/deactivate | system | cache invalidated on change | TenancyApi | Cross-node propagation ≤ L1 TTL (15 s) — not load-tested |
| Subdomain resolution | Tested | `TenantResolver`, `TenantResolutionMiddleware` | host bootstrap | — | all | — | host-only | TenantResolver, TenancyApi | — |
| Custom domains + DNS TXT verification + quarantine | Tested | `TenantDomainService`, `DomainVerificationJob` | — | `system.TenantDomains` | `/domains*` | system | unique live host | TenancyApi (fake DNS), model tests | Real DNS lookups not exercised in tests (DnsClient) |
| Tenant hint header conflict rejection | Tested | middleware | — | — | all | — | yes | TenancyApi | — |
| Login discovery by tenant code | Tested | endpoint | — | — | `/discovery/tenants/{code}` | anonymous, rate-limited | system host only | TenancyApi | — |
| Immutable tenant context / tenant scopes | Tested | `TenantContextHolder`, `TenantScopeFactory` | — | — | — | — | yes | TenantScopedServices | — |
| EF tenant filter + TenantId stamping/immutability | Tested | `RaaviDbContextBase`, `TenantStampingInterceptor` | — | — | — | — | yes | RowLevelSecurity, model test | — |
| SQL Server RLS + read-only SESSION_CONTEXT (pool-safe) | Tested | interceptor + migration helpers | — | `security.TenantIsolation` | — | — | yes | RowLevelSecurity (7) | Separate DB logins (app/migrator) not yet scripted → Phase 2 installer |
| Tenant-aware cache (HybridCache + optional Redis L2) | Tested | `TenantCache`, `CacheKeys` | — | — | — | — | `t:{id}:` prefix | TenantScopedServices | Redis L2 path not exercised (no Redis locally) |
| Tenant-aware background jobs | Tested | `TenantJobRunner`, `HangfireTenantJobScheduler` | — | `hangfire` schema | — | — | envelope + scope | TenantScopedServices, HostComposition | No production tenant job exists yet |
| Tenant settings & branding (DB-stored, validated) | Tested | `TenantConfigurationService` | branding applied (palette), tenant locale default | Settings, Branding (RLS) | `/settings`, `/branding`, `/host/context` | system | RLS | TenancyApi, frontend tests | Logo/favicon need attachments → Phase 7 |
| System + tenant audit (same transaction) | Implemented | `TenancyAudit` | — | `system.SystemAuditLogs`, `audit.TenantAuditLogs` (RLS) | — | — | yes | indirectly (RLS tests insert audit rows) | No audit query API yet |
| System administrator access (API keys, hashed) | Tested | `Raavi.Identity/SystemAccess` | — | — | Bearer on system host | policy `raavi.system-admin` | ignored on tenant hosts | TenancyApi | Bootstrap only; Phase 2 adds MFA sign-in |
| Host-kind enforcement before auth (404 on wrong host) | Tested | `UseRaaviHostKindEnforcement` | — | — | — | — | — | TenancyApi | — |
| Global config loader / tenant config in DB | Tested | Phase 0 loader + DB settings | — | — | — | — | — | Sandbox + API tests | — |
| Migrator (EF migrations + Hangfire schema) | Implemented | `Raavi.Migrator` | — | — | — | — | — | manual smoke (idempotent) | — |
| OpenTelemetry traces/metrics (OTLP optional) | Implemented | `ObservabilityExtensions` | — | — | — | — | tenant id tag | — | Exporter not tested against a collector |
| Readiness checks SQL | Tested | `TenancyDatabaseHealthCheck` | — | — | `/health/ready` | — | — | HttpFoundation | Redis/OpenSearch/S3 checks added with those modules |
| SPA tenant bootstrap, not-found page, system badge | Tested | — | `stores/host`, `App.vue`, `WorkspaceNotFound` | — | uses `/host/context` | — | never sends tenant ids | Vitest (8 new) + browser check | — |
| CI with SQL Server 2022 service | Implemented | — | — | — | — | — | — | not executed | Q5 |

### Deferred from Phase 1 (documented in the roadmap)

| Item | Moved to | Reason |
|---|---|---|
| System administration UI screens | Phase 2 | Needs administrator sign-in (API keys must not live in a browser) |
| Transactional outbox | Phase 3 | First producer/consumer (content events → search/notifications) arrives there |
| Storage quota enforcement, logo/favicon | Phase 7 | Needs attachments/object storage |
| Tenant export, retention-aware deletion/purge | Phase 12 | Needs all tenant-owned modules to exist to export/purge in dependency order |
| `ETag`/`If-Match` on tenant aggregates | Phase 3 | RowVersion columns exist; conditional requests added with the shared API conventions work |

## Phase 0 — Repository Assessment and Architecture Foundation

Complete; see git history / previous status. All Phase 0 rows remain Tested/Implemented; the PrimeVue licensing entry was corrected in Phase 1 (`DEPENDENCY_AND_LICENSING.md`).

## Later phases

Phases 2–12: **Planned** (`docs/product/IMPLEMENTATION_ROADMAP.md`).
