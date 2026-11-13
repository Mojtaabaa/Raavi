# B2 Migration Plan — Knowledge Relationships

**Date:** 2026-11-11  
**Risk:** Low (additive table; no Content column drops; no data deletion)

## 1. Current state

No general relationship table. Specialized links exist (ContentLabels, PageIssueLinks/Jira, calendar links) and stay as-is. Graph design: SQL edge store (`KNOWLEDGE_GRAPH_DESIGN.md`).

## 2. Target change

New table **`content.KnowledgeRelationships`**:

| Column | Type | Notes |
|--------|------|-------|
| Id | uniqueidentifier | PK |
| TenantId | uniqueidentifier | RLS |
| SourceContentId | uniqueidentifier | FK → Content |
| RelationshipType | varchar(32) | RelatedTo, References, DependsOn, Documents, Implements, Supersedes (+ extensible) |
| TargetKind | varchar(32) | `Content` now; `ExternalIssue` / others later |
| TargetContentId | uniqueidentifier NULL | When TargetKind=Content |
| TargetExternalKey | varchar(256) NULL | When external (e.g. `jira:PROJ-1`) |
| TargetTitle | nvarchar(500) NULL | Display snapshot for external / offline |
| Origin | varchar(32) | Manual \| SystemGenerated \| Integration |
| SourceSystem | varchar(64) NULL | Connector id when Integration |
| CreatedBy | uniqueidentifier | |
| CreatedAt | datetimeoffset | |

Constraints:

- FK (TenantId, SourceContentId) → Content ON DELETE CASCADE (edge dies with source; target entity untouched)  
- FK (TenantId, TargetContentId) → Content ON DELETE CASCADE when set (edge dies if target purged; does not cascade-delete other content)  
- Check: Content targets require TargetContentId; external require TargetExternalKey  
- Unique filtered indexes to prevent duplicate edges  
- `AddTenantIsolation` + Tenant FK  

## 3. Compatibility

No backfill required. Existing pages unchanged. Jira panel remains; mapping to system edges deferred.

## 4. Rollback

`Down`: DropTenantIsolation → drop table.

## 5. Security behaviour (app, not DB)

Create: CanEdit source + CanView target (Content). List: View both ends or omit neighbour. Delete: Manual only (SystemGenerated/Integration not user-editable without future policy).
