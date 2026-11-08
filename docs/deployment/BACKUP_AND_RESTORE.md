# Backup and Restore

> **Status:** Strategy defined in Phase 0. **SQL Server scripts and the restore drill exist and are tested (Phase 12b, ADR-038)**; scheduling, secrets backup, object storage / OpenSearch / Redis and a full-installation drill are not covered yet (see the end of this page).

| Asset | Method | Frequency (default) | Notes |
|---|---|---|---|
| SQL Server | Full + differential + log backups, `WITH ENCRYPTION` (certificate stored outside the backup location) | Full daily, diff 6 h, log 15 min | System of record incl. Hangfire and collaboration updates |
| Object storage | Bucket replication/versioning or snapshot of the storage volume; consistency point recorded after SQL backup | Daily + continuous versioning | Restore must be ≥ SQL point in time (orphan objects are cleaned automatically; missing objects are not) |
| Collaboration snapshots | Part of SQL + object storage | — | Y updates are in SQL |
| Configuration (`sandbox/config`) | Version-controlled deployment repository | On change | No secrets inside |
| Secrets / keys | Secret store backup (Data Protection key ring, collaboration ticket keys, tenant data-key master key, SQL backup certificate) | On change | Store separately from data backups — without them encrypted data is unrecoverable |
| Redis/Valkey | Not backed up by default (cache, ephemeral coordination). Optional AOF for faster warm-up | — | Rebuilt from SQL |
| OpenSearch | Rebuild via reindex jobs (authoritative data in SQL); optional snapshot repository to S3 for faster recovery | Optional daily | |

## SQL Server scripts (`deployment/scripts/backup/`)

```powershell
# 1. Back up (run on a schedule; add -EncryptionCertificate for encrypted backups on Standard/Enterprise)
.\Backup-Raavi.ps1 -ServerInstance sql01 -Database Raavi -BackupDirectory D:\backups\raavi -Mode Full
.\Backup-Raavi.ps1 -ServerInstance sql01 -Database Raavi -BackupDirectory D:\backups\raavi -Mode Differential
.\Backup-Raavi.ps1 -ServerInstance sql01 -Database Raavi -BackupDirectory D:\backups\raavi -Mode Log        # needs the FULL recovery model

# 2. Restore into a NEW database (side database): chain = manifests in order, optional point in time (UTC)
.\Restore-Raavi.ps1 -ServerInstance sql02 -TargetDatabase Raavi_side -Backups full.bak.json, diff.dif.json, log1.trn.json -StopAt '2026-11-08T10:15:00Z'

# 3. Quarterly drill (copy-only backup -> restore to <db>_drill_<ts> -> CHECKDB -> counts + structure -> drop); exit code 0 = passed
.\Test-RestoreDrill.ps1 -ServerInstance sql01 -SourceDatabase Raavi -WorkDirectory D:\drills
```

Rules built into the scripts: every backup has `CHECKSUM`, is verified and gets a manifest with a SHA-256 that the restore checks first; a restore never touches an existing database unless `-Replace` is combined with `-ConfirmOverwrite <exact name>`; backups are never overwritten; retention (`-RetentionDays`) deletes only files the script itself wrote. `Test-BackupScripts.ps1` is the self-test of all of this on a throw-away database (16 checks).

**Production settings to decide:** the recovery model (FULL is required for log backups and point in time), the schedule (strategy table above), the backup certificate and where its private key is kept (outside the backups!), the off-site copy, and who is alerted when a script exits non-zero.

**Measured once** (development database, LocalDB, local disk): 66 tables, 5 530 rows → backup 4.9 s / 131 MB, restore 5.1 s, CHECKDB 1.9 s, drill passed including the row-level-security policy. This is a lower bound of the RTO, not an RTO.

## Restore order

1. Secrets/keys → 2. SQL Server (point in time) → 3. object storage (same or later point) → 4. start api/workers in maintenance mode → 5. trigger full reindex per tenant → 6. warm caches → 7. leave maintenance mode.

## Tenant-level export / recovery

The workspace export (Phase 12a, `GET /tenant/export`) produces a JSON + attachments archive of what the requesting administrator may view; run it against a restored side database to recover content of one tenant. Importing an export into another installation is not built yet. Point-in-time recovery of a single tenant from a full database backup is done by restoring to a side database and running the tenant export from it.

## Testing

Implemented: `Test-BackupScripts.ps1` (scripts) and `Test-RestoreDrill.ps1` (a real database). Not yet: a drill that also starts the API, workers and collaboration server against the restored copy and runs the isolation and smoke suites.

Restore drills (quarterly by default) restore into an isolated environment and run the isolation and smoke test suites against it.
