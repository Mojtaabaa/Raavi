# ADR-028 — Databases (Phase 9c)

**Status:** Accepted (2026-11-08)

## Context

Third step of Phase 9. "Databases" means user-defined tables inside a space (a Notion/Confluence-Database style structured list), where the columns are chosen by users, not by developers. The requirement is metadata-driven structure with persistence, tenant ownership, permissions, navigation and export.

## Decisions

1. **Schema is data.** `content.Databases` stores the column list (id, name, type, select options) as JSON; `content.DatabaseRows` stores one JSON object per row, keyed by **column id**. No DDL is ever executed at runtime (no table-per-database): tenant isolation, RLS, migrations and backups stay identical to every other table. Both tables are tenant-owned with RLS and composite foreign keys (rows → database → space).
2. **Stable ids.** Columns and select options get server-generated ids. Renaming a column or an option never touches stored rows; a client can only reuse ids that exist (forged ids are refused). Removing a column hides its data (ignored on read, refused on write); it is not rewritten in other rows.
3. **Types are fixed per column** (Text, Number, Date, Checkbox, Select, Url). Changing the type of an existing column is refused (`db.column_type_locked`) because it would reinterpret stored data; the supported path is to add a new column. At most 30 columns, 50 options per select.
4. **Values are validated on every write** against the column type (text ≤ 2 000 chars without control characters, finite numbers, `yyyy-MM-dd` dates, booleans, known option ids, `http(s)` URLs only — so `javascript:` can never be stored). Empty cells are absent, unknown columns are refused. The read view renders links with `rel="noopener noreferrer nofollow"` and as text otherwise.
5. **Permissions come from the space**, evaluated per request: View to read, Edit on an active space to change schema or rows. A database of a space the caller cannot view does not exist for them (404, including rows and export).
6. **Querying is bounded.** A database holds at most 5 000 rows; the server filters (free text over every cell, select labels included), sorts by one column (numbers numerically, text case-insensitively, empty cells always last) and pages (≤ 500). With the cap, this in-memory step is cheap and avoids JSON-path SQL that LocalDB 2016 (compat level 130) does not support; indexing JSON columns is a possible later step if the cap is raised.
7. **Concurrency.** The schema and each row carry a revision (`"r{n}"` ETag, `If-Match` required). Rows are edited cell by cell, each save sending the whole row with its own ETag, so two people editing different rows never conflict.
8. **Export** is CSV (UTF-8 BOM, RFC 4180, formula-safe through the same writer as tasks); select cells export their label.

## Not in 9c

Relations/lookups/rollups between databases, formulas, multi-select/user/file columns, saved views and multi-column sorts or per-column filters, board/gallery layouts, import from CSV, embedding a database in a page, row-level permissions, change history of rows, search indexing of rows. Column removal does not purge old values.
