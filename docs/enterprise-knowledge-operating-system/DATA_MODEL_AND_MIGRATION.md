# Data Model and Migration

## Rules

- Additive migrations preferred; no destructive drops without PO approval  
- Backfill then unique indexes (Labels pattern)  
- RLS + tenant FK on new tables  
- Preserve ContentJson history; never store tags/graph in Tiptap JSON  

## Proposed new structures (Stage B candidates)

| Object | Purpose | Risk |
|--------|---------|------|
| `KnowledgeRelationships` | Graph edges | Low–med |
| Page/asset meta (Owner, Classification, ReviewAt, ExpireAt) | Governance | Med — backfill Owner=CreatedBy |
| `SavedSearches` | Discovery | Low |
| `Decisions` / `Incidents` / `Systems` (+ providers) | Record types | Med — new routes/UI |
| Classification policy tables | Governance | Low |

## Mapping from existing

| Existing | Action |
|----------|--------|
| Content, Labels, Calendar, Tasks, … | Keep |
| PageIssueLinks | Project as Documents edges (view or sync job) |
| ContentLabels | Keep; optional TaggedWith projection |
| Activity / Outbox | Extend event types |

## Migration procedure (each Stage B slice)

1. Document current → target  
2. EF migration + isolation  
3. Backfill script idempotent  
4. Validation queries  
5. Down/rollback notes  
6. Test on LocalDB + representative restore  
7. Update WIKI_API + user-guide fa + IMPLEMENTATION_STATUS  

## Compatibility

- Keep `/labels` and page URLs  
- Space keys / content ids stable  
- Confluence import continues to Labels + Content  

## Non-goals for early Stage B

Rewriting PageVersions storage; moving attachments to S3 mid-flight without 7b plan; graph DB.