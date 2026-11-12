# ADR-054 — Content governance metadata (side table)

## Status

Accepted — EKOS Stage B1

## Context

EKOS needs ownership, review scheduling, classification, and a governance lifecycle without bloating `content.Content` or conflating wiki `ContentStatus` (Draft/Current/Archived/Trashed) with stewardship states.

## Decision

1. Store governance on **`content.ContentGovernance`**, 1:1 with Content (`Id` = ContentId).  
2. Classification is an extensible string; initial values Public, Internal, Confidential, Restricted. It does **not** replace WikiAuthorizer permissions.  
3. Owner / ReviewOwner are stewardship fields only — changing them never grants or revokes ACL.  
4. ReviewAt reaching due does not expire or delete content.  
5. Retention / legal hold are **not** implemented in B1 (extension points only).  
6. Emit outbox events on governance changes (`content.governance_*`).

## Alternatives

- Columns on Content — rejected (pollutes core; harder to extend).  
- Separate Decision/Incident tables — deferred; use templates in later slice.

## Consequences

Every new Content row gets a governance row (create path + backfill). Search/AI/export must eventually honour classification without bypassing View.