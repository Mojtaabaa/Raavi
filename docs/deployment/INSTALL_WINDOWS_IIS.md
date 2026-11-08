# Installing Raavi on a Windows server (IIS + services)

> Written for releases 0.13.x (2026-11-08). The first real installation has not happened yet — treat this as a checklist to be corrected by the first run, and report what was wrong.

## 0. What you get

`raavi-<version>.zip` (see `scripts/release/New-ReleasePackage.ps1`): `api/ workers/ migrator/ web/ collaboration/ schema/ sandbox-defaults/ docs/ scripts/ release.json`. It contains **no** settings, keys or secrets (`sandbox/` is yours). The SQL role scripts are in `scripts\sql\` (packages 0.13.1 and later; 0.13.0 lacked them).

## 1. Server prerequisites

- Windows Server with IIS, **URL Rewrite** and **Application Request Routing (ARR)** (proxy enabled; WebSocket Protocol feature enabled).
- **.NET 10 ASP.NET Core Runtime / Hosting Bundle** (matches `Raavi.Api.runtimeconfig.json`).
- SQL Server 2016+ reachable from the server (SQL Server 2022 recommended; set `Raavi:Database:CompatibilityLevel` to the real compatibility level of your database: 130 for 2016, 140, 150, 160 for 2022).
- Node.js **22.18+** only if you enable real-time co-editing (collaboration server).
- DNS: `admin.<domain>` and `*.<domain>` (or one host per workspace) pointing at the server; a wildcard TLS certificate for `*.<domain>` and `admin.<domain>`.

## 2. Unpack and first install

```powershell
Expand-Archive raavi-0.13.0.zip C:\Deploy\raavi-0.13.0
powershell -ExecutionPolicy Bypass -File C:\Deploy\raavi-0.13.0\scripts\release\Update-Raavi.ps1 `
  -PackageDirectory C:\Deploy\raavi-0.13.0 -InstallDirectory D:\Raavi -SkipNpm
```

This creates `D:\Raavi\{api,workers,migrator,web,...}` and `D:\Raavi\sandbox\{config,data,secrets}`; `config` is initialised from the shipped defaults. Applications find `D:\Raavi\sandbox\config` by themselves (they look upwards from their own folder); set `RAAVI_SANDBOX_PATH` only if you put it elsewhere. Give the service/app-pool account **modify** rights on `D:\Raavi\sandbox\data` (the Data Protection key ring lives there) and read rights on `config` and `secrets`. **Back up `sandbox\` together with the database** (without `data\keys` the encrypted values — TOTP, AI keys, webhook secrets — cannot be read).

## 3. Settings to edit (`D:\Raavi\sandbox\config\appsettings.json`)

| Key | Set to |
|---|---|
| `Raavi:Hosting:SystemHost` | `admin.example.com` |
| `Raavi:Hosting:TenantBaseDomains` | `["example.com"]` (workspaces become `<slug>.example.com`) |
| `Raavi:Hosting:PublicScheme` | `https` |
| `Raavi:Hosting:KnownProxies` | the IP the proxy connects from (`["127.0.0.1"]` when IIS/ARR is on the same server; loopback is trusted by default) |
| `Raavi:Database:CompatibilityLevel` | e.g. `160` for SQL Server 2022 |
| `Raavi:Email` (`notifications.json`) | SMTP settings, `Mode: Smtp` — otherwise no notification e-mail is sent |

**Secrets are not written in files.** Set machine-level environment variables for the services (or `sandbox\secrets`/service environment): `RAAVI_ConnectionStrings__System`, `RAAVI_ConnectionStrings__Migrator` (DDL login, migrator only), `ASPNETCORE_ENVIRONMENT=Production`.

Optional hosts the installation allows (empty = feature off): `Raavi:Ai:AllowedHosts`, `Raavi:Integrations:AllowedWebhookHosts`, `Raavi:Integrations:AllowedConnectorHosts`, `Raavi:Migration:AllowedSourceHosts`.

## 4. Database

1. Create an empty database (collation default is fine).
2. Run the migrator once with a login that may create tables: `dotnet D:\Raavi\migrator\Raavi.Migrator.dll`.
3. Run `scripts\sql\01-database-roles.sql` and `02-logins.sql` as described in `DEPLOYMENT_GUIDE.md` §4, then point the **API and workers** at the restricted `raavi_app` login (`RAAVI_ConnectionStrings__System`). The application hosts never migrate.
4. Enable the FULL recovery model and schedule backups (`BACKUP_AND_RESTORE.md`, `scripts\backup`).

## 5. Run the services

Two long-running processes from the same install folder (use NSSM, `sc.exe` with a wrapper, or Windows service hosting):

- API: `dotnet D:\Raavi\api\Raavi.Api.dll --urls http://127.0.0.1:5180`
- Workers: `dotnet D:\Raavi\workers\Raavi.Workers.dll` (outbox, notifications, scheduled jobs — **required**)

Check `http://127.0.0.1:5180/health/ready`.

## 6. IIS front door (same-origin, host header preserved)

The SPA and the API must be served from the **same host name** (cookies are host-only and the workspace is chosen by host name).

- One IIS site with bindings for `admin.example.com` and `*.example.com` (https), physical path `D:\Raavi\web`.
- URL Rewrite rules that proxy `^api/(.*)`, `^hubs/(.*)`, `^health/(.*)` to `http://127.0.0.1:5180/{R:0}`; **ARR → "Preserve host header" must be ON** (Server Variables: allow `HTTP_X_FORWARDED_HOST`, `HTTP_X_FORWARDED_PROTO`; add them in the rule as `{HTTP_HOST}` / `https`), WebSocket for `/hubs`.
- SPA fallback: any other path that is not a file → `/index.html`.
- Do **not** expose `sandbox\`; it is outside `web\` on purpose.
- Timeout/body limits: raise `maxAllowedContentLength` if you allow large attachments (`Raavi:Attachments:MaxBytes`).

## 7. First start

1. Create a bootstrap operator key hash and set `Raavi:Security:SystemApiKeys` (see `docs/api/IDENTITY_API.md` "Bootstrapping a new installation"): invite the first **system administrator** with that key, open the setup link on `https://admin.example.com`, scan the TOTP QR code, set a password.
2. Sign in at `/system/login`, create the first workspace, invite its administrator, then remove/rotate the bootstrap key.
3. Open `https://<slug>.example.com`, sign in, create a space and a page.

## 8. Not needed / not included in this version

S3/MinIO, OpenSearch and Redis are not required for the implemented features (Redis connection string stays empty). Real-time co-editing needs the collaboration server (`Raavi:Collaboration:Enabled`, Node 22.18+, `npm ci --omit=dev` in `D:\Raavi\collaboration`, `sandbox\secrets\collaboration.env`, signing key `.pem`, service key ≥ 32 chars in both sides) — leave it off for the first start; without it pages are edited one person at a time.

## 9. Upgrades

Stop services → `Update-Raavi.ps1` with the new package (keeps `sandbox\`, runs `sandbox sync`, keeps a rollback copy in `D:\Raavi\previous\<time>`; add `-RunMigrator` with `RAAVI_ConnectionStrings__Migrator` set) → start services. Restore the database from backup if a migration fails half-way; the rollback copy only restores program files.
