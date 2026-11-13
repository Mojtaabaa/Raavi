# Domain Model (Target)

## Principle

Do **not** force every knowledge thing into one table. Share **contracts** (metadata, permissions, search indexing, relationships) across concrete entities.

## Knowledge Asset (logical)

A Knowledge Asset is any tenant-scoped entity that participates in search, graph, governance, and AI.

| Asset family | Concrete today / tomorrow | Module |
|--------------|---------------------------|--------|
| Wiki page | `ContentNode` + `PageVersion` | Wiki |
| Task / DB row / Slide / Board | Existing entities | Wiki |
| Calendar event | `CalendarEvent` | Wiki |
| Label | `Label` | Wiki |
| Decision / Incident / Catalog entry | **New** content types or tables | Wiki |
| Jira issue (linked) | External + `PageIssueLink` | Integrations |
| Prompt | Prompt Hub | AI |

## Shared metadata (target abstraction)

Logical fields applied per asset type (columns or side table `content.KnowledgeAssetMeta` keyed by `(EntityType, EntityId)` — **decision for Stage B PO**):

- OwnerUserId, StewardUserId  
- LifecycleState (Draft/Current/InReview/Approved/Archived/Expired)  
- Classification (extensible: Public/Internal/Confidential/Restricted)  
- ReviewAt, ExpireAt  
- SourceSystem / SourceId (already on Content for import)

**Default recommendation:** additive columns / side table for pages first; other types opt-in — avoid big-bang universal table.

## Relationships (target)

New `content.KnowledgeRelationships` (relational graph):

- TenantId, Id  
- FromType, FromId, ToType, ToId  
- RelationType (controlled enum/string registry)  
- Provenance: Manual | System | Integration  
- SourceSystem, CreatedBy, CreatedAt  
- Optional Strength / Note  

Permission: traverse only if caller can View both ends (or redact node).

## Existing relationship seeds to map

| Today | Maps to |
|-------|---------|
| ContentClosure | Tree BelongsTo (structural, keep as-is) |
| ContentLabels | TaggedWith |
| PageIssueLinks | Documents / RelatesTo Issue |
| Calendar LinkedPage/Whiteboard | DiscussedIn / Documents |
| Editor links / mentions | References (extract later) |

## Ownership of truth

| Entity | Authority |
|--------|-----------|
| Page body, Raavi relationships, decisions, incident knowledge | Raavi |
| Jira issue fields / status | Jira |
| Templar analytical records | Templar |
| External calendar events (when synced) | Provider per sync policy |