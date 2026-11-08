# ADR-038 — Backup, restore and restore-drill scripts (Phase 12b)

**Status:** Accepted (2026-11-08)

## Context

`docs/deployment/BACKUP_AND_RESTORE.md` defined a strategy in Phase 0 and said that scripts and restore drills were Phase 12 deliverables, "nothing tested yet". SQL Server is the system of record (content, collaboration state, Hangfire, outbox), so the first deliverable is a tested path for it. Object storage, Redis and OpenSearch have nothing to back up in this installation yet (7b/7c deferred).

## Decisions

1. **Three PowerShell scripts, no modules** (`deployment/scripts/backup/`, Windows PowerShell 5.1 compatible, System.Data.SqlClient only, UTF-8 with BOM):
   - `Backup-Raavi.ps1` — full / differential / log backup with `CHECKSUM`, verification (`RESTORE VERIFYONLY`), optional AES-256 encryption with a server certificate, optional compression, optional **copy-only**, and a **manifest** (`<file>.json`: database, mode, times, size, SHA-256, LSNs, server version, flags). File names carry a millisecond timestamp and the script refuses to overwrite an existing backup. Retention deletes only files this script wrote (manifest says so, database matches).
   - `Restore-Raavi.ps1` — restores a chain (full → optional differential → optional logs) into a **named target**. It checks every file against its manifest hash first, validates the chain order, never touches an existing database unless `-Replace` is combined with `-ConfirmOverwrite <exact name>`, supports point-in-time (`-StopAt`, given in UTC and converted to the server's local time, which is what `RESTORE LOG` compares), and relocates files to the instance defaults as `<target>_<logical>.<ext>`.
   - `Test-RestoreDrill.ps1` — the drill: copy-only full backup → restore into a **new** `<db>_drill_<timestamp>` → `DBCC CHECKDB` → comparison of row counts per table and of structure (tables, **row-level-security policies**, indexes, foreign keys) with the source → drop the drill database (only this run's own, matched by exact pattern) → `drill-report-*.json` with timings → exit code 0/1. The drill uses copy-only backups so it can run at any time without becoming the base of the real differential chain.
2. **Self-test** (`Test-BackupScripts.ps1`) creates its own throw-away database (full recovery, two tables, a security policy), exercises every path above — chain restore, point in time, refusals, damaged-file detection, SIMPLE-recovery log refusal, SQL-injection-style names, retention that keeps strangers' files — and drops everything it created. It found and fixed three real defects while being written: timestamp collisions overwriting a backup (now millisecond names plus an overwrite refusal), `STOPAT` being compared in server-local time, and a drill backup that would have replaced the differential base (now copy-only).
3. **Identifiers are never concatenated blindly:** database and certificate names must match `^[A-Za-z0-9_.-]{1,128}$` and are bracket-quoted; file paths are N-literals with doubled quotes.
4. **What is measured** (LocalDB, local disk, development database of 66 tables / 5 530 rows): backup 4.9 s for 131 MB, restore 5.1 s, CHECKDB 1.9 s — a lower bound of a real RTO, which also includes copying the backup, restoring secrets and starting the services (see the restore order in `BACKUP_AND_RESTORE.md`).

## Not covered

Scheduling (SQL Agent / Task Scheduler / cron jobs that call the scripts with the frequencies of the strategy), off-site copy and immutability, backup encryption was **not** exercised (LocalDB/Express cannot encrypt backups; the code path exists but is untested), restore of secrets/Data Protection key ring, backup of object storage / OpenSearch / Redis (not in use), restore into a different SQL Server version or edition, automatic tenant-level recovery from a side database (the tenant export runs on a restored copy by hand), alerting when a backup or drill fails, and a full-installation drill with the API, workers and collaboration server started against the restored database.
