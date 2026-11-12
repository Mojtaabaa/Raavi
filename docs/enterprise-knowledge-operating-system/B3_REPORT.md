# B3 Report — Unified Discovery Filters

**Date:** 2026-11-11  
**Status:** Complete (no schema migration)

## Capabilities

- `IDiscoveryContentFilter` + Wiki `DiscoveryContentFilterService` (batch, no N+1)  
- Smart search `GET /ai/search` accepts: ownerUserId, reviewOwnerUserId, classification, lifecycleStatus, reviewDue, relationshipType, contentType, updatedFrom, updatedTo (plus existing space + q)  
- Combined with View filter (unchanged) and tag filter (client + LabelService)  
- URL query state for classification, lifecycle, relationshipType, reviewDue  
- Over-fetch then facet when filters present so pagination/take still useful  

## Tests

Rag + governance + relationship suites (see run log).

## Remaining polish

- ~~Owner / review-owner pickers~~ → **Owned by me / I am review owner** checkboxes (2026-11-12)  
- Server-side tag facet merge (today still client intersect)  
- Dedicated sort beyond relevance score  
- Full directory picker for arbitrary owner (optional)  
