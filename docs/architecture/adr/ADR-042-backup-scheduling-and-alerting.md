# ADR-042 — Backup scheduling, status and failure alerts (Phase 12f)

**Status:** Accepted (2026-11-08)

## Context
ADR-038 delivered backup/restore scripts but left "scheduling and alerting" open: a backup that nobody runs, or that fails silently, is the usual way to learn too late.

## Decisions
1. **`Invoke-ScheduledBackup.ps1`** is the only thing a scheduler calls. It runs `Backup-Raavi.ps1`, writes a status file per database and mode (`status/<db>-<mode>.json`: last success, last failure, last error, consecutive failures), a daily log, and exits 0/1. On failure — and on the first success after failures — it alerts through every configured channel, each best effort so that one broken channel hides neither the others nor the original error: a generic `-AlertScript` hook (pager/chat/ticket/authenticated mail), a webhook POST, unauthenticated SMTP, and the Windows Event Log (source "Raavi Backup", event 4101). Error text is scrubbed of `password/pwd/secret/token/key=…` and truncated before it reaches the log, the status file or an alert.
2. **`Test-BackupFreshness.ps1`** is the monitoring side: with maximum ages per mode it fails (exit 1) when a status file is missing (never ran), the last runs failed, the last success is too old, the backup file is gone, or its size no longer matches its manifest; exit 2 for usage errors. It detects "the scheduler stopped" which the wrapper alone cannot.
3. **`Register-RaaviBackupTasks.ps1`** creates Windows Task Scheduler tasks (full daily 02:00, differential every 6 h, optional log every 15 min, retention 14 days, compression, no overlapping runs, 6 h limit), supports `-WhatIf`, `-Replace`, `-Unregister`, never overwrites an existing task silently.
4. **Self-test** `Test-BackupSchedule.ps1` (throw-away databases, a real task registration that it removes): 21 checks.

## Not covered
Linux cron / SQL Agent definitions (the wrapper works as their command, but no registration script); tasks running as SYSTEM or a service account were not tried (registered for the current user only); off-site copy and immutability; the alert channels other than the script hook were not exercised against real endpoints (webhook, SMTP, Event Log); encrypted backups on a real edition; long-term retention policy beyond a simple age; automatic restore drill scheduling (run `Test-RestoreDrill.ps1` quarterly by hand or register it yourself).
