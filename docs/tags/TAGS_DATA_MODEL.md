# Tags Data Model

## `content.Labels` (extended)

| Column | Notes |
|--------|--------|
| Id, TenantId | Existing |
| Name | Confluence-safe token (= Slug); legacy unique callers |
| DisplayName | User-facing Unicode; spaces allowed |
| NormalizedName | `SearchText.Normalize(DisplayName)`; unique per tenant |
| Slug | Stable URL key; set at create; **not** changed on rename |
| Description, Color | Optional |
| ParentId | Optional self-FK; max depth 5 |
| DisplayOrder | Sibling order |
| IsSystem, IsActive | Governance |
| ArchivedAt | Soft archive (PageTag rows kept) |
| CreatedBy/At, UpdatedBy/At | Audit |

Backfill: DisplayName = Name, NormalizedName = folded Name, Slug = Name, IsActive = true.

## `content.LabelAliases`

TenantId, Id, LabelId, Alias, NormalizedAlias (unique per tenant), CreatedBy, CreatedAt. RLS via `AddTenantIsolation`.

## `content.ContentLabels`

Unchanged junction (PageTag): `(TenantId, ContentId, LabelId)` PK, CreatedBy/At.

## Rules

- Circular parents forbidden; moving preserves ContentLabels.
- Archive hides from new assignment; historical display kept.
- Merge re-points ContentLabels + aliases; drops duplicate links; deletes source.
- Delete only when unused (no ContentLabels, no children, no aliases unless cleared).
