# Backup and Restore

> **Status:** Strategy defined in Phase 0; scripts and restore drills are Phase 12 deliverables. Nothing here has been tested yet.

| Asset | Method | Frequency (default) | Notes |
|---|---|---|---|
| SQL Server | Full + differential + log backups, `WITH ENCRYPTION` (certificate stored outside the backup location) | Full daily, diff 6 h, log 15 min | System of record incl. Hangfire and collaboration updates |
| Object storage | Bucket replication/versioning or snapshot of the storage volume; consistency point recorded after SQL backup | Daily + continuous versioning | Restore must be ≥ SQL point in time (orphan objects are cleaned automatically; missing objects are not) |
| Collaboration snapshots | Part of SQL + object storage | — | Y updates are in SQL |
| Configuration (`sandbox/config`) | Version-controlled deployment repository | On change | No secrets inside |
| Secrets / keys | Secret store backup (Data Protection key ring, collaboration ticket keys, tenant data-key master key, SQL backup certificate) | On change | Store separately from data backups — without them encrypted data is unrecoverable |
| Redis/Valkey | Not backed up by default (cache, ephemeral coordination). Optional AOF for faster warm-up | — | Rebuilt from SQL |
| OpenSearch | Rebuild via reindex jobs (authoritative data in SQL); optional snapshot repository to S3 for faster recovery | Optional daily | |

## Restore order

1. Secrets/keys → 2. SQL Server (point in time) → 3. object storage (same or later point) → 4. start api/workers in maintenance mode → 5. trigger full reindex per tenant → 6. warm caches → 7. leave maintenance mode.

## Tenant-level export / recovery

Per-tenant export job (JSON + attachments archive) supports moving or restoring a single tenant into another installation; importing a tenant export is a Phase 12 capability. Point-in-time recovery of a single tenant from a full database backup is done by restoring to a side database and running the tenant export from it.

## Testing

Restore drills (quarterly by default) restore into an isolated environment and run the isolation and smoke test suites against it.
