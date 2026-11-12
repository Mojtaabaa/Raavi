# B4 Report — Event and Integration Coverage

**Date:** 2026-11-11  
**Status:** Complete (reuses existing outbox; no parallel framework)

## Events emitted (same Wiki outbox + ActivityProjection)

| Event type | When |
|------------|------|
| `content.owner_changed` | OwnerUserId changes |
| `content.review_scheduled` | ReviewAt set/changed |
| `content.reviewed` | MarkReviewed |
| `content.classification_changed` | Classification changes |
| `content.governance_updated` | Any governance PUT |
| `content.relationship_created` | Manual edge created |
| `content.relationship_removed` | Manual edge removed |

## Reliability (existing ADR-023 pipeline)

- Idempotent ActivityProjection (entry id = outbox id)  
- Tenant + actor on WikiEvent payload  
- Retry via OutboxDispatcher (unchanged)  
- Correlation via outbox message id  

## Not added

Separate event bus, Hangfire review reminders (optional later), notification fan-out for review due (optional).
