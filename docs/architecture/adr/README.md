# Architecture Decision Records

Format: Context · Decision · Alternatives considered · Consequences. Status values: Proposed, Accepted, Superseded by ADR-xxx.

| ADR | Title | Status |
|---|---|---|
| [001](ADR-001-overall-architecture.md) | Overall Architecture | Accepted |
| [002](ADR-002-modular-monolith.md) | Modular Monolith | Accepted |
| [003](ADR-003-multi-tenant-architecture.md) | Multi-Tenant Architecture | Accepted |
| [004](ADR-004-tenant-resolution-and-domains.md) | Tenant Resolution and Domain Management | Accepted |
| [005](ADR-005-tenant-data-isolation.md) | Tenant Data Isolation | Accepted — implemented & verified in Phase 1 |
| [006](ADR-006-identity-provider-architecture.md) | Identity and Authentication Provider Architecture | Accepted |
| [007](ADR-007-jira-authentication.md) | Jira Authentication Integration | Accepted (pending verification Q1) |
| [008](ADR-008-confluence-import.md) | Confluence Import Architecture | Accepted (pending verification Q2) |
| [009](ADR-009-content-storage.md) | Content Storage | Accepted |
| [010](ADR-010-editor-extension-architecture.md) | Editor and Extension Architecture | Accepted |
| [011](ADR-011-real-time-collaboration.md) | Real-Time Collaboration | Accepted |
| [012](ADR-012-permission-and-acl.md) | Permission and ACL | Accepted |
| [013](ADR-013-search.md) | Search | Accepted |
| [014](ADR-014-attachment-storage.md) | Attachment Storage | Accepted (bundled store pending Q4) |
| [015](ADR-015-plugin-architecture.md) | Plugin Architecture | Accepted |
| [016](ADR-016-ai-provider-architecture.md) | AI Provider Architecture | Accepted |
| [017](ADR-017-background-processing.md) | Background Processing | Accepted |
| [018](ADR-018-deployment.md) | Deployment | Accepted |
| [019](ADR-019-versioning-and-publishing.md) | Versioning and Publishing | Accepted |
| [020](ADR-020-database-and-migrations.md) | Database and Migration Strategy | Accepted |
| [021](ADR-021-api-versioning.md) | API Versioning and Compatibility | Accepted |
| [022](ADR-022-i18n-and-localization.md) | Internationalization and Localization | Accepted |
| [023](ADR-023-wiki-module-and-outbox.md) | Wiki Core Module and Transactional Outbox | Accepted |
| [024](ADR-024-comments-and-notifications.md) | Comments, Watching and Notifications | Accepted |
| [025](ADR-025-confluence-import.md) | Confluence Import (REST source, idempotent importer, jobs) | Accepted |
| [026](ADR-026-tasks.md) | Tasks (Phase 9a) | Accepted |
| [027](ADR-027-calendar.md) | Calendar (Phase 9b) | Accepted |
| [028](ADR-028-databases.md) | Databases (Phase 9c) | Accepted |
| [029](ADR-029-slides.md) | Slides (Phase 9d) | Accepted |
| [030](ADR-030-whiteboards.md) | Whiteboards, Konva vs Fabric (Phase 9e) | Accepted |
| [031](ADR-031-workflows.md) | Workflows: rules engine on the outbox (Phase 10a) | Accepted |
| [032](ADR-032-approvals.md) | Page approvals (Phase 10b) | Accepted |
| [033](ADR-033-scheduling-and-flow-view.md) | Reminders, stale-page trigger, flow view (Phase 10c) | Accepted |
| [049](ADR-049-smart-search-and-rag.md) | Smart search and retrieval-augmented answers (Phase 11b) | Accepted |
| [050](ADR-050-workspace-deletion.md) | Workspace deletion with a grace period and purge | Accepted |
| [051](ADR-051-operations-users-space-transfer-workers.md) | Accounts without a workspace, space export/import, worker status | Accepted |
| [053](ADR-053-quotes-email-time-display.md) | Comment quotes, e-mail settings in the app, workspace time zone and calendar | Accepted |
| [052](ADR-052-prompt-hub.md) | Prompt Hub: library, categories, templates, context, execution, ratings, versions, forks | Accepted |
| [048](ADR-048-operations-status-logs-compression.md) | Operations: status page, error log, activity log, compressed page bodies (Phase 15) | Accepted |
| [047](ADR-047-experience-polish-themes-admin-tools-logo.md) | Experience polish: themes, administrator tools, workspace logo, two-step setting, guidance (Phase 14) | Accepted |
| [046](ADR-046-brand-assets-and-profile-pictures.md) | Brand assets and profile pictures (Phase 13h) | Accepted |
| [045](ADR-045-path-addressing-and-default-workspace.md) | Path addressing of workspaces and a default workspace (Phase 13g) | Accepted |
| [044](ADR-044-bootstrap-system-administrator.md) | Bootstrap system administrator with forced first sign-in (Phase 13f) | Accepted |
| [043](ADR-043-issue-tracker-connectors.md) | Read-only issue tracker connectors: GitHub, GitLab, Jira (Phase 13a) | Accepted |
| [042](ADR-042-backup-scheduling-and-alerting.md) | Backup scheduling, status and failure alerts (Phase 12f) | Accepted |
| [041](ADR-041-release-package-and-upgrade-script.md) | Release package and upgrade script (Phase 12d) | Accepted |
| [040](ADR-040-sandbox-instance-files-and-upgrade-sync.md) | Sandbox as the home of instance files; upgrade sync of new settings | Accepted |
| [039](ADR-039-performance-baseline.md) | Performance baseline and load tests (Phase 12c) | Accepted |
| [038](ADR-038-backup-and-restore-scripts.md) | Backup, restore and restore-drill scripts (Phase 12b) | Accepted |
| [037](ADR-037-workspace-export.md) | Workspace data export (Phase 12a) | Accepted |
| [036](ADR-036-webhooks.md) | Outbound webhooks (Phase 11d) | Accepted |
| [035](ADR-035-live-whiteboards.md) | Live whiteboards on the collaboration server (Phase 11c) | Accepted |
| [034](ADR-034-ai-assistant.md) | AI assistant on pages: OpenAI-compatible provider per tenant (Phase 11a) | Accepted |
