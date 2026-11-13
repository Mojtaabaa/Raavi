# Tags Search Design

## Stack

Use existing SQL Discovery + AI smart search. Do **not** add OpenSearch for tags (Phase 7c deferred).

## APIs

1. **Suggest** — `GET /labels?prefix=` → enriched DTOs (slug, displayName, color); view-filtered usage.
2. **Pages by labels** — `GET /labels/content?tags=a,b&tagMode=any|all&includeDescendants=bool&spaceKey=&skip=&take=`  
   Resolve slugs/aliases → id set (expand descendants if requested) → join ContentLabels → Current pages → `FilterViewableAsync` → page.
3. **Tag detail** — `GET /labels/{slug}` — metadata, children, related (co-occurrence on viewable pages), counts.
4. **Smart search** — optional `labels`, `tagMode`, `includeDescendants` on existing search path: intersect candidate ids with ContentLabels before return.

## Semantics

| Mode | Meaning |
|------|---------|
| any (OR) | ≥1 selected tag |
| all (AND) | every selected tag |
| includeDescendants | expand each selected tag to subtree |

Never include descendants implicitly. Counts/facets only on viewable pages.

## Indexes

`(TenantId, LabelId)` on ContentLabels (exists); `(TenantId, NormalizedName)`; `(TenantId, Slug)`; alias NormalizedAlias; ParentId+DisplayOrder.

## Indexing

Include label DisplayNames in `IndexablePage` plain text when building chunks so keyword RAG can hit them; still filter at query time.
