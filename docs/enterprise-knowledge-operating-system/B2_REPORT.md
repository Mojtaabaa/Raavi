# B2 Report — Knowledge Relationships

**Date:** 2026-11-11  
**Status:** Complete

## Capabilities completed

- SQL edge table `content.KnowledgeRelationships` with RLS  
- Types: RelatedTo, References, DependsOn, Documents, Implements, Supersedes  
- Origin: Manual / SystemGenerated / Integration; external target columns for Jira/Templar later  
- Create: Edit on source + View on target; List: omit neighbours without View  
- Delete: Manual only; never deletes Content entities  
- Incoming + outgoing on page panel with navigation  
- Outbox: `content.relationship_created` / `content.relationship_removed`  
- Trash cleanup of inbound edges (target FK Restrict for SQL Server cascade rules)

## APIs

| Method | Path |
|--------|------|
| GET | `/api/v1/content/{id}/relationships` |
| POST | `/api/v1/content/{id}/relationships` |
| DELETE | `/api/v1/content/{id}/relationships/{relationshipId}` |

## Database

Migration `20261111170039_KnowledgeRelationships` — additive. Source CASCADE, Target RESTRICT + app cleanup on trash.

## Tests

| Suite | Result |
|-------|--------|
| `KnowledgeRelationshipTests` | 2 passed |
| `ContentGovernanceTests` | 3 passed |
| `TenantIsolationModelTests` (+ related filter) | passed in combined run |

## Remaining

- B3 discovery filters (incl. relationship type)  
- Map Jira/label links as System/Integration edges  
- Graph visualization  
