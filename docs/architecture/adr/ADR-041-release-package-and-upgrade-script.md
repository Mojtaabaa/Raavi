# ADR-041 — Release package and upgrade script (Phase 12d)

**Status:** Accepted (2026-11-08)

## Context
ADR-040 made `sandbox/` the home of everything an installation keeps, but nothing produced a deployable package that leaves it out, and nothing applied one safely.

## Decisions
1. **`New-ReleasePackage.ps1 -Version x`** builds a folder + zip whose layout mirrors the installation (`api/ workers/ migrator/ web/ collaboration/ schema/ sandbox-defaults/ docs/ scripts/`, `release.json` with version, commit, UTC time and SHA-256 of every file). It fails if the package would contain `sandbox`, `secrets`, a key ring, `*.Local.json`, `*.local` or `collaboration.env`, and never overwrites an existing package. Because the layout mirrors the repository, the existing walk-up lookup finds `<install>/sandbox` and `<install>/sandbox-defaults`, and the collaboration server finds `../../schema` and `../../sandbox/secrets`.
2. **`ConfigSync.ps1 -InstallDirectory i`** (supersedes `Update-Raavi.ps1`): operators stop services and copy application folders themselves. The script only creates `sandbox/{config,data,secrets}` when missing (first install: `config` from `sandbox-defaults`), runs `Raavi.Migrator sandbox sync`, and unless `-SkipMigrator` applies database migrations. It supports `-WhatIf` and never touches existing sandbox values except by adding missing keys/files.
3. **Self-test** `Test-ReleaseScripts.ps1` builds a real package, copies folders as an operator would, then runs `ConfigSync.ps1`: administrator value kept, new setting and missing file added, key ring and secrets byte-identical, missing migrator refused.

## Addendum (0.13.2): IIS hosting of the API
The API runs as an IIS site (in-process, internal binding 127.0.0.1:5180, environment variables on its application pool so that upgrades do not overwrite them); only the workers use a Windows service (NSSM). The public site serves `web/` and proxies `api/`, `hubs/`, `health/` with ARR (host header preserved); its `web.config` ships in the package (source `deployment/iis/web.config`: proxy rules, SPA fallback, CSP, long cache only for hashed `assets/`, never for `index.html`; `/internal/*` is not proxied). Stop/start of pools and services is operator-owned (not in `ConfigSync.ps1`).

## Addendum (0.24.x): ConfigSync replaces Update-Raavi
Full package verify, rollback copy, folder replace, npm ci, and `-AppPool`/`-Service` were removed from the shipped script. Use `ConfigSync.ps1` after a manual folder replace; helper `ConfigSync-Server.cmd` targets `C:\inetpub\Raavi`.

## Not covered
Rollback is a manual copy back of `previous/<ts>` (database restore is the backup/restore scripts' job — a migration that already ran is not undone). Service stop/start and IIS/Windows-service/systemd/Docker integration are not scripted. The frontend build is skipped in the self-test (`-WithFrontend` includes it; not run). Packages are not signed. Linux/PowerShell 7 was not tried (Windows PowerShell 5.1 only). Self-contained .NET publish is not used: the target needs the .NET 10 ASP.NET runtime and Node 22+ (for `process.loadEnvFile`, 20.12+).
