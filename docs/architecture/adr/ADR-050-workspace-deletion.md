# ADR-050 — Workspace (tenant) deletion with a grace period and purge

**Status:** Accepted (2026-11-10)

## Context
Phase 12 listed "tenant deletion with retention and purge" as open. The status values `PendingDeletion` and `Deleted` already existed but nothing used them. Requirements: deleting a workspace must be deliberate (no accidental click), reversible for a while, final afterwards, must never touch another workspace (RLS has no bypass), and must leave the code and slug unusable so an address cannot be taken over.

## Decisions
1. **Two steps.** `POST /system/tenants/{id}/delete-request` (reason + the workspace code typed by the administrator) moves a Provisioning/Active/Suspended/Deactivated tenant to `PendingDeletion`: access stops at once (the directory only serves `Active`), nothing is removed, `DeletionScheduledFor = now + Raavi:Tenants:DeletionGraceDays` (default 14, 0–365). `POST …/cancel-deletion` returns it to the status it had (`StatusBeforeDeletion`; a never-activated tenant returns as Deactivated). Activation is **not** a way out of PendingDeletion.
2. **Purge** (`POST …/purge` with the code, or the daily system job `TenantPurgeJob` for every tenant whose date has passed, ≤ 50 per run) works inside an explicit tenant scope for the doomed tenant — the same RLS-bound connection as any tenant request, so it can only see and delete that tenant's rows. It lists every base table that has a `TenantId` column (INFORMATION_SCHEMA, so new modules are covered without code) except schemas `audit`, `system`, `hangfire`, adds `system.TenantSettings/TenantBranding/TenantLogos`, and runs `DELETE … WHERE TenantId = @t` per table in up to 8 passes; a foreign-key error (547) only postpones that table to the next pass (child tables go first). If something is left, the tenant stays `PendingDeletion`, an audit row `tenant.purge_incomplete` is written and the job retries the next day.
3. **Tombstone.** The `Tenants` row stays with status `Deleted` and `DeletedAt`; code and slug remain unique-reserved; its domains are marked removed (they enter the usual quarantine). System and tenant audit rows are kept (the evidence that the deletion happened and who asked: `tenant.deletion_requested`, `…_cancelled`, `tenant.purged` with table and row counts).
4. **UI** (system host, "Workspaces"): Delete workspace → dialog with the consequences, a reason and the code to type; a pending workspace shows its purge date, "Cancel deletion" and "Delete permanently now" (code again); deleted rows have no actions.

## Not covered / limits
- **Users are global**: the purge removes the workspace's memberships and everything it owned, but user accounts (and their sessions in other workspaces) are kept — a person may belong to several workspaces. A user who belonged only to the deleted workspace remains as an account without membership; a general "orphaned user cleanup" is not built.
- No four-eyes rule (a second system administrator approving); one administrator can request and purge.
- No automatic export before purge (use the Phase 12a export first); no backup is touched — the data lives on in older backups until they expire.
- Attachments and everything else are in SQL Server, so nothing outside the database needs cleaning today; if files move to disk this job must learn that.
- Large tenants delete in single statements per table (command timeout 15 min); log growth is not throttled.
- Only tested against SQL Server through the API with a wiki space; the other modules rely on the generic TenantId scan.
