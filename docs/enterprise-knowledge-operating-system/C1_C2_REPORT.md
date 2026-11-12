# C1/C2 — Decision/Incident templates + Jira→Relationship projection

**Date:** 2026-11-11

## C1 Decision / Incident templates

- Global templates seeded idempotently on template list (`KnowledgeSystemTemplates`)
- Marker `[raavi:system=decision-record|incident-record]` in Description
- Structured H2 sections matching Stage B product list (no separate tables)
- API: `TemplateDto.isSystem` + `systemKey`; create-page picker shows translated names
- Tests: `KnowledgeSystemTemplateTests`

## C2 Jira link projection

- `IKnowledgeRelationshipProjection` (Application) implemented by Wiki
- On Jira link create → Integration edge `RelatedTo` / `ExternalIssue` / key `jira:{connectorId}/{issueId}`
- On unlink → edge removed; Content untouched; `canRemove=false` in UI
- Tests: extended `PageIssueLinkTests`

## Migrations

None (templates are data seed; relationships table already exists).
