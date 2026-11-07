# ADR-014: Attachment Storage

**Status:** Accepted (bundled object store pending Q4) · **Date:** 2026-11-06

## Context
Attachments, images, collaboration snapshots, exports and import archives need scalable binary storage on-prem.

## Decision
- **S3 API** via AWSSDK.S3 against any S3-compatible store. MinIO for development (compose); production store chosen with the customer (licensing in `DEPENDENCY_AND_LICENSING.md`).
- Object keys: `tenants/{tenantId}/attachments/{attachmentId}/{fileVersionId}` (random ids, no file names in keys). Snapshots: `tenants/{tenantId}/collab/{documentId}/{seq}`. Exports: `tenants/{tenantId}/exports/{jobId}`.
- Metadata in SQL (`Attachments`, `FileVersions` with size, SHA-256, media type sniffed from content, scan status).
- Uploads stream through the API (validation, quota, MIME sniffing) — or, for very large files (Phase 7 decision), pre-signed multipart upload into a quarantine prefix followed by server-side validation before activation.
- Downloads: API checks `DownloadFiles`/`View` via `IPermissionEvaluator`, then streams or issues a pre-signed URL valid ≤ 120 s. Private bucket; no public URLs.
- Versioning: replacing an attachment adds a `FileVersion`; old versions retained per retention policy.
- Optional ClamAV scanning; files stay `Pending` (not downloadable except by uploader) until clean when scanning is enabled.
- Orphan cleanup job (objects without metadata after grace period) per tenant.

## Alternatives considered
- Database FILESTREAM — rejected: backup size, scalability, not cloud-native.
- Shared file system — supported later as an alternative `IObjectStorage` implementation for small installations.

## Consequences
Object storage must be backed up consistently with SQL (BACKUP_AND_RESTORE).
