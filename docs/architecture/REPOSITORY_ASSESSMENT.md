# Repository Assessment

**Date:** 2026-11-06 · **Phase:** 0 · **Assessor:** Claude Code (architecture agent)

## 1. Current state at the start of Phase 0

The repository `C:\Projects\Ravi` was inspected before any change was made.

| Aspect | Finding |
|---|---|
| Files | **None.** The directory was empty (no source, docs, configuration or hidden files). |
| Version control | Not a git repository. `git init` was run in Phase 0 (no commits made). |
| Existing applications / libraries | None. |
| Database schemas / migrations | None. |
| Authentication / authorization | None. |
| APIs / integration points | None. |
| Frontend components / UI conventions | None. |
| Tests / CI | None. |
| Infrastructure / deployment | None. |
| Incomplete / duplicated / obsolete code | None. |

There is therefore nothing to preserve, migrate or replace; Phase 0 is a greenfield foundation. No existing behaviour was removed.

### Development machine toolchain (verified)

| Tool | Version found | Notes |
|---|---|---|
| .NET SDK | 10.0.202 (also 7, 8, 9) | Target: `net10.0` |
| Node.js / npm | 24.18.0 / 11.16.0 | Frontend and collaboration server |
| SQL Server tools | `sqlcmd` 15.0 | A local SQL Server instance was not verified |
| Docker | **not installed** | Compose files are authored but could not be executed on this machine |
| git | 2.50.1 | |

## 2. What Phase 0 created

| Area | Delivered | Verified by |
|---|---|---|
| Backend solution | `backend/Raavi.slnx`, 12 projects (Domain, Application, Infrastructure, Tenancy, Identity, Migration, Search, AI, Integrations, Api, Workers, Tests) with enforced dependency direction | `dotnet build` (warnings as errors), architecture tests |
| Contracts in code | `ITenantContext`, `ITenantResolver`, `IPermissionEvaluator`, `IContentProvider`, `IAuthenticationProvider` (+ registry, provisioning, membership), Confluence import contracts, `TenantJobContext` | Compile |
| Working foundation code | Sandbox configuration loader, host normalization & subdomain parsing, Persian text normalizer, correlation-id middleware, security headers, RFC 9457 problem details, health endpoints, JSON structured logging | 65 xUnit tests |
| Frontend | Vue 3.5 + TypeScript + Vite 8 + PrimeVue 5 + Pinia + vue-i18n; Persian/English with RTL/LTR, Jalali date formatting, self-hosted fonts, design tokens | `vue-tsc`, `vite build`, 8 Vitest tests |
| Global configuration | `sandbox/config/*.json` (no secrets) | Loader tests |
| Documentation | Architecture docs, 22 ADRs, database design, API conventions, roadmap, feature matrix, implementation status | Review |
| CI | `.github/workflows/ci.yml` (backend + frontend) | Not executed (no remote) |
| Dev infrastructure | `deployment/compose/docker-compose.dev.yml` | **Not executed** (Docker absent) |

## 3. Reusable components

Nothing pre-existed. Within the new foundation the following are designed to be reused by every later phase:

- `SandboxConfiguration` — the only way global configuration enters a host.
- `TenantHost` — the only way a Host header becomes a tenant lookup key.
- `CorrelationIdMiddleware`, `SecurityHeadersMiddleware`, `ApiFoundationExtensions` — shared by API and any future HTTP host.
- `PersianTextNormalizer` — shared by search queries, suggestions, slugs and duplicate detection.
- Frontend `core/i18n/locales.ts` (direction, precedence, calendar-aware formatting) and `core/theme` tokens.

## 4. Technical debt / gaps (intentional, tracked)

| Item | Why it exists | Resolved in |
|---|---|---|
| No persistence (EF Core, SQL Server) yet | Phase 0 defines contracts only | Phase 1 |
| Tenant resolution middleware not implemented (only host parsing) | Needs Tenant/TenantDomain tables | Phase 1 |
| OpenTelemetry exporters not wired | Collector endpoints not yet defined | Phase 1 |
| Hangfire not registered in Workers | Needs SQL Server storage | Phase 1 |
| Collaboration server (`collaboration/`) has only a README | Implementation is Phase 6 | Phase 6 |
| CI pipeline never executed | No git remote / CI runner known | When a remote is configured |
| Docker Compose never executed | Docker not installed on this machine | First environment with Docker |
| Jira capabilities not verified against the real Jira | Instance URL/version not provided | Phase 2 (see §6) |

## 5. Architecture gaps closed by Phase 0 decisions

See the ADR index in `docs/architecture/adr/README.md`. The seven foundations requested for Phase 0 are covered by:

1. Multi-tenancy & isolation — `MULTI_TENANT_ARCHITECTURE.md`, `TENANT_ISOLATION.md`, ADR-003, ADR-005.
2. Tenant resolution — ADR-004, `MULTI_TENANT_ARCHITECTURE.md` §3–§5.
3. Global vs tenant configuration — `SYSTEM_ARCHITECTURE.md` §6, ADR-003 §Configuration.
4. Multiple authentication providers incl. Jira — `AUTHENTICATION_ARCHITECTURE.md`, ADR-006, ADR-007.
5. Confluence import with history — `docs/migration/CONFLUENCE_MIGRATION.md`, ADR-008.
6. Editor extension registry — `EDITOR_ARCHITECTURE.md`, ADR-010.
7. Real-time collaboration & permissions — `COLLABORATION_ARCHITECTURE.md`, ADR-011, ADR-012.

## 6. Open questions that need customer input

These do not block Phase 1 but block the named phase.

| # | Question | Blocks |
|---|---|---|
| Q1 | (Phase 2b: OAuth 2.0 provider implemented; needs these answers for real-instance verification) Exact Jira product, version and build (Server vs Data Center; ≥ 8.22?) and whether an OAuth 2.0 incoming application link may be created. Is Jira already federated to an IdP (SAML/OIDC, Crowd)? | Phase 2 Jira provider |
| Q2 | Source Confluence edition/version and whether REST API access with an admin account or a full site XML backup will be provided. | Phase 8 |
| Q3 | Base domain(s) for tenant subdomains (e.g. `raavi.ir`) and the certificate strategy (wildcard certificate vs per-domain). | Phase 1 deployment, custom domains |
| Q4 | Object storage: customer-provided S3-compatible storage, or must Raavi ship one? (MinIO licensing — see `DEPENDENCY_AND_LICENSING.md`). | Phase 7 |
| Q5 | CI platform (GitHub Actions assumed; Azure DevOps / GitLab possible). | CI |
| Q6 | ~~PrimeVue ≥ 5 is commercially licensed — stay on 4.5.x or buy a license?~~ **Resolved 2026-11-06: stay on PrimeVue 4.5.x (MIT).** | — |

## 7. Recommended implementation strategy

1. Proceed phase by phase exactly as `docs/product/IMPLEMENTATION_ROADMAP.md`; each phase ends with build + tests + docs + status update.
2. Phase 1 starts with the tenant tables, the tenant resolution middleware and the tenant isolation test suite **before** any tenant-owned feature, so every later table is born tenant-aware.
3. Keep the tenant-isolation regression suite (`TESTING_GUIDE.md` §4) growing with each module; a module is not done until its isolation tests exist.
4. Verify uncertain external capabilities (Jira OAuth 2.0, Confluence history export, SQL Server `SESSION_CONTEXT` behaviour with pooling) with spikes and integration tests before building on them.
