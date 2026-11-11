# Installing Raavi on a Windows server (IIS + one worker service)

> Written for releases 0.13.x (2026-11-08). The first real installation has not happened yet — treat this as a checklist to be corrected by the first run, and report what was wrong. A Persian version is `INSTALL_WINDOWS_IIS_fa.md`.

## 0. Shape of the installation

```
browser ──https──► IIS site "RaaviWeb"  (D:\Raavi\web, bindings admin.<domain> and *.<domain>)
                    ├─ static files of the SPA (+ index.html fallback)
                    └─ api/ hubs/ health/  ──proxy (ARR, X-Forwarded-Host)──►  IIS site "RaaviApi"
                                                                                     (D:\Raavi\api, http://127.0.0.1:5180, in-process)
Windows service "RaaviWorkers" (NSSM)  – outbox, notifications, scheduled jobs (no web, no port)
SQL Server  ◄── API and workers (login raavi_app)      migrator (login raavi_migrator, run by hand)
D:\Raavi\sandbox\  – settings, key ring, secrets (yours; kept on upgrades)
```

Why two IIS sites: the SPA and the API must be reached by the **same host name** (cookies are host-only; the workspace is chosen by host name), so the public site proxies the API paths to an internal-only API site.

## 1. Server prerequisites

- IIS with: **URL Rewrite**, **Application Request Routing (ARR)**, the **WebSocket Protocol** feature and the **.NET 10 Hosting Bundle** (installs the ASP.NET Core Module V2; check with `dotnet --list-runtimes` → `Microsoft.AspNetCore.App 10.x`). Run `iisreset` after installing the bundle.
- SQL Server 2016+ reachable from the server (2022 recommended). Set `Raavi:Database:CompatibilityLevel` to your database's real compatibility level (130 = 2016, 140, 150, 160 = 2022).
- **NSSM** (https://nssm.cc) for the workers service only.
- Node.js 22.18+ only if you enable real-time co-editing (later).
- DNS for `admin.<domain>` and `*.<domain>`, and a wildcard TLS certificate imported into IIS.

## 2. Unpack and first install

```powershell
Expand-Archive raavi-0.24.0.zip C:\Deploy\raavi-0.24.0
# Copy application folders yourself (api, workers, migrator, web, collaboration, schema, sandbox-defaults, docs, scripts, release.json) into D:\Raavi — never replace an existing sandbox\.
powershell -ExecutionPolicy Bypass -File C:\Deploy\raavi-0.24.0\scripts\release\ConfigSync.ps1 `
  -InstallDirectory D:\Raavi -SkipMigrator
```

Creates `D:\Raavi\sandbox\{config,data,secrets}` when missing (`config` is initialised from the shipped defaults). The applications find `D:\Raavi\sandbox\config` by looking upwards from their own folder; set `RAAVI_SANDBOX_PATH` only if you put it elsewhere. **Back up `sandbox\` together with the database** (without `data\keys` the encrypted values — TOTP, AI keys, webhook secrets — cannot be read).

## 3. Settings (`D:\Raavi\sandbox\config\appsettings.json`)

| Key | Set to |
|---|---|
| `Raavi:Hosting:SystemHost` | `admin.example.com` |
| `Raavi:Hosting:TenantBaseDomains` | `["example.com"]` (workspaces become `<slug>.example.com`) |
| `Raavi:Hosting:PublicScheme` | `https` |
| `Raavi:Hosting:KnownProxies` | `["127.0.0.1"]` (the proxy connects from loopback; loopback is also trusted by default) |
| `Raavi:Database:CompatibilityLevel` | e.g. `160` |
| `notifications.json` → `Raavi:Email` | `Mode: Smtp` + SMTP settings; otherwise no notification e-mail |

Secrets are **not** written in files. They are environment variables of the API application pool and of the workers service (sections 5 and 6): `RAAVI_ConnectionStrings__System`, `RAAVI_Raavi__Database__CompatibilityLevel`, `ASPNETCORE_ENVIRONMENT=Production`. Only the migrator uses `RAAVI_ConnectionStrings__Migrator`.

Optional allow-lists (empty = feature off): `Raavi:Ai:AllowedHosts`, `Raavi:Integrations:AllowedWebhookHosts`, `Raavi:Integrations:AllowedConnectorHosts`, `Raavi:Migration:AllowedSourceHosts`.

### Issue trackers and Wiki ↔ Jira page links

- Set `Raavi:Integrations:AllowedConnectorHosts` to the Jira / GitHub Enterprise / GitLab host names (e.g. `jira.example.com` or `*.example.com`). Empty = connectors cannot be created or called (SSRF guard).
- **Lookup** in the editor (issue chip) works with an optional token (public data) or Browse access.
- **Create / link Jira issues from a wiki page** (Remote Link + Raavi-owned relationship) needs a **Jira** connector with an https token that can **Browse Projects**, **Create Issues** and **Link Issues**. GitHub/GitLab stay read-only.
- After upgrade the migrator creates `integrations.PageIssueLinks` / `PageIssueLinkContexts` and the integrations outbox; take a backup and stop services first, then run `ConfigSync.ps1`.

## 3b. One address for everything (no wildcard DNS/TLS) — path addressing and a default workspace

With `Addressing = Path` the workspaces live under **one host name** (`SystemHost`), so one DNS record and one certificate are enough:

| What | Address |
|---|---|
| default workspace (if configured) | `https://raavi.example.com/` |
| any workspace | `https://raavi.example.com/t/<slug>/` (e.g. `/t/dev/`) |
| administration area | `https://raavi.example.com/system/login` |

```json
"Hosting": {
  "SystemHost": "raavi.example.com",
  "Addressing": "Path",
  "PathPrefix": "t",
  "DefaultTenantCode": "DEVELOPMENT",
  "PublicScheme": "https",
  "KnownProxies": [ "127.0.0.1" ]
}
```

- `Addressing`: `Subdomain` (default: `slug.<base domain>`, needs wildcard DNS/TLS), `Path` (as above; `TenantBaseDomains` is then not needed) or `Both` (both are accepted; links use subdomains). Verified custom domains work in every mode.
- `DefaultTenantCode`: the **code** of the workspace that answers every request on the host that names no workspace (the root `/`). Empty = the root is the administration area, as before. When it is set the administration area is reached only through `/system/...` (its API is `/system/api/...`); if the code does not exist, the root answers "workspace not found" while `/system/login` keeps working.
- `PathPrefix` (default `t`) may be changed; `system`, `api`, `hubs`, `health`, `internal`, `assets` are refused.
- Links the system creates (invitations, e-mails, "open workspace") use `https://raavi.example.com/t/<slug>/` in `Path` mode.
- **IIS:** the `web.config` shipped in `web\` already proxies `t/<slug>/api|hubs|health` and `system/api` to the API with the original path; no extra IIS rule or binding is needed. Restart the API pool after changing the settings.

What you give up (the browser no longer separates workspaces): all workspaces share one browser origin, so **one session cookie** — a browser is signed in to one workspace (or the administration area) at a time; signing in to another replaces the session — and a cross-site-scripting flaw in one workspace could read another workspace's pages for the same signed-in user. Use `Path` only when all workspaces belong to organisations you trust equally (ADR-045).

## 4. Database and the migrator

1. Create an **empty** database (the migrator creates tables, not the database).
2. Run the migrator **from its own folder** (it finds the settings from the current directory) with a login that may create tables:

```powershell
cd D:\Raavi\migrator
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:RAAVI_Raavi__Database__CompatibilityLevel = '160'
$env:RAAVI_ConnectionStrings__Migrator = 'Server=SQLHOST;Database=Raavi;User Id=raavi_migrator;Password=...;TrustServerCertificate=true'
dotnet .\Raavi.Migrator.dll       # prints: Raavi database is up to date.
```
(Windows authentication: `Integrated Security=true` instead of user/password.) It is safe to run again.
3. Run `D:\Raavi\scripts\sql\01-database-roles.sql` and `02-logins.sql` (see `DEPLOYMENT_GUIDE.md` §4): they create the restricted runtime login `raavi_app`. The API and workers use **that** login, never the migrator login.
4. Use the FULL recovery model and schedule backups (`BACKUP_AND_RESTORE.md`, `D:\Raavi\scripts\backup`).

## 5. The API as an IIS site

1. **Application pool** `RaaviApi`: .NET CLR version **No Managed Code**; Start Mode `AlwaysRunning`; Idle Time-out `0`.
2. **Site** `RaaviApi`: physical path `D:\Raavi\api`, pool `RaaviApi`, binding **http, IP 127.0.0.1, port 5180** (internal only). The shipped `web.config` hosts the app in-process; no `--urls` is needed.
3. **Environment variables on the pool**, not in `web.config` (which is replaced on every upgrade):

```powershell
$a = "$env:windir\System32\inetsrv\appcmd.exe"
$cs = 'Server=SQLHOST;Database=Raavi;User Id=raavi_app;Password=...;TrustServerCertificate=true'
& $a set config -section:system.applicationHost/applicationPools /+"[name='RaaviApi'].environmentVariables.[name='ASPNETCORE_ENVIRONMENT',value='Production']" /commit:apphost
& $a set config -section:system.applicationHost/applicationPools /+"[name='RaaviApi'].environmentVariables.[name='RAAVI_Raavi__Database__CompatibilityLevel',value='160']" /commit:apphost
& $a set config -section:system.applicationHost/applicationPools /+"[name='RaaviApi'].environmentVariables.[name='RAAVI_ConnectionStrings__System',value='$cs']" /commit:apphost
```
4. **Permissions** for `IIS AppPool\RaaviApi`: read on `D:\Raavi\api`, `D:\Raavi\sandbox\config`, `D:\Raavi\sandbox\secrets`; **modify** on `D:\Raavi\sandbox\data` (the key ring is written there). With Windows authentication to SQL, run the pool under a domain/service account instead and grant that account SQL access.
5. Test from the server: `Invoke-WebRequest http://127.0.0.1:5180/health/ready -UseBasicParsing` → `Healthy`. For startup errors check the Windows Event Log, or set `stdoutLogEnabled="true"` temporarily in `D:\Raavi\api\web.config` (logs go to `D:\Raavi\api\logs`).

## 6. The workers as a Windows service (NSSM)

```powershell
$n = 'C:\Tools\nssm.exe'; $cs = '<same raavi_app connection string>'
& $n install RaaviWorkers "C:\Program Files\dotnet\dotnet.exe" "D:\Raavi\workers\Raavi.Workers.dll"
& $n set RaaviWorkers AppDirectory D:\Raavi\workers
& $n set RaaviWorkers AppEnvironmentExtra "ASPNETCORE_ENVIRONMENT=Production" "RAAVI_Raavi__Database__CompatibilityLevel=160" "RAAVI_ConnectionStrings__System=$cs"
New-Item -ItemType Directory -Force D:\Raavi\logs | Out-Null
& $n set RaaviWorkers AppStdout D:\Raavi\logs\workers.log
& $n set RaaviWorkers AppStderr D:\Raavi\logs\workers.err.log
& $n set RaaviWorkers AppRotateFiles 1
& $n set RaaviWorkers Start SERVICE_AUTO_START
Start-Service RaaviWorkers
```
`AppDirectory` must be the workers folder (that is how it finds `sandbox\config`). The service account needs the same permissions on `sandbox` as the pool. The workers are **required**: without them no notification/e-mail, no outbox events, no scheduled jobs. Check `workers.log` for errors after the start.

## 7. The public site (SPA + proxy)

1. One-time server settings (ARR proxy on, forwarded headers allowed):

```powershell
$a = "$env:windir\System32\inetsrv\appcmd.exe"
& $a set config -section:system.webServer/proxy /enabled:"True" /commit:apphost
& $a set config -section:system.webServer/rewrite/allowedServerVariables /+"[name='HTTP_X_FORWARDED_HOST']" /commit:apphost
& $a set config -section:system.webServer/rewrite/allowedServerVariables /+"[name='HTTP_X_FORWARDED_PROTO']" /commit:apphost
```
**Do not set `preserveHostHeader` on the server.** It is a server-wide ARR setting: other sites that proxy to a backend by an internal name (reverse-proxy sites on the same server) then send the public host name to their backend and answer 404 (this happened on the first installation). Raavi does not need it: the rule in `web.config` sends the original host and scheme as `X-Forwarded-Host` / `X-Forwarded-Proto`, and the API trusts them from the proxy on loopback (`KnownProxies`). If your server really lacks any other ARR site you may enable it, but it is not required.
2. **Site** `RaaviWeb`: physical path `D:\Raavi\web`, https bindings `admin.example.com` and `*.example.com` with the wildcard certificate (a pool with No Managed Code is enough).
3. `D:\Raavi\web\web.config` (shipped in the package, source `deployment/iis/web.config`) does the rest: proxies `api/`, `hubs/`, `health/` to `http://127.0.0.1:5180`, serves `index.html` for every other non-file URL, sets the Content-Security-Policy and caches only the hashed files in `assets/`. `/internal/*` is **not** proxied on purpose. If your API listens elsewhere, change the two URLs in that file (and re-apply that change after upgrades).
4. Never put `sandbox\` inside the web folder; the application refuses to start if it is inside a web root.

## 8. First start (default administrator)

1. In `D:\Raavi\sandbox\config\authentication.json` fill the bootstrap account (nothing is shipped by default; an empty value means "no bootstrap account"):

```json
"Bootstrap": { "SystemAdministrator": { "Email": "admin@vesta.local", "InitialPassword": "<temporary password>", "DisplayName": "Administrator" } }
```
   The "user name" is an e-mail-shaped address (it does not have to exist). Use a temporary password that is hard to guess — the site is reachable from the internet until you have signed in. Restart the API pool (`Restart-WebAppPool RaaviApi`); on start the API creates the account **only if no system administrator exists yet** (the log says "waiting for its first sign-in").
2. Open `https://admin.example.com/system/login`, type the e-mail and the temporary password (leave the code empty) and press sign in. You are taken to the setup page automatically: choose a **new password** (at least 12 characters), scan the QR code with an authenticator app, type the 6-digit code. You are signed in and the temporary password no longer works. (System administrators need the authenticator code afterwards, unless you turn two-step verification off with `Raavi:Authentication:SystemMfaRequired` = `false` in `authentication.json`: then there is no QR code or code at all and the password alone signs in. Keep it `true` when the system address is reachable from the internet.)
3. **Remove the `Bootstrap` values from `authentication.json`** (set them back to empty) and restart the pool. Create the first workspace and invite its administrator from the system pages.
4. If the temporary password is lost before the first sign-in, change it in the settings and restart the pool: while the account has not completed its first sign-in the configured password is applied again. Operator API keys and invitations (`Raavi:Security:SystemApiKeys`, `docs/api/IDENTITY_API.md`) remain available as an alternative.

## 9. Not needed / not included in this version

S3/MinIO, OpenSearch and Redis are not required for the implemented features (Redis connection string stays empty). Real-time co-editing needs the collaboration server (Node 22.18+, `npm ci --omit=dev` in `D:\Raavi\collaboration`, `sandbox\secrets\collaboration.env`, `Raavi:Collaboration:Enabled`, a signing key `.pem`, a ≥ 32-character service key on both sides, and one more NSSM service) — leave it off for the first start.

## 10. Upgrades

```powershell
Expand-Archive raavi-<new>.zip C:\Deploy\raavi-<new>
# Stop pool/services yourself, back up DB + sandbox\, copy application folders (keep sandbox\), then:
$env:RAAVI_ConnectionStrings__Migrator = 'Server=...;Database=Raavi;...'
powershell -ExecutionPolicy Bypass -File C:\Deploy\raavi-<new>\scripts\release\ConfigSync.ps1 `
  -InstallDirectory D:\Raavi
# Start pool/services yourself.
```

`ConfigSync.ps1` only runs `sandbox sync` (adds settings new in this version without changing yours) and the database migrator. File copy and service stop/start are your responsibility. Use `-SkipMigrator` if you will run the migrator by hand. If a migration failed half-way, restore the database from backup. Take a database backup and a copy of `sandbox\` before every upgrade.

## 11. Common problems

| Symptom | Look at |
|---|---|
| 500.30 / 502.5 from the API site | wrong .NET runtime (Hosting Bundle missing), a bad connection string, or `sandbox\config` not found; temporarily set `stdoutLogEnabled="true"` in `D:\Raavi\api\web.config` |
| `/health/ready` Unhealthy | database unreachable or the `raavi_app` login lacks rights (step 4.3) |
| Login works on `admin.` but a workspace host shows "not found" | `TenantBaseDomains`/`SystemHost` mismatch, or the proxy rule not sending `X-Forwarded-Host` (server variable not allowed in `allowedServerVariables`) |
| Browser shows the SPA but every API call fails | URL Rewrite/ARR not installed or proxy not enabled; test `https://admin.example.com/health/live` |
| Cookies not kept / endless login | `PublicScheme` not `https`, or the site is reached through a different host name than the one in settings |
| No notification e-mail, no events | the `RaaviWorkers` service is not running (`workers.err.log`) or e-mail `Mode` is `Disabled` |
