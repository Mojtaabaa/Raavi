# Deployment Guide

> **Status:** Draft from Phase 0. Production images and the Helm chart are delivered in Phases 1–12; this guide fixes the target shape (ADR-018).

## 1. Topologies

| Topology | Use | Components |
|---|---|---|
| Development | Local dev | `deployment/compose/docker-compose.dev.yml`: SQL Server 2022, Valkey (Redis protocol), OpenSearch, MinIO, Mailpit |
| Single server | Small/medium installations | Compose: web (reverse proxy + SPA), api, workers, collab, migrator + infrastructure (or external) |
| High availability | Large installations | Kubernetes Helm chart; SQL Server Always On AG (customer), Redis/Valkey Sentinel or cluster, OpenSearch 3-node cluster, distributed S3 store |

## 2. DNS and TLS

- Wildcard record `*.raavi.example` → reverse proxy; system host `admin.raavi.example`; collaboration either same host path `/collab` (recommended) or `collab.raavi.example`.
- Wildcard certificate for each base domain. Custom domains: the customer points a CNAME to the proxy and the operator provisions a certificate (ACME on-demand TLS via the proxy, or a manually installed certificate).
- Reverse proxy must forward `X-Forwarded-For/Proto/Host` and its address must be listed in `Raavi:Hosting:KnownProxies`.

## 3. Configuration

- Keep the whole `sandbox/` folder (ADR-040, `sandbox/README.md`): `config/` (settings), `data/` (key ring, written by the app — must be writable and shared by all API nodes) and `secrets/`. Set `RAAVI_SANDBOX_PATH` to its `config` folder, e.g. `/etc/raavi/sandbox/config` (mount `config` read-only if you wish; `data` and `secrets` are siblings of it).
- Provide secrets as environment variables (`RAAVI_ConnectionStrings__System`, `RAAVI_Raavi__ObjectStorage__SecretKey`, …) or Docker/Kubernetes secrets mapped to them.
- Never expose the sandbox directory through the web server; the application refuses to start if it is inside `wwwroot`.

## 4. Database

- SQL Server 2022+. After the first migration run `deployment/scripts/sql/01-database-roles.sql` (runtime role: DML only, cannot alter tables, schemas or the RLS policy; verified by `DatabaseRolesTests`) and `02-logins.sql` (logins `raavi_app` → runtime role, `raavi_migrator` → db_owner; passwords from your secret store via sqlcmd variables). API/Workers use `raavi_app`; only the migrator uses `raavi_migrator` (`ConnectionStrings:Migrator`).
- Set `Raavi:Security:DataProtection:KeyRingPath` only to place the key ring elsewhere; the default is `sandbox/data/keys` (a shared, backed-up directory for all API nodes: it protects TOTP secrets, AI keys, webhook secrets and antiforgery tokens).
- Run the migrator before starting new versions:

```bash
docker compose run --rm migrator
```

## 5. Health and monitoring

- Liveness `/health/live`, readiness `/health/ready` for load balancers / Kubernetes probes.
- OTLP endpoint configured in `appsettings.json` (Phase 1); JSON logs on stdout.

## 6. Upgrades

Scripted path (Windows PowerShell, ADR-041): build once with `deployment/scripts/release/New-ReleasePackage.ps1 -Version 1.2.0`; on the server stop API, workers and collaboration server, then run `Update-Raavi.ps1 -PackageDirectory <package> -InstallDirectory <install> [-RunMigrator]`. It verifies the package, keeps a rollback copy in `<install>/previous/<time>`, replaces only the application folders, keeps `sandbox/`, runs `sandbox sync` and (optionally) the database migrator. Start the services afterwards. Manual steps:

1. Back up (BACKUP_AND_RESTORE) — including `sandbox/`.
2. Pull new images / replace the application folders **but not `sandbox/`** (it holds your settings, key ring and secrets).
3. Run `Raavi.Migrator sandbox check`, then `sandbox sync` (adds settings that are new in this version; see `sandbox/README.md`), then run the migrator (expand migrations only).
4. Rolling restart api → workers → collab → web.
5. Contract migrations ship in the following release.

## SPA Content-Security-Policy (Phase 4)

The reverse proxy serving the SPA must send a CSP that allows the editor's sandboxed Mermaid frames and KaTeX fonts, for example:

```
default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; frame-src data:; object-src 'none'; base-uri 'self'; frame-ancestors 'none'
```

`frame-src data:` is required because Mermaid (`securityLevel: sandbox`) renders each diagram in a `sandbox` iframe with a `data:` URL. `style-src 'unsafe-inline'` is needed by PrimeVue and ProseMirror inline styles. API responses keep their own deny-all CSP.

## Collaboration server (Phase 6)

- Run `collaboration/` (Node ≥ 22.18) as its own service: `RAAVI_API_URL` (internal URL of the API), `RAAVI_COLLAB_SERVICE_KEY` (≥ 32 random characters, shared with the API as `RAAVI_Raavi__Collaboration__ServiceKey`), `RAAVI_ALLOWED_ORIGINS` (tenant origins), `PORT`.
- API: `Raavi:Collaboration:Enabled=true`, `PublicUrl` (wss URL browsers use), `ServerUrl` (internal URL of the collaboration server), `SigningKeyPath` (PEM, P-256 private key, mounted secret — generate with `openssl ecparam -name prime256v1 -genkey -noout -out key.pem`).
- The reverse proxy must **not** expose `/internal/*` of the API or of the collaboration server.
- One collaboration node for now (multi-node Redis wiring is pending).
