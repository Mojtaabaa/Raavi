# ADR-041 — Release package and upgrade script (Phase 12d)

**Status:** Accepted (2026-11-08)

## Context
ADR-040 made `sandbox/` the home of everything an installation keeps, but nothing produced a deployable package that leaves it out, and nothing applied one safely.

## Decisions
1. **`New-ReleasePackage.ps1 -Version x`** builds a folder + zip whose layout mirrors the installation (`api/ workers/ migrator/ web/ collaboration/ schema/ sandbox-defaults/ docs/ scripts/`, `release.json` with version, commit, UTC time and SHA-256 of every file). It fails if the package would contain `sandbox`, `secrets`, a key ring, `*.Local.json`, `*.local` or `collaboration.env`, and never overwrites an existing package. Because the layout mirrors the repository, the existing walk-up lookup finds `<install>/sandbox` and `<install>/sandbox-defaults`, and the collaboration server finds `../../schema` and `../../sandbox/secrets`.
2. **`Update-Raavi.ps1 -PackageDirectory p -InstallDirectory i`** verifies every file hash, refuses a package that contains `sandbox/` or an install directory equal to the package, refuses to run while a file of `api/`, `workers/` or `migrator/` is locked (a service is still running), copies the current application folders to `previous/<timestamp>/` (rollback copy, never overwritten), replaces only the application folders that the package lists, creates `sandbox/{config,data,secrets}` when missing (first install: `config` from `sandbox-defaults`), runs `Raavi.Migrator sandbox sync`, restores collaboration dependencies (`npm ci --omit=dev`, kept when the lock file is unchanged) and with `-RunMigrator` applies database migrations. It supports `-WhatIf`, never starts services and never touches `sandbox/` except by adding missing items.
3. **Self-test** `Test-ReleaseScripts.ps1` builds a real package and runs a first install and an upgrade in a temp folder: 22 checks (administrator value kept, new setting and missing file added, key ring and secrets byte-identical, rollback copy present and without sandbox, damaged/sandbox-containing/same-folder packages refused).

## Not covered
Rollback is a manual copy back of `previous/<ts>` (database restore is the backup/restore scripts' job — a migration that already ran is not undone). Service stop/start and IIS/Windows-service/systemd/Docker integration are not scripted. The frontend build is skipped in the self-test (`-WithFrontend` includes it; not run). Packages are not signed. Linux/PowerShell 7 was not tried (Windows PowerShell 5.1 only). Self-contained .NET publish is not used: the target needs the .NET 10 ASP.NET runtime and Node 22+ (for `process.loadEnvFile`, 20.12+).
