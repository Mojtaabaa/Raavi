# Database Design

SQL Server 2022+ · shared database, logical tenant isolation (ADR-005) · EF Core 10 code-first migrations per module (ADR-020).

## 1. Conventions

| Topic | Rule |
|---|---|
| Keys | `Id uniqueidentifier`, app-generated SQL-Server-sequential GUID. |
| Tenant column | `TenantId uniqueidentifier NOT NULL` on every tenant-owned table, never updatable. |
| Clustered index | Hot tenant tables cluster on `(TenantId, Id)` (non-clustered PK on `Id`); others cluster on PK. Decision per table recorded in its migration. |
| Unique constraints | Include `TenantId` (e.g. `UX_Spaces_TenantId_Key`). |
| Foreign keys | Important parent/child relationships use composite FKs `(TenantId, ParentId) → (TenantId, Id)` backed by a unique index on the parent `(TenantId, Id)`. |
| Timestamps | `datetime2(7)` UTC, suffix `At` (`CreatedAt`); user-facing schedules also store an IANA `TimeZone`. |
| Concurrency | `RowVersion rowversion` on mutable aggregates. |
| Soft delete | `IsDeleted` + `DeletedAt` + `DeletedBy` only where trash/restore is a feature (content, attachments); otherwise hard delete with audit. |
| JSON | `nvarchar(max)` with `CHECK (ISJSON(col) = 1)` and a sibling `…SchemaVersion int`. (SQL Server 2025 native `json` type considered later; 2022 is the floor.) |
| Strings | `nvarchar` everywhere. Database default collation `Latin1_General_100_CI_AS_SC` (supplementary characters). Machine keys (space keys, slugs, codes, hosts) use `Latin1_General_100_BIN2`. Locale-aware sorting of user-facing names is done by OpenSearch / the application (ICU); per-column `Persian_100_CI_AS_SC` is evaluated in Phase 3 for title sorting (ADR-020). |
| Naming | Schemas per module; PascalCase tables (plural) and columns. |
| RLS | Every tenant-owned table is added to `security.TenantIsolation` by its migration (ADR-005). |

## 1a. Implemented so far

| Phase | Tables | Notes |
|---|---|---|
| 10b | `content.ApprovalRequests`, `content.ApprovalAssignments` | Migration `Approvals` (RLS; request → space and assignment → request composite FKs; no FK to the page node). Request revision is a concurrency token bumped on every decision. |
| 10a | `content.Workflows`, `content.WorkflowRuns` | Migration `Workflows` (RLS + composite FKs: workflow → space, run → workflow). Rule definition as validated JSON; unique (TenantId, WorkflowId, EventId) on runs = idempotency key. |
| 9e | `content.Whiteboards` | Migration `Whiteboards` (RLS + composite FK to the space). Elements are validated JSON (`ElementsJson`), saved as a whole with a revision. |
| 9d | `content.SlideDecks` | Migration `Slides` (RLS + composite FK to the space). Slides are validated JSON (`SlidesJson`), direction stored as `Ltr`/`Rtl`. |
| 9c | `content.Databases`, `content.DatabaseRows` | Migration `Databases` (RLS + composite FKs rows → database → space). Schema and row values are JSON keyed by stable column ids; no runtime DDL. |
| 9b | `content.CalendarEvents` | Migration `Calendar` (RLS + composite FK to the space). Wall-clock dates/times + IANA zone; recurrence as flat columns; cancelled occurrences as a comma-separated date list. |
| 9a | `content.Tasks` | Migration `Tasks` (RLS + composite FK to the space; no FK for the optional page link). |
| 7a | `content.Comments`, `CommentReactions`, `Watches`, `Notifications`, `NotificationPreferences` | Migration `Comments` (RLS + tenant FKs). Unique `(TenantId, EventId, RecipientId)` makes notification fan-out idempotent. |
| 6 | `content.CollaborationDocuments`, `CollaborationUpdates`, `CollaborationSnapshots` | Migration `Collaboration` (RLS + tenant FKs; patched with the idempotent RLS script). Snapshots inline (`varbinary`) until object storage (Phase 7). |
| 5 | `content.PageDrafts`, `PageVersionContributors`, `PublishRequests`; columns `Spaces.RequirePublishApproval`, `Content.PublishedAt`, `PageVersions.RestoredFromVersionNumber` | Migrations `Publishing` (RLS + tenant FKs; backfill with RLS off inside the migration transaction: Publish grants next to Edit grants, PublishedAt, contributors) and `ContributorIndex`. |
| 4 | `content.Attachments` (attachments-lite: bytes in `varbinary(max)`, sniffed media type, SHA-256) | Migration `EditorAttachments`: composite FK to Content, FK to `system.Tenants`, RLS. Phase 7 moves bytes to object storage. |
| 3 | `spaces.Spaces`; `content.Content`, `ContentClosure`, `PageVersions`, `Labels`, `ContentLabels`, `Templates`, `Activity`, `Outbox` (global); `security.PermissionGrants`, `ContentRestrictions` | Migration `InitialWiki` (module `Raavi.Wiki`): composite (TenantId, Id) FKs between wiki tables, FK to `system.Tenants` and RLS on all 10 tenant tables; revisions as concurrency tokens (ADR-023). |
| 2b | `[identity].UserTenantMemberships` (global, non-authoritative index for tenant switching; FK to `system.Tenants`) | Migration `MembershipIndex`; backfill runs with the RLS policy switched off only inside the migration transaction (migrator login). |
| 2a | `[identity]`: Users, ExternalIdentities, AuthenticationSessions, SystemRoleAssignments, SystemInvitations, SystemSecurityEvents (global); TenantMembers, TenantRoleAssignments, TenantGroups, TenantGroupMembers (composite FK to the group), TenantInvitations, TenantAuthenticationProviders, TenantSecurityEvents (RLS + FK to `system.Tenants`) | Migration `InitialIdentity` (`Raavi.Identity/Persistence/Migrations`). **`IDENTITY` is a reserved word in T-SQL: always write `[identity]` in raw SQL.** |
| 1 | `system.Tenants`, `system.TenantDomains`, `system.TenantSettings` (RLS), `system.TenantBranding` (RLS), `system.SystemAuditLogs`, `audit.TenantAuditLogs` (RLS), `security.fn_tenant_predicate` + `security.TenantIsolation` policy, `hangfire.*` | Migration `InitialTenancy` (`Raavi.Tenancy/Persistence/Migrations`), history table `system.__EFMigrationsHistory`. Custom-domain uniqueness is a filtered unique index on `Host WHERE RemovedAt IS NULL`; removed rows are kept for quarantine/audit. |

## 2. Ownership map

S = system, G = global identity, T = tenant-owned, U = user-owned within tenant.

| Schema | Table | Class | Notes |
|---|---|---|---|
| system | Tenants | S | Code (unique, immutable), Slug (unique), Status, DisplayName (fa/en), timestamps |
| system | TenantDomains | S | Host (unique), TenantId, IsPrimary, Status (Pending/Verified/Lost), VerificationToken (hashed), VerifiedAt |
| system | TenantSettings | T | Typed columns: DefaultLocale, TimeZone, DateFormat, Calendar, StorageQuotaBytes, RetentionDays, … + `ExtensionsJson` |
| system | TenantBranding | T | LogoAttachmentId, FaviconAttachmentId, PrimaryColor, SecondaryColor, AppName, WelcomeMessageJson |
| system | TenantFeatureFlags | T | Narrowing overrides of global flags |
| system | TenantUsage | T | Daily counters (storage, users, AI tokens) |
| system | SystemFeatureFlags | S | Runtime toggles beyond `feature-flags.json` |
| system | SystemAuditLogs | S | Append-only |
| identity | Users | G | Display name, e-mail (unverified/verified), status, locale preference, password hash (local) |
| identity | ExternalIdentities | G | `UX (AuthenticationProviderId, ExternalSubjectId)`; UserId, ExternalUsername, ExternalEmail, CreatedAt, LastAuthenticatedAt |
| identity | AuthenticationSessions | G | Session id hash, UserId, **TenantId**, ProviderId, CreatedAt, LastSeenAt, ExpiresAt, RevokedAt |
| identity | UserPreferences | G | Global prefs (language, theme) |
| identity | ApiTokens | T | Hash, scopes, ExpiresAt, owner |
| identity | TenantAuthenticationProviders | T | ProviderId (ref. global definition id), Enabled, IsDefault, Order, ProvisioningMode, AllowedDomains, GroupMappingsJson |
| identity | TenantMembers | T | `UX (TenantId, UserId)`, Status (Invited/Active/Suspended/PendingApproval), JoinedAt |
| identity | TenantGroups / TenantGroupMembers | T | Includes externally synced groups (Source, ExternalGroupId) |
| identity | TenantRoles / TenantUserRoles | T | Built-in + custom roles |
| identity | SystemRoles / SystemRoleAssignments | S | System administration |
| spaces | Spaces | T | `UX (TenantId, Key)`, Type (Team/Project/Documentation/Personal/KnowledgeBase), OwnerId, Status, HomeContentId |
| spaces | SpaceSettings, SpaceMembers | T | |
| content | Content | T | Generic content node: Id, TenantId, SpaceId, ParentId, ContentType, Title, Slug, Status, CurrentVersionId, PublishedVersionId, OwnerId, SortOrder (decimal for cheap reordering), IsArchived, IsDeleted, timestamps. (`Pages` in the spec = Content rows with ContentType `page`.) |
| content | ContentClosure | T | Ancestor/descendant/depth for fast tree + inherited restriction queries |
| content | PageVersions | T | Id, TenantId, ContentId, VersionNumber (`UX (TenantId, ContentId, VersionNumber)`), ContentJson, SchemaVersion, ContentHash (SHA-256), ChangeSummary, CreatedBy (nullable for unmapped imports), CreatedAt, VersionType (Draft/Published/Restore/Import), SourceSystem, SourceVersionIdentifier, SourceAuthorJson |
| content | PageDrafts | U | Per-content shared draft pointer to collaboration doc |
| content | PageLinks | T | Source → target (content id or unresolved reference) for backlinks and broken-link reports |
| content | Labels / ContentLabels | T | |
| content | ContentProperties | T | Key/value JSON extension properties |
| collaboration | CollaborationDocuments | T | DocumentName, ContentId, LastSeq, SnapshotSeq |
| collaboration | YjsUpdates | T | `(TenantId, DocumentId, Seq)` clustered; varbinary(max) |
| collaboration | CollaborationSnapshots | T | Inline varbinary or ObjectKey |
| collaboration | CollaborationSessions | T | Active connections (for revocation/analytics) |
| comments | Comments | T | ContentId, ParentCommentId (replies), Kind (Page/Inline), AnchorJson (Yjs relative positions), Status (Open/Resolved), BodyJson |
| comments | CommentReactions, CommentHistory | T | |
| security | PermissionGrants | T | Scope (Tenant/Space/Content), ScopeId, SubjectType (User/Group/Role/Anonymous/System), SubjectId, Action, Effect (Allow/Deny) |
| security | ContentRestrictions | T | ContentId, Kind (View/Edit), SubjectType, SubjectId |
| security | SharedLinks | T | Token hash, ContentId, Permission, ExpiresAt |
| security | EffectiveViewAcl | T | Materialized principal set per content for search (ADR-012) |
| media | Attachments | T | ContentId, FileName, MediaType, CurrentFileVersionId, IsDeleted |
| media | FileVersions | T | ObjectKey, Size, Sha256, ScanStatus, UploadedBy |
| templates | Templates, TemplateVersions | T | Global templates are tenant-copied, not shared rows |
| tasks | Tasks, TaskAssignments, TaskDependencies | T | |
| calendar | Calendars, CalendarEvents, EventRecurrences, EventParticipants | T | RRULE text + TimeZone |
| userdb | UserDatabases, DatabaseColumns, DatabaseViews, DatabaseRecords (ValuesJson + indexed computed columns for sort/filter), DatabaseRelations | T | Metadata-driven, no physical table per database |
| whiteboards | Whiteboards, WhiteboardVersions | T | Yjs-backed like pages |
| slides | SlideDecks, Slides | T | Independent model |
| workflow | Workflows, WorkflowStates, WorkflowTransitions, WorkflowInstances, WorkflowHistory | T | |
| automation | AutomationRules, AutomationExecutions, AutomationExecutionLogs | T | |
| notification | Notifications, NotificationPreferences, NotificationSubscriptions | U | |
| audit | ActivityLogs | T | User activity |
| audit | SecurityEvents | T or S | TenantId nullable only for system events (separate RLS predicate) |
| messaging | Outbox | T | Type, PayloadJson, OccurredAt, ProcessedAt, Attempts |
| search | IndexingOutbox, IndexState | T | |
| migration | MigrationSources | T | Encrypted credentials, edition, version |
| migration | MigrationJobs | T | Status, phase, counters, DryRun flag, options |
| migration | MigrationSourceMappings | T | `UX (TenantId, MigrationSourceId, SourceObjectType, SourceObjectId, SourceVersionIdentifier)`; DestinationObjectType/Id, SourceSpaceKey, ImportStatus |
| migration | MigrationItemLogs, MigrationWarnings, MigrationCheckpoints | T | |
| integration | IntegrationConnections, WebhookSubscriptions | T | Encrypted credentials |
| ai | AIProviders, AIModels | T | Tenant-specific where permitted; global defaults from config |
| ai | AIUsage, EmbeddingJobs, PromptTemplates | T | |
| hangfire | (Hangfire tables) | S | Job arguments contain TenantJobContext |

## 3. Key models

### Content (page)

`Id, TenantId, SpaceId, ParentId, ContentType, Title, Slug, Status (Draft/Published/Archived/Deleted), CurrentVersionId, PublishedVersionId, OwnerId, CreatedAt, CreatedBy, UpdatedAt, UpdatedBy, PublishedAt, ReviewAt, ExpiresAt, IsArchived, IsDeleted, SortOrder, RowVersion`.

Composite FKs: `(TenantId, SpaceId) → spaces.Spaces(TenantId, Id)`, `(TenantId, ParentId) → content.Content(TenantId, Id)`.

### PageVersions

As in §2; `VersionNumber` is Raavi's monotonic sequence; `SourceVersionIdentifier` keeps the Confluence version number/id for imported versions; imported versions keep original `CreatedAt` and either mapped `CreatedBy` or `SourceAuthorJson` (original key/username/display name) when unmapped.

### ExternalIdentities

`Id, UserId, AuthenticationProviderId, ExternalSubjectId, ExternalUsername, ExternalEmail, CreatedAt, LastAuthenticatedAt`; unique `(AuthenticationProviderId, ExternalSubjectId)`.

## 4. Migrations

- One EF Core migrations assembly per DbContext; applied by a dedicated `raavi-migrator` job (never by app startup in Production).
- Destructive changes follow expand → migrate → contract across releases.
- RLS policy changes are generated SQL inside migrations and covered by a test comparing `sys.security_predicates` with the EF model's tenant-owned tables.
