# B1 Migration Plan — Content Governance Metadata

**Date:** 2026-11-11  
**Risk:** Low (additive only; no drops; no data deletion)

## 1. Current state

| Object | Schema | Role |
|--------|--------|------|
| `content.Content` | ContentNode | Title, tree, `Status` (Draft/Current/Archived/Trashed), CreatedBy/UpdatedBy, import provenance, Revision |
| Permissions | `security.PermissionGrants`, `ContentRestrictions` | Space + page ACL via WikiAuthorizer |
| Users | `identity.Users` | Referenced by Guid only from Wiki (no FK across modules typically) |

**Gap:** No Owner, ReviewAt, Classification, or governance lifecycle separate from `Content.Status`.

## 2. Target change

New table **`content.ContentGovernance`** (1:1 with Content):

| Column | Type | Notes |
|--------|------|-------|
| Id | uniqueidentifier | **Same as Content.Id** |
| TenantId | uniqueidentifier | RLS |
| OwnerUserId | uniqueidentifier | Knowledge owner (≠ admin permission) |
| ReviewOwnerUserId | uniqueidentifier NULL | Who should review |
| ReviewAt | datetimeoffset NULL | Next review due |
| LastReviewedAt | datetimeoffset NULL | |
| Classification | varchar(32) | Public\|Internal\|Confidential\|Restricted (extensible string) |
| LifecycleStatus | varchar(32) | Draft\|Current\|InReview\|Approved\|Archived\|Expired (governance; **not** Content.Status) |
| UpdatedBy | uniqueidentifier | |
| UpdatedAt | datetimeoffset | |

Constraints/indexes:

- PK nonclustered Id; clustered AK (TenantId, Id)  
- FK (TenantId, Id) → Content (TenantId, Id) ON DELETE CASCADE (or Restrict + app delete) — **Cascade** so purge of content removes meta  
- FK TenantId → system.Tenants  
- Indexes: (TenantId, OwnerUserId), (TenantId, ReviewAt), (TenantId, Classification), (TenantId, LifecycleStatus)  
- `AddTenantIsolation("content", "ContentGovernance")`

## 3. Compatibility / backfill

```sql
INSERT INTO content.ContentGovernance (Id, TenantId, OwnerUserId, Classification, LifecycleStatus, UpdatedBy, UpdatedAt)
SELECT c.Id, c.TenantId, c.CreatedBy, 'Internal',
  CASE c.Status WHEN 4 THEN 'Draft' WHEN 2 THEN 'Archived' ELSE 'Current' END,
  c.UpdatedBy, c.UpdatedAt
FROM content.Content c
WHERE NOT EXISTS (SELECT 1 FROM content.ContentGovernance g WHERE g.Id = c.Id);
```

- Existing pages keep working; DTO gains governance fields  
- Default Classification = **Internal**  
- Owner = CreatedBy (does not change ACL)  
- ReviewAt / LastReviewedAt / ReviewOwner null  

## 4. Rollback

Migration `Down`: DropTenantIsolation → drop table. Content untouched.

## 5. Retention / Legal Hold

Columns **not** added in B1 (extension points documented in ADR only).

## 6. Approval

Additive side table → proceed per Stage B instructions (no destructive change).