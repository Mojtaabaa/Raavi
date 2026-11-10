# Implementation Status

**Last updated:** 2026-11-10 (login LTR password eye, page link picker, path rewrites, resolved quote jump, RTL lists)

Statuses: Not Started · Planned · In Progress · Implemented · Tested · Blocked · Deferred. "Tested" requires executed test evidence (listed).

### Editor / login polish (2026-11-10)

LTR login: password reveal icon moves to the inline-start so typed text does not cover it. Editor link dialog: pick a page via space + page tree (cross-space) or type a URL; stored as `/spaces/{key}/pages/{id}`. Configurable `Raavi:Links:PathRewrites` (sandbox + defaults) rewrites path-prefix hrefs such as `vianna/UI/...` to an absolute base (default `https://svn.viannacloud.ir`); allow-list, HTML render and SPA click use the same rules (`GET /editor/link-rewrites`). Resolved inline comments keep quote anchors when "show resolved" is on so quote clicks still jump to the passage. Persian empty search: «موردی یافت نشد». RTL bullet lists: list-item paragraphs inherit direction and CSS flips markers via `:has(> p[dir=rtl|ltr])`.

## Phase 12 — Enterprise readiness (split: 12a workspace export and 12b backup/restore scripts and 12c performance baseline done; deletion/purge, scheduling, HA, large-scale/k6 load tests, hardening pending)

### 13c — Browser walk-through of the running app (2026-11-08)

Dev API + workers + Vite started; signed in as the dev workspace admin in the built-in browser. **Covered:** 15 global/admin routes (home, spaces, tasks, calendar, approvals, profile, members, groups, sign-in methods, AI, connectors, webhooks, export, permissions, Confluence migration) and 9 routes of a space (home, workflows, whiteboards, slides, databases, calendar, tasks, settings, create) loaded with correct headings and **no console errors**; a whiteboard was created, a rectangle drawn and saved ("saved" toast); the page editor opened an existing page with panel/math/Mermaid content, the new **/issue** command was offered, inserted the node and showed the "no tracker connected" state; the test draft was discarded afterwards.

**Defect found and fixed:** the top navigation listed nine admin links next to the five main ones, overflowed an 800 px-wide window (horizontal scroll, wrapped labels). The admin links are now one "Administration" menu (aria-expanded, closes on Escape / navigation / outside click; test added). **Correction:** the "vue-tsc clean" statements for 13a/13b were not backed by a completed run (the compiler ran out of memory silently); a real run with a larger heap found a type error in `tests/issue-link.test.ts` (fixed). Now: `NODE_OPTIONS=--max-old-space-size=4096 npx vue-tsc --noEmit` clean, vitest 25 files / 161 tests. `whiteboards.test.ts` failed once in a loaded parallel run and passed alone and on re-run (timing, not reproduced).

**Not covered:** creating/using slides, databases, workflows, approvals, tasks, calendar events; live co-editing between two browsers; AI, webhooks and connectors with real hosts (dev config allows none); PNG/SVG/PPTX/PDF downloads; mobile widths, dark mode, keyboard-only use, screen readers; English UI. Test data left in the dev DB: whiteboard "Browser check board" in space ENGINEERIN.

### 21 — Comment quotes, e-mail settings, time zone and calendar (ADR-053)

Quote cut + "show more" + jump to the passage; system "E-mail settings" page (SMTP / pickup, protected password, test message, applied to API and Workers live); Administration → Display (time zone, calendar, default language) applied to every displayed time; calendar-aware date and date-time fields. Tests: 2 backend, 5 frontend. **Not verified:** a real SMTP server, the Persian picker on real devices, time display on every single screen (formatters were unified in the screens that used ad-hoc ones).

### 20 — Prompt Hub (ADR-052)

Library (personal/public, categories tree with drag & drop, search/filter/sort), editor on the existing Tiptap editor with variable chips and insertion menu, typed input variables, draft/published versions with diff and restore, context providers (page, selection, section, pages, space, Jira issue, generic), preview / manual copy / automatic execution through the shared `AiService.CompleteAsync`, ratings and feedback, usage events and statistics, fork, administrator analytics, "AI prompts" button on pages (save as draft page / add to page draft with confirmation). Tests: 6 + 8 + 3 backend (PromptHubTests, PromptRunTests, PromptTemplateTests), 11 frontend. **Not done / not verified:** Templar and Jira-project context (unavailable), user/group sharing, per-permission category rights, retention job, real AI provider and a real Jira (fakes only), visual RTL check, load/perf measurement of the analytics queries.

### 19 — Accounts report, space export/import, worker status (ADR-051)

System "Accounts" page (workspace count per account, filter, delete selected / all without a workspace — only accounts with no membership and no system role), space export (settings) and import (spaces list; pages, tree, labels, attachments, comments, links rewritten), worker indicator in the header (Hangfire heartbeat, administrators only). Tests: 3 backend (accounts, space round trip with link/attachment rewrite, worker status), 5 frontend. **Not verified:** the heartbeat against a really installed `RaaviWorkers` service (tests have no worker: Running was not observed), large archives through IIS, import of tasks/databases/slides/whiteboards/permissions/history (not implemented).

### 18 — Workspace deletion and the "More" menu (ADR-050)

Delete request (code typed, access stops, 14-day grace by `Raavi:Tenants:DeletionGraceDays`), cancel, purge now / daily `TenantPurgeJob` (generic `TenantId` scan inside the tenant scope, audit kept, tombstone keeps code and slug), system UI with dialogs; header: calendar and approvals moved behind a "More" dropdown (`NavDropdown`, also used by Administration). Tests: 3 backend (request/cancel, purge isolation, daily job), 3 frontend. **Not verified:** purge of a large real tenant (timing, log growth) and of data from every module (only a wiki space was seeded); four-eyes approval and orphaned-user cleanup are not built.

### 17 — Persian search folding and application-shell improvements

Folding (ADR-049 addendum), 213 frontend tests, backend suite re-run; UI: Menu button for narrow screens, "page does not exist" page (was a silent redirect), shortcut list on ?, / opens smart search, quick-find offers a text search, back-to-top button. **Not verified:** folding on real Persian wikis (only constructed examples), phone layout on a real device.

### 16 — Smart search and "ask the wiki" (ADR-049, Phase 11b)

```
dotnet test Raavi.Tests → 515 passed (7 new: chunker, vectors, scoring/snippets, keyword search without AI, permission leakage incl. prompt and no-call-when-nothing-readable, hybrid with embeddings + failing provider + model change); vitest + smart-search page tests
live (dev stack, Workers + API): "Index now" → 150 pages / 557 passages indexed in one run from the imported Confluence tenant (785 pages pending, finished by following runs); RAG job registered in Hangfire
```

Index in SQL Server (`ai.Chunks`, `ai.IndexedPages`, migration `SmartSearchIndex`, RLS), hybrid retrieval (keywords + cosine, reciprocal rank fusion), query-time permission filtering, `AiIndexJob` every 15 minutes, `/ai/search`, `/ai/ask`, index administration, embedding model setting, "Smart search" page. **Not verified:** any real provider (OpenAI-compatible embeddings/chat were exercised against the in-process fake only); search quality on real Persian text (no normalisation of Arabic/Persian letter variants yet); brute-force vector search beyond tens of thousands of passages. **Not done:** ANN store, reranking, indexing of comments/attachments/database rows.

### 15 — Operations: status page, logs, activity chart, compression, navigation (ADR-048)

```
dotnet test Raavi.Tests → 508 passed (13 new: request metrics, status endpoint content/privacy/switch, gzip bodies in a varbinary column, client error reporting + search/group/purge/authorization, activity log search by every field, activity stats, security-event search, nightly maintenance); vitest 30 files / 205 tests (11 new); vue-tsc clean
browser (dev stack): /status healthy-with-problems view, then API killed → "cannot reach the server" with the gateway cause; error log row opened with its stack; activity chart on the home page; breadcrumbs on 9 routes; Material theme applied; crop dialog (found and fixed an infinite render loop of the first version)
dev database: content.PageVersions 114.7 MB -> 10.4 MB after the CompressPageVersions migration
```

Status page + `/health/status`, connection banner, error log (`system.ErrorLog`, browser reporter), activity log/chart, security-event search, compressed page bodies, nightly maintenance (`Raavi:Maintenance`), API response compression, breadcrumbs, Material themes, new crop dialog, whiteboard templates/copy-paste/layers/Tab. **Not verified:** a real IIS installation (proxy classification of 502/504/non-API answers was exercised only against the Vite dev proxy); the compression migration on a large database (only 5 000 rows; it switches the RLS policy off inside its transaction); response compression through IIS/ARR; Workers on a real server for the nightly job (tested against the test database only). **Not done:** alerting, metrics shared between API processes, logging of row-level changes of databases/slides/boards.

### 14 — Experience polish (ADR-047)

```
dotnet test Raavi.Tests → 495 passed (8 new: two-step setting, administrator password/picture, shared accounts, theme, workspace logo, database filters/totals, board shapes ×2); vitest 29 files / 194 tests (11 new); vue-tsc clean
browser (dev stack, Persian UI): themes chocolate/ocean applied and the choice read back from /auth/me; member password dialog shows the guided error for a short password; system sign-in without a code (setting off); address column + logo upload (shown top right, then removed); mobile emulation 360 px: no horizontal page overflow on 18 routes after the fix; board with diamond/triangle/line/sticky + grid + inspector; database board view, card move, filter chip, Σ row; page tree connector rules present (computed styles)
```

Fixed: password/icon fields (text no longer under the reveal icon, both text directions); user guide linked from sign-in pages and the header; "Address" column (clickable) in the workspace list; administrators set a member's password and picture (only for accounts that belong to no other workspace); square workspace logo with a crop dialog (system administrators; shown in header, sign-in and invitation pages); `SystemMfaRequired` setting; horizontal space menu; page tree with connector lines; sticky editor toolbar (it was hidden under the title bar and the wrapped header); error guidance (steps + guide link); nine colour themes (tokens, PrimeVue palettes, icon colour) saved in `Users.Theme`; responsive fixes (header wrap on phones, grid pages `minmax(0, 1fr)`, tables scroll in their own box); whiteboard: diamond/triangle/line, grid + snap, numeric geometry, key shortcuts, nudging; databases: column filters, number totals, column visibility, board view, duplicate row, CSV import, column reordering; guide updated. Migrations `UserTheme` (identity) and `TenantLogo` (tenancy, RLS) applied to RaaviDev. **Not verified:** a real IIS installation; the logo/theme/admin-password paths with real users; Safari/Firefox; a screen reader on the crop dialog; the page tree lines in a deep (5+ levels) tree and in LTR.

### 13h — Brand assets and profile pictures (ADR-046)

```
dotnet test Raavi.Tests → 487 passed (6 new avatar tests); vitest 28 files / 183 tests (5 new); vue-tsc clean
browser (dev stack): logo + favicon links load; a 640×400 picture uploaded through the profile page became 256×256 WebP (3.4 KB), shown in the header, profile, members list, page header and comments; removed again
```

Brand package in `docs/branding` and `frontend/public` (favicons, manifest, logos), `BrandLogo` on the header and sign-in pages, guide header/favicon. Profile pictures: table `identity.UserAvatars` (migration applied to RaaviDev), content-sniffed PNG/JPEG/WebP only, ≤ 512 KB and ≤ 1024 px, served only to active members of the same workspace, versioned immutable URLs, batched lookups, `UserAvatar` component. **Not done:** pictures where the API gives only a name (activity feed, history, creators of databases/slides/boards/events, mentions), server-side re-encoding, tenant export of pictures, real IIS verification.

### 13g — Path addressing and a default workspace (ADR-045)

```
dotnet test Raavi.Tests → 481 passed (22 new: prefix parsing, option validation, real-pipeline requests); vitest 27 files / 178 tests (14 new); vue-tsc clean
browser (dev stack, Addressing=Both then DefaultTenantCode=ACME): /t/acme/ login + 11 pages without errors; /system/login with /system/api calls; root = default workspace
```

`Raavi:Hosting:{Addressing,PathPrefix,DefaultTenantCode}`; workspaces at `system-host/t/<slug>/`, default workspace at the root, administration at `/system`. **Not verified:** a real IIS installation, OIDC/Jira sign-in and real-time editing under a path prefix; `Location` headers carry no prefix. Risk: one browser origin for all workspaces (ADR-045).

### 13f — Bootstrap system administrator (ADR-044)

```
dotnet test Raavi.Tests → 459 passed (3 new bootstrap tests); vitest 26 files / 164 tests (3 new); vue-tsc clean (NODE_OPTIONS=--max-old-space-size=4096)
```

Settings `Raavi:Authentication:Bootstrap:SystemAdministrator` (empty by default) create the first system administrator at API start when none exists; its temporary password opens only `/auth/system/first-login` → setup page (new password per policy + TOTP). Migration `BootstrapAdministrator` adds `Users.MustChangePassword`. **Not verified:** on a real server; the migration was not applied to RaaviDev yet. Also fixed: a flaky wait in `whiteboards.test.ts`.

### 13e — IIS hosting of the API, Persian install guide (ADR-041 addendum)

`deployment/iis/web.config` (shipped in `web/`), `Update-Raavi.ps1 -AppPool/-Service`, `INSTALL_WINDOWS_IIS.md` rewritten (API = IIS site, workers = NSSM) and `INSTALL_WINDOWS_IIS_fa.md` (Persian). `Test-ReleaseScripts.ps1 -WithFrontend` → 26 checks passed. **Not verified:** a real IIS/ARR/URL Rewrite/NSSM installation, the per-site ARR variant, WebSocket (`/hubs`) through ARR.

### 13d — Browser walk-through, second round (2026-11-08)

Same dev stack and browser as 13c; only the two console errors of the signed-out start-up (`/auth/me` 401) appeared during the whole session. **Verified end to end in the real UI:** slide deck (create, add slide, presentation mode); database (create, add rows, edit a cell, value persisted after reload); workflow (create → publish a new page → the run log shows "label 'newpage' added" and the page carries the label); page (create draft, task list, publish with the publish dialog); workspace task (create); calendar event (create, shown on the Persian-calendar grid with accessible day buttons); whiteboard and editor from 13c. No application defect found in this round.

**Observed, not defects:** inline task lists in pages are not linked to the tasks store (documented in 9a); "My tasks" lists only tasks assigned to the user. A doubled line in my test page came from my own repeated typing, confirmed in the saved document.

**Still not covered:** approvals (needs a second user), live co-editing between two browsers, AI/webhook/connector calls with real hosts, PPTX/PDF/PNG/SVG/CSV downloads (downloads need explicit permission), mobile widths, dark mode, keyboard-only and screen-reader use, English UI. **Test data left in the dev DB (space ENGINEERIN):** whiteboard "Browser check board", deck "Browser check deck", database "Browser check db", workflow "Browser check rule", page "Browser check page", task "Browser check task", event "Browser check event".

### 13a — Issue tracker connectors: GitHub, GitLab, Jira (ADR-043)

```
dotnet test Raavi.Tests → 455 passed (23 new: reference parsing incl. hostile input, host policy, admin CRUD/token handling, lookups for the three trackers, error mapping, cache, disabled connector, tenant isolation)
npx vitest run (frontend) → 24 files / 154 tests passed (4 new); vue-tsc clean
```

Per-tenant read-only connectors (title, status, link) with a write-only encrypted token, installation allow-list `AllowedConnectorHosts`, SSRF-guarded client, strict reference patterns, 256 KB cap, 60 s cache, 300 lookups/user/hour. Admin page `/admin/connectors` with a "check a reference" panel; member API `GET /integrations/issues/lookup`. **Not verified:** any real GitHub/GitLab/Jira (fakes only), Jira Cloud, OAuth/App tokens, MRs. **Not done:** Teams, Outlook, Google, per-cluster rate limit. Migration `IssueConnectors` applied to RaaviDev. Editor node `raavi.issue` ("Issue link" slash command; shared manifest + fixture, so backend/frontend/collaboration parity tests cover it; 5 view tests): backend 455 / frontend 159 / collaboration 14 passed.

### 12f — Backup scheduling, status and failure alerts (ADR-042)

```
powershell deployment/scripts/backup/Test-BackupSchedule.ps1 -ServerInstance '(localdb)\MSSQLLocalDB'   → 21 checks passed, 0 failed
```

`Invoke-ScheduledBackup.ps1` (status + log + alerts on failure/recovery), `Test-BackupFreshness.ps1` (monitoring exit codes), `Register-RaaviBackupTasks.ps1` (Task Scheduler, -WhatIf/-Replace/-Unregister). **Not done / not verified:** webhook, SMTP and Event Log alerts against real endpoints (only the script hook was exercised), tasks under SYSTEM/service accounts, Linux cron/SQL Agent registration, off-site copy, scheduled restore drills.

### 12e — Security review notes and endpoint authorization guard

```
dotnet list package --vulnerable --include-transitive → none; npm audit --omit=dev (frontend) → 0
dotnet test Raavi.Tests                                → all passed (new: EndpointAuthorizationTests, 228 routes, 24 reviewed anonymous routes)
```

`docs/architecture/SECURITY_REVIEW.md` records what was verified and, explicitly, what was not. **Not done:** penetration test, DAST, full ASVS L2 walk-through, log-scrubbing test, collaboration/SPA re-review.

### 12d — Release package and upgrade script (ADR-041)

```
powershell deployment/scripts/release/Test-ReleaseScripts.ps1   → 22 checks passed, 0 failed (real dotnet publish of api/workers/migrator, first install + upgrade in a temp folder)
```

`New-ReleasePackage.ps1` (folder + zip + release.json with SHA-256, refuses sandbox/secrets/local files), `Update-Raavi.ps1` (hash check, refuses while a service runs, rollback copy, replaces only application folders, keeps sandbox, `sandbox sync`, npm ci, optional migrator, `-WhatIf`). **Not done / not verified:** frontend build inside the package (skipped in the self-test), service stop/start, IIS/Windows service/systemd/Docker integration, automatic rollback, package signing, PowerShell 7/Linux, a real server install.

### Sandbox as home of instance files + upgrade sync (ADR-040)

Key ring (`sandbox/data/keys`), mail pickup (`sandbox/data`), collaboration signing key and `collaboration.env` (`sandbox/secrets`) now live in the sandbox; relative paths resolve against the sandbox root. `sandbox-defaults/config` ships the version's settings; `Raavi.Migrator sandbox check|sync` adds new settings/files without touching existing values (originals to `sandbox/backups`); shipped scalar defaults are the lowest configuration layer; API/Workers warn on drift. `sandbox/README.md` lists what to keep. Tests: 6 new (sync, defaults layer, path resolution, repo drift guard). **Not done:** release packaging script, automatic migration of renamed keys, a hosted-environment (Linux/Docker) run of the sync, moving an old profile key ring automatically.

### 12c — Performance baseline (ADR-039)

```
dotnet test Raavi.Tests                                  → 425 passed (load tests inert without RAAVI_LOAD=1)
RAAVI_LOAD=1 … --filter Category=Load                    → 1 000 pages/40 users: 152 req/s, 0 errors; 4 000 pages/80 users: 198 req/s, 0 errors, p99 ≤ 1.1 s
```

Hot reads issue 1–13 SQL statements, not growing with item count (no N+1). No code optimisation was needed. Details: `docs/development/PERFORMANCE_BASELINE.md`.

**Not done / not proven:** 500 users, 100k pages, attachments, WebSocket load, OpenSearch; k6 scripts; closed-loop, client and server share one i5-3470, LocalDB — not a capacity statement.

### 12b — Backup, restore and restore drill for SQL Server (ADR-038)

```
powershell deployment/scripts/backup/Test-BackupScripts.ps1 -ServerInstance '(localdb)\MSSQLLocalDB'   → 16 checks passed, 0 failed (3 consecutive runs)
powershell deployment/scripts/backup/Test-RestoreDrill.ps1 -SourceDatabase RaaviDev                     → PASSED: 66 tables, 5 530 rows, 1 row-level-security policy; backup 4.9 s (131 MB), restore 5.1 s, CHECKDB 1.9 s
```

`Backup-Raavi.ps1` (full/differential/log, CHECKSUM, verify, optional encryption/compression/copy-only, manifest with SHA-256, safe retention), `Restore-Raavi.ps1` (hash check first, chain validation, side-database restore, point in time, overwrite only with `-Replace` + the exact name), `Test-RestoreDrill.ps1` (restore into a new throw-away database, CHECKDB, row counts and structure incl. RLS policies, cleanup, report, exit code). The self-test found and fixed three defects (colliding file names that overwrote a backup, STOPAT compared in server-local time, a drill backup that would have replaced the differential base). The dev drill ran against a copy of the real development database; nothing of it was changed or kept.

**Not done / not verified:** encrypted backups (LocalDB cannot create them — code path untested), scheduling and alerting, off-site copy, secrets / Data Protection key ring backup, a drill that starts the API/workers/collaboration server on the restored copy, other SQL Server versions or editions, object storage / OpenSearch / Redis (not in use).

### 12a — Workspace data export (ADR-037)

```
cd backend && dotnet test Raavi.Tests    → Passed: 423, Failed: 0   (new: ExportUnitTests 1, ExportTests 2 API: contents, permission filtering, tenant isolation, concurrency)
cd frontend && npm run build && npm test → ok; 23 files, 150 tests (new: export.test.ts 2); npm audit (production) 0
```

Tenant administrators download a ZIP built on demand in a temporary file (nothing stored): spaces, current pages with labels, comments and attachments, tasks, events, databases with rows, slide decks, whiteboards (JSON, SVG for boards), manifest and user names. Scope is **what the exporting administrator may view** — restricted pages, private spaces of others and other tenants are absent from files and counts (tests assert the hidden text appears nowhere in any file); hostile attachment names cannot escape their folder; one export per workspace at a time; attachments capped at 1 GB. UI: admin "Data export" page stating exactly what is and is not included. **Not tested:** a large workspace (time and memory), the browser download itself, opening the archive on another OS. No dedicated audit-log entry (the manifest records the exporter).

**Not in 12a:** tenant deletion with retention and purge, complete dump by the installation administrator, job/link delivery (needs 7b), import, history export, backup/restore automation, HA, load tests, hardening review.

## Phase 11 — AI & integrations (split: 11a AI assistant done; RAG, integrations, webhooks pending)

### 11d — Outbound webhooks (ADR-036)

```
cd backend && dotnet test Raavi.Tests    → Passed: 420, Failed: 0   (new: WebhookUnitTests 4 pure, WebhookTests 3 API/outbox against an in-process receiver)
EF: has-pending-model-changes (Integrations) → none; new module context IntegrationsDbContext, migration Initial (RLS)
cd frontend && npm run build && npm test → ok; 22 files, 148 tests (new: webhooks.test.ts 3); npm audit (production) 0
```

Tenant administrators define webhooks (URL, events, optional space); Raavi POSTs signed JSON (ids only, no titles or text) through the outbox: idempotent per (webhook, event), retried with back-off on 5xx/timeouts/unreachable, final on other 4xx, automatic switch-off after 20 failures in a row, delivery log without bodies, secret shown once and stored protected, installation-wide host allow-list with connect-time address checks. UI: admin "Webhooks" page (create/edit with event groups and space, test ping, secret rotation, delivery log). **Never run against a real receiver or in a real browser**: the receiver is an in-process fake and the real SSRF connect callback (DNS resolution) is not exercised by tests; the signature scheme was verified by recomputing it on the receiver side in the tests.

**Not in 11d:** payload templates, replay of failed deliveries, inbound webhooks, per-receiver rate limits, secret rotation overlap, named integrations (Jira/GitHub/GitLab/Teams/Outlook/Google), RAG (11b).

### 11c — Live whiteboards with Yjs (ADR-035)

```
cd backend && dotnet test Raavi.Tests    → Passed: 413, Failed: 0   (new: BoardAddressTests 1, BoardCollaborationTests 3 API: tickets, signed state/updates, validation, checks)
cd collaboration && npm test             → 14 tests, all passing in 6 consecutive runs (new: 4 board tests on a real Hocuspocus server + 1 fixture contract test)
EF: has-pending-model-changes (Wiki)     → none; new migration WhiteboardLiveState
cd frontend && npm run build && npm test → ok; 21 files, 145 tests (new: liveBoard.test.ts 8, whiteboardLive.test.ts 3); npm audit (production) 0
```

Boards become a second document kind on the collaboration server: editors get a board ticket, edit through Yjs (per-element field maps + order array, so concurrent changes to different fields of one element both survive), see who else is on the board, and undo only their own changes. The server persists the **full** Yjs state with the converted element list on the board row; the API validates it like a REST save. A whole-board save clears the live state and resets editors. Readers, and everyone when the collaboration server is off or unreachable, keep the ordinary whole-board editing. A frontend-generated fixture is decoded by the server tests so the two Yjs implementations cannot drift.

**Verified:** convergence, persistence and restart of a real Hocuspocus server with real Yjs clients; API validation, tickets, tenant isolation, rights re-checks; the binding logic; the view's live/fallback/reset behaviour with an in-memory session. **Not verified:** the full chain API ⇄ collaboration server ⇄ browser (no signed-in browser session, so never run end to end), the Konva canvas under live remote updates, multi-user latency, and multi-node operation. The 'reset drops the live state' test of the collaboration server was already timing-sensitive (about 1 run in 4 failed on the previous code too); it now tolerates an in-flight store, and the underlying narrow race is documented in the ADR.

**Not in 11c:** live view for readers, live cursors, offline cache, Redis multi-node, board history.

### 11a — AI assistant on pages (ADR-034)

```
cd backend && dotnet test Raavi.Tests    → Passed: 409, Failed: 0   (new: AiClientTests 3 pure, AiTests 5 API against an in-process OpenAI-compatible fake)
EF: has-pending-model-changes (Wiki, AI) → none; new module context AiDbContext, migration Initial (RLS)
cd frontend && npm run build && npm test → ok; 19 files, 134 tests (new: ai.test.ts 3); npm audit (production) 0
```

Per-tenant OpenAI-compatible provider (base URL, model, write-only key protected with a tenant-bound Data Protection purpose), summarise a page / ask about one page, usage accounting (token counts only), monthly token cap, per-user hourly limit, installation-wide host allow-list with connect-time address checks (SSRF). Leakage tests: a user without View, another tenant (with AI on) and an unknown id all get the same 404 and the fake provider records **zero** calls. Prompt: the page is delimited untrusted data; long pages are cut and the model told; answers are plain text. UI: admin "AI assistant" page (settings, usage), assistant panel on pages when AI is on. **Never run against a real model or in a real browser** — the provider is an in-process fake and the UI was tested with components only. Prompt-injection robustness of real models is not tested.

**Not in 11a:** embeddings/RAG over spaces (no vector store: SQL Server 2016 compat 130, OpenSearch deferred), editor writing help, streaming, retry/fallback/multiple providers, non-OpenAI protocols, Jira/GitHub/GitLab/Teams/Outlook/Google integrations, webhooks.

## Phase 10 — Workflows & automation (split: 10a rules engine, 10b approvals and 10c scheduling done; drag-and-drop designer, branching, external actions pending)

### 10c — Scheduling, reminders, flow view (ADR-033)

```
cd backend && dotnet test Raavi.Tests    → Passed: 401, Failed: 0   (new: SchedulingTests 3 API/job, StaleTriggerDomainTests 1; jobs run directly in the tenant scope)
EF: has-pending-model-changes (Wiki)     → none; new migration WorkflowStaleDays
cd frontend && npm run build && npm test → ok; 18 files, 131 tests (workflows.test.ts now 3); npm audit (production) 0
```

Approvals with a due date get a reminder (one day before) and an overdue notice (at the due date) as per-request tenant jobs that re-check the request when they run. New workflow trigger `content.stale` (N days without an update): daily system job `TenantFanOut<StaleSweepJob,…>` enqueues a per-tenant sweep through the new `ITenantJobScheduler.EnqueueForTenant`; a page is handled once per update, within the workflow's hourly run budget. UI: stale-day field, read-only flow view (list toggle + live preview in the editor), two new notification kinds. **Not verified:** the Hangfire recurring registration and real execution by a running worker (tests call the job code directly; the fan-out was tested with a recording scheduler); UI not exercised in a real browser. Not covered: the "failed sweep run" path.

**Not in 10c:** drag-and-drop designer, branching/parallel steps, per-workflow cron schedules, configurable reminder lead time, escalation to space administrators, reminders for tasks/calendar, run retention.

### 10b — Approvals (ADR-032)

```
cd backend && dotnet test Raavi.Tests    → Passed: 397, Failed: 0   (new: ApprovalDomainTests 2 pure, ApprovalTests 3 API through the real outbox dispatcher)
EF: has-pending-model-changes (Wiki)     → none; new migration Approvals (RLS, composite FKs)
cd frontend && npm run build && npm test → ok; 18 files, 130 tests (new: approvals.test.ts 4); npm audit (production) 0
```

A page can have one pending approval request with 1–10 named approvers (mode All or Any; any rejection ends it and needs a comment; due date optional). Editors request, assigned approvers decide, requester/space admin cancel; approvers must be able to read the page when asked and when deciding; users who cannot read the page get 404. Revision-token concurrency between simultaneous decisions; "page changed since the request" flag. Notifications through the outbox (requested → approvers, outcome → requester, cancel → pending approvers) with a fresh View check. UI: approvals panel on every page (history, approve/reject/cancel, request dialog with user picker), personal "Approvals" page (waiting for me / requested by me) in the main navigation, four new notification kinds. **Not exercised in a real browser** (component tests only). The simultaneous-decision race (412 + retry) rests on the EF concurrency token and was not tested concurrently.

**Not in 10b:** chains, group approvers, delegation, reminders/escalation (overdue is display-only), approval as a workflow action/trigger, e-mail, approval-before-publish, retention.

### 10a — Workflows (ADR-031)

```
cd backend && dotnet test Raavi.Tests    → Passed: 392, Failed: 0   (new: WorkflowDomainTests 2 pure, WorkflowTests 3 API through the real outbox dispatcher)
EF: has-pending-model-changes (Wiki)     → none; new migration Workflows (RLS + composite FKs, unique run key)
cd frontend && npm run build && npm test → ok; 17 files, 126 tests (new: workflows.test.ts 2); npm audit (production) 0
```

Rules per space (administrators only): trigger = page event, conditions (title / content type / label, AND), actions `AddLabel` and `Notify` (watchers or the actor; recipients' View right re-checked). Engine = outbox handler: one `WorkflowRun` per (workflow, event) (idempotent), failed runs retried by the outbox with back-off, skipped runs for unmet conditions / missing page / rate limit (100 executed runs per workflow per hour), no action emits page events so rules cannot loop. UI: per-space "Workflows" page (admins), form editor, on/off, run history; new "workflow" notification kind. **Not exercised in a real browser.** Not covered by tests: the Failed→retry path and the hourly rate limit (both rely on code paths that need fault injection / 100 events); the watcher View-right re-check is code-shared with comment notifications and not re-tested here.

**Not in 10a:** approvals, visual designer, time-based triggers, e-mail/webhook/edit actions, explicit-user recipients, run retention, tenant-wide rules.

## Phase 9 — Advanced content types (split: 9a tasks, 9b calendar, 9c databases, 9d slides and 9e whiteboards done; live board co-editing and advanced macros pending)

### 9e — Whiteboards (ADR-030)

```
cd backend && dotnet test Raavi.Tests    → Passed: 387, Failed: 0   (new: WhiteboardDomainTests 2 pure, WhiteboardTests 3 API incl. SVG well-formedness/escaping)
EF: has-pending-model-changes (Wiki)     → none; new migration Whiteboards (RLS + composite FK)
cd frontend && npm run build && npm test → ok; 16 files, 124 tests (new: whiteboards.test.ts 7); npm audit (production) 0
```

Konva chosen over Fabric after measuring (62 KB vs 90 KB gzip for a comparable editor; ADR-030). Boards per space: rect, ellipse, text, sticky note, arrow, pen; select/move/resize/rotate (transformer), pan/zoom/fit, colours, line width, duplicate, stacking order, undo/redo, element list + inspector as the accessible path, unsaved-changes guard, whole-board ETag saves, permissions from the space, PNG export (canvas) and SVG export (server). The Konva canvas was **driven in a real browser through a throw-away harness page** (select, drag-move, creation of rect/pen/arrow/text, anchor resize 160×90 → 220×150, PNG export 2× opaque, stage restored); the signed-in editor page (toolbar, inspector, save) was only exercised through component tests with a stand-in canvas, because no signed-in browser session was available. One timing-sensitive frontend test (`databases`) failed once while the backend suite ran concurrently and passed on an unloaded rerun.

**Not in 9e:** live co-editing (Yjs — needs a "board" kind in the collaboration server, ADR-030), images, attached connectors, re-shaping arrows/strokes, grouping, snapping, board history, PDF export.

### 9d — Slides (ADR-029)

```
cd backend && dotnet test Raavi.Tests    → Passed: 382, Failed: 0   (new: SlideDomainTests 2 pure, SlideTests 3 API incl. OpenXmlValidator 0 errors)
EF: has-pending-model-changes (Wiki)     → none; new migration Slides (RLS + composite FK)
cd frontend && npm run build && npm test → ok; 15 files, 117 tests (new: slides.test.ts 5); npm audit (production) 0; NuGet vulnerable: none
```

Slide decks per space: ordered text slides (Title / Bullets / Text layouts) with speaker notes, Ltr/Rtl direction, stable slide ids, whole-deck ETag saves, permissions from the space. UI: per-space "Slides" list, editor with slide strip (add, reorder, delete), live preview, presenter mode (keyboard, notes, mirrored arrows for RTL), unsaved-changes guard. Export: PPTX on the server (Open XML SDK, schema-validated with OpenXmlValidator, speaker notes, RTL) and PDF through a shell-less print page (browser "Save as PDF"). **PPTX was not opened in PowerPoint; UI not exercised in a real browser.**

**Not in 9d:** images, shapes, tables, charts, transitions, themes, PPTX import, slide embedding in pages, co-editing/comments, deck history, server-side PDF.

### 9c — Databases (ADR-028)

```
cd backend && dotnet test Raavi.Tests    → Passed: 377, Failed: 0   (new: DatabaseDomainTests 2 pure, DatabaseTests 3 API)
EF: has-pending-model-changes (Wiki)     → none; new migration Databases (RLS + composite FKs)
cd frontend && npm run build && npm test → ok; 14 files, 112 tests (new: databases.test.ts 3); npm audit (production) 0
```

Metadata-driven tables per space: columns (Text, Number, Date, Checkbox, Select, Url) stored as data with stable ids, rows as validated JSON, no runtime DDL. Server-side free-text filter, one-column sort (empty last), paging, CSV export (BOM, formula-safe), ETag concurrency for schema and rows, permissions from the space, type of existing columns locked. UI: per-space "Databases" list, table with in-place cell editing, sortable headers, search, column editor dialog, read-only rendering without Edit. Verified through unit/API/component tests; **not exercised in a real browser**.

**Not in 9c:** relations/rollups/formulas, multi-select/user/file columns, saved views, filters per column, board/gallery layouts, CSV import, embedding in pages, row-level permissions, row history, search indexing.

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
