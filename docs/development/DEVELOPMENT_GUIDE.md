# Development Guide

## 1. Prerequisites

- .NET SDK 10.0.2xx
- Node.js 22+ (24 used in Phase 0), npm 10+
- Docker (for local SQL Server, Valkey/Redis, OpenSearch, MinIO) — or equivalent locally installed services
- Optional: SQL Server Management Studio / Azure Data Studio

## 2. Layout

```
backend/        ASP.NET Core solution (Raavi.slnx)
frontend/       Vue 3 SPA
collaboration/  Hocuspocus server (Phase 6)
sandbox/config/ global configuration (no secrets)
deployment/     compose, kubernetes, scripts
docs/           architecture, ADRs, guides
```

## 3. Running locally

### Database without Docker (Windows, LocalDB)

```bash
sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "IF DB_ID('RaaviDev') IS NULL CREATE DATABASE RaaviDev"
```

```bash
export RAAVI_ConnectionStrings__System="Server=(localdb)\MSSQLLocalDB;Database=RaaviDev;Integrated Security=true;TrustServerCertificate=true"
export RAAVI_Raavi__Database__CompatibilityLevel=130   # only for LocalDB 2016; omit on SQL Server 2022
```

```bash
cd backend && dotnet run --project Raavi.Migrator
```

The migrator applies EF migrations (incl. Row-Level Security) and installs the Hangfire schema; it is idempotent. Application hosts never migrate.

### System administrator key (Phase 1 bootstrap)

```bash
export RAAVI_Raavi__Security__SystemApiKeys__0__Id=dev
export RAAVI_Raavi__Security__SystemApiKeys__0__Sha256=$(printf %s "$RAAVI_DEV_KEY" | sha256sum | cut -d' ' -f1)
```

Then follow "Bootstrapping a new installation" in `docs/api/IDENTITY_API.md`: invite a system administrator with the key, open the setup link on `http://admin.raavi.localhost:5173`, enroll TOTP, then create workspaces and invite their administrators from the UI.

Development runs over plain http, so `sandbox/config/authentication.Development.json` enables `AllowInsecureHttp` (non-Secure cookies without the `__Host-` prefix). It is refused outside Development.

Local test accounts you create can be noted in `deployment/dev/*.local` (git-ignored), never in tracked files.

### With Docker

```bash
docker compose -f deployment/compose/docker-compose.dev.yml --env-file deployment/compose/.env up -d
```

```bash
cd backend && dotnet run --project Raavi.Api --launch-profile http
```

```bash
cd frontend && npm install && npm run dev
```

- API: http://localhost:5180 (health: `/health/live`, `/health/ready`, OpenAPI in Development: `/openapi/v1.json`).
- SPA: http://localhost:5173 (proxies `/api` and `/health` to the API).
- Redis is optional in development (`ConnectionStrings:Redis` empty → in-process cache only).
- Workers: `cd backend && dotnet run --project Raavi.Workers` (Hangfire server + recurring domain verification).
- Tenant hosts in development: `http://<slug>.raavi.localhost:5173` — `*.localhost` resolves to loopback in Chromium/Firefox; system host `admin.raavi.localhost`.

## 4. Configuration and secrets

- Global configuration is loaded from `sandbox/config` (found by walking up from the content root, or `RAAVI_SANDBOX_PATH`).
- Never put secrets in `sandbox/config`. Use environment variables with the `RAAVI_` prefix, e.g.:

```bash
export RAAVI_ConnectionStrings__System="Server=localhost,14333;Database=Raavi;User Id=sa;Password=...;TrustServerCertificate=True"
```

- Tenant settings are data in SQL Server — never add tenant-specific values to config files.

## 5. Coding conventions

### Backend
- File-scoped namespaces, nullable enabled, warnings as errors, `latest-recommended` analyzers (`.editorconfig`).
- Respect module dependency rules (`docs/architecture/MODULE_BOUNDARIES.md`); architecture tests will fail otherwise.
- Use cases return `Result`/`Result<T>` for expected failures with stable error codes (`area.reason`); exceptions only for bugs/infrastructure.
- Never accept `TenantId` from a request; use `ITenantContext`.
- Every resource access → `IPermissionEvaluator`.
- `ConfigureAwait(false)` in library code; async all the way.
- Log with structured templates; never log secrets or content.

### Frontend
- `<script setup lang="ts">`, strict TypeScript, Pinia stores, composition-API i18n (`useI18n`).
- All user-visible text through i18n catalogs (fa + en keys must match — tested).
- CSS logical properties only (`margin-inline-start`, not `margin-left`); design tokens from `core/theme/tokens.css`.
- No `v-html` except the sanitized content renderer.
- No external CDNs (on-prem/air-gapped).

## 6. Git
- Branches `feature/<phase>-<topic>`; conventional commit messages; PRs require green CI.
