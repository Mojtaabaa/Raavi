# Knowledge Governance

## Already in product

- Space/page permissions + restrictions  
- Publish + page approvals (All/Any)  
- Workflows on outbox (add label, notify)  
- Stale-page trigger + reminders  
- Activity / audit outbox  
- Label archive/merge admin  

## B1 shipped (2026-11-11)

Table `content.ContentGovernance` (1:1 with Content; ADR-054). Fields: OwnerUserId, ReviewOwnerUserId, ReviewAt, LastReviewedAt, Classification (Public/Internal/Confidential/Restricted, extensible), LifecycleStatus (stewardship; separate from Content.Status). APIs: `PUT /content/{id}/governance`, `GET /content/review-due`. Owner ≠ ACL. Restricted bodies excluded from external-model Ask context via `IViewableContentFilter.FilterForExternalModelAsync`. Retention / legal hold: extension points only (not implemented).

## Remaining gaps

| Capability | Approach |
|------------|----------|
| Review policy engine | Days / event-based scheduling via Hangfire + workflow triggers |
| Classification in export/share paths | Extend beyond Ask; B3 search filters |
| Retention / legal hold | Hold flag blocks purge; retention job — **PO legal review** |
| Quality rules | Configurable checks → dashboard + tasks |
| Stewardship dashboards | Knowledge Analytics |

## Policy principle

Policies are **data + workflow config**, not hardcoded status machines. Reuse ADR-031/032/033 patterns.

## Enforcement surfaces

Page access, search, AI context, attachments, export, notifications, background purge, connectors.