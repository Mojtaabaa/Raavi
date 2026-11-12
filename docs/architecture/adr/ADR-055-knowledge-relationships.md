# ADR-055 — Knowledge relationships (SQL edge store)

## Status

Accepted — EKOS Stage B2

## Context

EKOS needs typed links between knowledge assets without a separate graph database. Specialized links (labels, Jira) already exist; a general edge table must coexist and stay extensible for external systems.

## Decision

1. Store edges in **`content.KnowledgeRelationships`** (SQL Server, RLS, tenant composite FKs).  
2. B2 supports Content→Content for types: RelatedTo, References, DependsOn, Documents, Implements, Supersedes.  
3. Columns `TargetKind`, `TargetExternalKey`, `TargetTitle`, `SourceSystem`, `Origin` enable Jira/Templar later without schema rewrite.  
4. `Origin`: Manual (user-editable/deletable), SystemGenerated / Integration (not user-deletable in B2).  
5. Authorization: Edit on source + View on target to create; View both to list; deleting an edge never deletes Content.  
6. Outbox: `content.relationship_created` / `content.relationship_removed`.

## Alternatives

- Neo4j — rejected for ops cost (graph design doc).  
- Only reuse PageIssueLinks — too narrow.

## Consequences

Page UI gains a relationships panel. Discovery filters by relationship type arrive in B3.
