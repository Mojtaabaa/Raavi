# CLAUDE.md — Raavi

Raavi is a multi-tenant, fully on-premises enterprise wiki (Confluence Cloud feature baseline). Read `docs/architecture/SYSTEM_ARCHITECTURE.md` and the ADR index (`docs/architecture/adr/README.md`) before changing architecture.

## Commands

- Backend build: `cd backend && dotnet build Raavi.slnx` (warnings are errors)
- Backend tests: `cd backend && dotnet test Raavi.slnx`
- Frontend: `cd frontend && npm install && npm run build && npm test`
- Run API: `cd backend && dotnet run --project Raavi.Api --launch-profile http` (http://localhost:5180)
- Collaboration server: `cd collaboration && npm install && npm test` (Node ≥ 22.18); run with `RAAVI_API_URL` + `RAAVI_COLLAB_SERVICE_KEY` (= API `RAAVI_Raavi__Collaboration__ServiceKey`), `node src/main.ts` (port 1234)
- Run SPA: `cd frontend && npm run dev` (http://localhost:5173, proxies /api)

## Non-negotiable rules

1. **Tenant isolation**: never read `TenantId` from requests; use `ITenantContext`. Every tenant-owned entity implements `ITenantOwned`. Foreign-tenant ids → 404. Follow `docs/architecture/TENANT_ISOLATION.md`.
2. **Authorization**: every resource access goes through `IPermissionEvaluator` — REST, search, attachments, exports, AI context, collaboration tickets, notifications.
3. **Configuration**: global settings → `sandbox/config/*.json` (no secrets; env vars `RAAVI_*` for secrets). Tenant settings → SQL Server only.
4. **Module boundaries**: Domain ← Application ← Infrastructure ← feature modules ← hosts; feature modules never reference each other (`MODULE_BOUNDARIES.md`, enforced by `DependencyRulesTests`).
5. **Authentication ≠ membership ≠ authorization.** No e-mail-based auto-linking of identities. No password forwarding to Jira.
6. **Editor content**: canonical Raavi Document JSON validated server-side; HTML is derived and sanitized; imported content is untrusted.
7. **No fake functionality**: no hardcoded production data, no buttons without working backend.
8. **Avoid** MediatR ≥13, AutoMapper ≥15, MassTransit ≥9, FluentAssertions ≥8, Duende, Tiptap Pro, **PrimeVue ≥5** (commercial PrimeUI license — decided 2026-11-06: stay on 4.5.x MIT, only 4.x updates). Re-check the licence of the *new* version before any major upgrade (`DEPENDENCY_AND_LICENSING.md`).
12. Tenant-owned tables: implement `ITenantOwned`, register the DbContext via `AddRaaviDbContext`, and add `migrationBuilder.AddTenantIsolation(schema, table)` in the migration — `TenantIsolationModelTests` fails otherwise.
13. Never read configuration eagerly during service registration; resolve `IConfiguration` from the provider (host/test overrides are applied at build time).
14. Backend package versions live in `backend/Directory.Packages.props` (central management + transitive pinning).
15. `IDENTITY` is a T-SQL reserved word: write `[identity]` in any raw SQL.
16. Identity UI/API: never put tokens in query strings (use `#token=` fragments); every unsafe cookie request needs `X-XSRF-TOKEN`; generic auth errors only (no account enumeration).
17. Frontend: PrimeIcons stays on 7.x (8 is commercially licensed).
23. Collaboration: the server never touches the database; all persistence goes through HMAC-signed `/internal/collaboration/*` (verified in middleware before binding). Node code runs via type stripping: no enums or constructor parameter properties. Patch migrations with the idempotent RLS script.
22. Readers only ever see published versions; editing goes through the shared draft (`PublishingService`). Background work acting for a user uses `WikiActor.ActAs` and reads permissions fresh. Scripts that patch migrations must be idempotent; `dotnet ef migrations remove` needs a database connection (in its absence add a follow-up migration instead).
21. Editor contract: every node/mark has a manifest in `schema/editor/extensions` **and** a fixture in `schema/editor/fixtures`; both test suites round-trip every fixture. Changing a manifest means bumping its version (and `registry.json` schemaVersion + a migration step in `RaaviDocument` when stored documents change). Mermaid stays on 11.x (12.x pulls EPL/GPL `elkjs`); vue-i18n messages must escape `@` as `{'@'}`.
20. Wiki access checks go through `WikiAuthorizer` (implements `IPermissionEvaluator`); invisible content is 404. Versioned updates require `If-Match` (`ETags` helper). Cross-module events go through the module outbox in the same SaveChanges (ADR-023).
19. External identities are keyed only by (provider, subject); never link or merge by e-mail; unverified provider e-mail must never become `NormalizedEmail`.
18. Scripted edits of generated files (EF migrations use CRLF) must normalise line endings and fail when the anchor text is not found.
9. **Frontend**: all strings via i18n (fa + en catalogs must match), CSS logical properties only, no external CDNs, no `v-html` outside the sanitized renderer.
10. Never claim a build/test passed without running it; update `docs/IMPLEMENTATION_STATUS.md` after each phase.
11. No destructive database operations without explicit approval.

## Current phase

Phases 0–6, 7a, 9a (tasks), 9b (calendar) and 9c (databases) complete; Phase 8 (Confluence migration) in progress — reader, converter, importer, jobs, API, admin UI, attachments, people mapping, restrictions and dynamic lists (children/contentbylabel) done and verified live; space permissions (need XML reader), comments, include/roadmap macros remain. 7b (S3 media, quotas, previews, branding assets) and 7c (OpenSearch) are deferred by the user until S3/MinIO and OpenSearch are available; do not implement unless asked (`docs/product/IMPLEMENTATION_ROADMAP.md`). External providers were verified only against in-process fakes (Jira/AD real-instance checks pending, Q1).

Identity tests use `FakeExternalIdentity` (OIDC + Jira over a fake HttpMessageHandler on the `raavi-identity` HttpClient) and `FakeLdapDirectory`; provider definitions for tests live in `SqlServerFixture.Settings`.

DB tests use LocalDB by default (`RAAVI_TEST_SQL` overrides). Migrations: `cd backend && dotnet tool restore && dotnet ef migrations add <Name> --project Raavi.Tenancy --startup-project Raavi.Migrator --context TenancyDbContext --output-dir Persistence/Migrations`.
