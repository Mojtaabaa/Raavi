# ADR-013: Search

**Status:** Accepted · **Date:** 2026-11-06

## Context
Full-text search over Persian/English mixed content with strict tenant and ACL isolation; later semantic search for AI.

## Decision

### 1. Engine and indexing
- **OpenSearch** (Apache-2.0); .NET client OpenSearch.Client.
- Indexing via **outbox**: domain transactions write `IndexingOutbox(TenantId, EntityType, EntityId, Operation)`; workers batch, load the entity through its `IContentProvider` in the tenant's context and bulk-index. At-least-once, idempotent (document id = entity id, version = row version).
- Full reindex job per tenant (used after analyzer changes and imports) with index aliases for zero-downtime swaps.

### 2. Index layout
- Shared indices per entity family (`raavi-content-v1`, `raavi-attachments-v1`, `raavi-people-v1`) with `_routing = tenantId` and a mandatory `tenantId` keyword field. Very large tenants can be moved to dedicated indices behind the same alias scheme.
- Fields: title (fa/en analyzed + keyword + completion), body (fa, en, and a `standard` multi-field), labels, space, author, contentType, status, dates, language, `acl` (keyword: principal tokens from `EffectiveViewAcl`), `vector` (k-NN, Phase 11).

### 3. Query enforcement
- The **only** query entry point is `ISearchQueryBuilder`, which always adds `term tenantId`, routing, and `terms acl ∈ principalTokens(user)`; raw OpenSearch client is not injectable into feature code.
- Suggestions, aggregations and highlights come from the same filtered query. Hits returned to the user are re-checked with `IPermissionEvaluator` (only the current page), so stale ACL data can only hide results.

### 4. Persian normalization (reference implementation: `Raavi.Search/Text/PersianTextNormalizer.cs`)
Applied identically at index time (custom analyzer) and query time:
- Arabic Yeh/Alef Maksura → Persian Yeh; Arabic Kaf → Keheh; Teh Marbuta → Heh.
- Arabic-Indic and Persian digits → ASCII digits.
- Remove tashkil (harakat) and tatweel.
- ZWNJ and zero-width/bidi controls → token boundary; additionally a `shingle`/concatenated subfield so that "می‌روم", "می روم" and "میروم" all match.
- NFKC + lower-case for Latin.
- OpenSearch analyzer chain: `char_filter` (mapping for ZWNJ/digits) → `standard` tokenizer → `lowercase`, `decimal_digit`, `arabic_normalization`, `persian_normalization`, Persian stop words (curated, not aggressive), optional stemmer evaluated with real content. English: `english` analyzer. Mixed content uses multi-fields and the query searches both.

### 5. Other
- Recently viewed / favorites stored in SQL (user-owned), not in the search engine.
- Search analytics (queries, zero-result queries) per tenant, without storing query text beyond retention policy.

## Alternatives considered
- SQL Server Full-Text Search — rejected: weak Persian support, no vectors, limited relevance tuning.
- Index per tenant always — rejected: shard explosion with many tenants; kept as an option per large tenant.

## Consequences
ACL token freshness is an eventual-consistency concern, mitigated by post-filtering. Analyzer changes require reindex jobs.
