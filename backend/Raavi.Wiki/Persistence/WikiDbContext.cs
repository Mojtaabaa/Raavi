using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Messaging;
using Raavi.Infrastructure.Persistence;
using Raavi.Wiki.Domain;

namespace Raavi.Wiki.Persistence;

/// <summary>
/// Wiki core persistence (Phase 3). Schemas: <c>spaces</c> (Spaces), <c>content</c> (Content, ContentClosure,
/// PageVersions, Labels, ContentLabels, Templates, Activity, Outbox) and <c>security</c> (PermissionGrants,
/// ContentRestrictions). Every table except the Outbox is tenant-owned and RLS-protected; cross-row references use
/// composite (TenantId, Id) foreign keys so a row can never point into another tenant.
/// </summary>
public sealed class WikiDbContext(DbContextOptions<WikiDbContext> options, ITenantContext tenantContext)
    : RaaviDbContextBase(options, tenantContext), IHasOutbox
{
    public const string HistorySchema = "content";
    private const string Bin2 = "Latin1_General_100_BIN2";

    public DbSet<Space> Spaces => Set<Space>();

    public DbSet<ContentNode> Nodes => Set<ContentNode>();

    public DbSet<ContentGovernance> ContentGovernance => Set<ContentGovernance>();

    public DbSet<KnowledgeRelationship> KnowledgeRelationships => Set<KnowledgeRelationship>();

    public DbSet<ContentClosure> Closure => Set<ContentClosure>();

    public DbSet<PageVersion> PageVersions => Set<PageVersion>();

    public DbSet<Label> Labels => Set<Label>();

    public DbSet<LabelAlias> LabelAliases => Set<LabelAlias>();

    public DbSet<ContentLabel> ContentLabels => Set<ContentLabel>();

    public DbSet<Template> Templates => Set<Template>();

    public DbSet<ActivityEntry> Activity => Set<ActivityEntry>();

    public DbSet<PermissionGrant> Grants => Set<PermissionGrant>();

    public DbSet<ContentRestriction> Restrictions => Set<ContentRestriction>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    public DbSet<Attachment> Attachments => Set<Attachment>();

    public DbSet<PageDraft> Drafts => Set<PageDraft>();

    public DbSet<PageVersionContributor> VersionContributors => Set<PageVersionContributor>();

    public DbSet<PublishRequest> PublishRequests => Set<PublishRequest>();

    public DbSet<CollaborationDocument> CollaborationDocuments => Set<CollaborationDocument>();

    public DbSet<CollaborationUpdate> CollaborationUpdates => Set<CollaborationUpdate>();

    public DbSet<CollaborationSnapshot> CollaborationSnapshots => Set<CollaborationSnapshot>();

    public DbSet<Comment> Comments => Set<Comment>();

    public DbSet<WorkTask> WorkTasks => Set<WorkTask>();

    public DbSet<TaskAssignee> TaskAssignees => Set<TaskAssignee>();

    public DbSet<TaskChecklist> TaskChecklists => Set<TaskChecklist>();

    public DbSet<TaskChecklistItem> TaskChecklistItems => Set<TaskChecklistItem>();

    public DbSet<TaskComment> TaskComments => Set<TaskComment>();

    public DbSet<TaskAttachment> TaskAttachments => Set<TaskAttachment>();

    public DbSet<TaskWatcher> TaskWatchers => Set<TaskWatcher>();

    public DbSet<TaskTimeEntry> TaskTimeEntries => Set<TaskTimeEntry>();

    public DbSet<Calendar> Calendars => Set<Calendar>();

    public DbSet<CalendarAccessGrant> CalendarAccessGrants => Set<CalendarAccessGrant>();
    public DbSet<CalendarEventReminder> CalendarEventReminders => Set<CalendarEventReminder>();
    public DbSet<CalendarEventAttendee> CalendarEventAttendees => Set<CalendarEventAttendee>();
    public DbSet<CalendarUserPreference> CalendarUserPreferences => Set<CalendarUserPreference>();

    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();

    public DbSet<SpaceDatabase> SpaceDatabases => Set<SpaceDatabase>();

    public DbSet<DatabaseRow> DatabaseRows => Set<DatabaseRow>();

    public DbSet<SlideDeck> SlideDecks => Set<SlideDeck>();

    public DbSet<Whiteboard> Whiteboards => Set<Whiteboard>();

    public DbSet<WhiteboardAttachment> WhiteboardAttachments => Set<WhiteboardAttachment>();

    public DbSet<WhiteboardFavorite> WhiteboardFavorites => Set<WhiteboardFavorite>();

    public DbSet<WhiteboardRevision> WhiteboardRevisions => Set<WhiteboardRevision>();

    public DbSet<WhiteboardTemplate> WhiteboardTemplates => Set<WhiteboardTemplate>();

    public DbSet<WhiteboardComment> WhiteboardComments => Set<WhiteboardComment>();

    public DbSet<Workflow> Workflows => Set<Workflow>();

    public DbSet<ApprovalRequest> ApprovalRequests => Set<ApprovalRequest>();

    public DbSet<ApprovalAssignment> ApprovalAssignments => Set<ApprovalAssignment>();

    public DbSet<WorkflowRun> WorkflowRuns => Set<WorkflowRun>();

    public DbSet<CommentReaction> CommentReactions => Set<CommentReaction>();

    public DbSet<Watch> Watches => Set<Watch>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<NotificationPreference> NotificationPreferences => Set<NotificationPreference>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Space>(b =>
        {
            b.ToTable("Spaces", "spaces");
            Keys(b);
            b.Property(x => x.Key).HasMaxLength(Space.MaxKeyLength).IsUnicode(false).UseCollation(Bin2);
            b.Property(x => x.Name).HasMaxLength(Space.MaxNameLength);
            b.Property(x => x.Description).HasMaxLength(Space.MaxDescriptionLength);
            b.Property(x => x.Type).HasConversion<int>();
            b.Property(x => x.Status).HasConversion<int>();
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.Key }).IsUnique();
            b.Property(x => x.SourceSystem).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.SourceId).HasMaxLength(128).UseCollation(Bin2);
            b.HasIndex(x => new { x.TenantId, x.SourceSystem, x.SourceId }).IsUnique().HasFilter("[SourceSystem] IS NOT NULL");
        });

        modelBuilder.Entity<ContentNode>(b =>
        {
            b.ToTable("Content", "content");
            Keys(b);
            b.Property(x => x.ContentType).HasMaxLength(ContentNode.MaxContentTypeLength).IsUnicode(false).UseCollation(Bin2);
            b.Property(x => x.Title).HasMaxLength(ContentNode.MaxTitleLength);
            b.Property(x => x.Slug).HasMaxLength(ContentNode.MaxTitleLength);
            b.Property(x => x.Status).HasConversion<int>();
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.ParentId, x.Position });
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.Status, x.UpdatedAt });
            b.Property(x => x.SourceSystem).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.SourceId).HasMaxLength(128).UseCollation(Bin2);
            b.Property(x => x.SourceUrl).HasMaxLength(500);
            b.HasIndex(x => new { x.TenantId, x.SourceSystem, x.SourceId }).IsUnique().HasFilter("[SourceSystem] IS NOT NULL");
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.ParentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ContentGovernance>(b =>
        {
            b.ToTable("ContentGovernance", "content");
            Keys(b);
            b.Property(x => x.Classification).HasMaxLength(ContentClassifications.MaxLength).IsUnicode(false).UseCollation(Bin2);
            b.Property(x => x.LifecycleStatus).HasMaxLength(GovernanceLifecycles.MaxLength).IsUnicode(false).UseCollation(Bin2);
            b.HasIndex(x => new { x.TenantId, x.OwnerUserId });
            b.HasIndex(x => new { x.TenantId, x.ReviewOwnerUserId });
            b.HasIndex(x => new { x.TenantId, x.ReviewAt });
            b.HasIndex(x => new { x.TenantId, x.Classification });
            b.HasIndex(x => new { x.TenantId, x.LifecycleStatus });
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.Id }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<KnowledgeRelationship>(b =>
        {
            b.ToTable("KnowledgeRelationships", "content");
            Keys(b);
            b.Property(x => x.RelationshipType).HasMaxLength(KnowledgeRelationshipTypes.MaxLength).IsUnicode(false).UseCollation(Bin2);
            b.Property(x => x.TargetKind).HasMaxLength(KnowledgeTargetKinds.MaxLength).IsUnicode(false).UseCollation(Bin2);
            b.Property(x => x.TargetExternalKey).HasMaxLength(KnowledgeRelationship.MaxExternalKeyLength).IsUnicode(false).UseCollation(Bin2);
            b.Property(x => x.TargetTitle).HasMaxLength(KnowledgeRelationship.MaxTitleLength);
            b.Property(x => x.Origin).HasMaxLength(KnowledgeRelationshipOrigins.MaxLength).IsUnicode(false).UseCollation(Bin2);
            b.Property(x => x.SourceSystem).HasMaxLength(KnowledgeRelationship.MaxSourceSystemLength).IsUnicode(false).UseCollation(Bin2);
            b.HasIndex(x => new { x.TenantId, x.SourceContentId, x.RelationshipType });
            b.HasIndex(x => new { x.TenantId, x.TargetContentId });
            b.HasIndex(x => new { x.TenantId, x.RelationshipType });
            b.HasIndex(x => new { x.TenantId, x.SourceContentId, x.RelationshipType, x.TargetKind, x.TargetContentId })
                .IsUnique()
                .HasFilter("[TargetContentId] IS NOT NULL");
            b.HasIndex(x => new { x.TenantId, x.SourceContentId, x.RelationshipType, x.TargetKind, x.TargetExternalKey })
                .IsUnique()
                .HasFilter("[TargetExternalKey] IS NOT NULL");
            // Cascade only on source — SQL Server forbids multiple CASCADE paths to Content; purge cleans target edges in app.
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.SourceContentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.TargetContentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ContentClosure>(b =>
        {
            b.ToTable("ContentClosure", "content");
            b.HasKey(x => new { x.TenantId, x.AncestorId, x.DescendantId }).IsClustered();
            b.HasIndex(x => new { x.TenantId, x.DescendantId, x.Depth });
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.AncestorId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.DescendantId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PageVersion>(b =>
        {
            b.ToTable("PageVersions", "content");
            Keys(b);
            b.Property(x => x.Title).HasMaxLength(ContentNode.MaxTitleLength);
            b.Property(x => x.ContentHash).HasMaxLength(64).IsUnicode(false).UseCollation(Bin2);
            b.Property(x => x.ChangeSummary).HasMaxLength(500);
            b.Property(x => x.ContentJson).HasConversion(GzipText.Converter).HasColumnType("varbinary(max)"); // compressed (GzipText)
            b.Property(x => x.VersionType).HasConversion<int>();
            b.HasIndex(x => new { x.TenantId, x.ContentId, x.VersionNumber }).IsUnique();
            b.Property(x => x.SourceVersionIdentifier).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.SourceAuthorJson).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.ContentId, x.SourceVersionIdentifier }).IsUnique().HasFilter("[SourceVersionIdentifier] IS NOT NULL");
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Label>(b =>
        {
            b.ToTable("Labels", "content");
            Keys(b);
            b.Property(x => x.Name).HasMaxLength(Label.MaxLength).UseCollation(Bin2);
            b.Property(x => x.DisplayName).HasMaxLength(Label.MaxDisplayLength);
            b.Property(x => x.NormalizedName).HasMaxLength(Label.MaxDisplayLength).UseCollation(Bin2);
            b.Property(x => x.Slug).HasMaxLength(Label.MaxLength).UseCollation(Bin2);
            b.Property(x => x.Description).HasMaxLength(Label.MaxDescriptionLength);
            b.Property(x => x.Color).HasMaxLength(Label.MaxColorLength).IsUnicode(false);
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.NormalizedName }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.Slug }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.ParentId, x.DisplayOrder });
            b.HasOne<Label>().WithMany().HasForeignKey(x => new { x.TenantId, x.ParentId }).HasPrincipalKey(l => new { l.TenantId, l.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LabelAlias>(b =>
        {
            b.ToTable("LabelAliases", "content");
            Keys(b);
            b.Property(x => x.Alias).HasMaxLength(LabelAlias.MaxAliasLength);
            b.Property(x => x.NormalizedAlias).HasMaxLength(LabelAlias.MaxAliasLength).UseCollation(Bin2);
            b.HasIndex(x => new { x.TenantId, x.NormalizedAlias }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.LabelId });
            b.HasOne<Label>().WithMany().HasForeignKey(x => new { x.TenantId, x.LabelId }).HasPrincipalKey(l => new { l.TenantId, l.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ContentLabel>(b =>
        {
            b.ToTable("ContentLabels", "content");
            b.HasKey(x => new { x.TenantId, x.ContentId, x.LabelId }).IsClustered();
            b.HasIndex(x => new { x.TenantId, x.LabelId });
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Label>().WithMany().HasForeignKey(x => new { x.TenantId, x.LabelId }).HasPrincipalKey(l => new { l.TenantId, l.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Template>(b =>
        {
            b.ToTable("Templates", "content");
            Keys(b);
            b.Property(x => x.Name).HasMaxLength(Template.MaxNameLength);
            b.Property(x => x.Description).HasMaxLength(1000);
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.SpaceId });
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ActivityEntry>(b =>
        {
            b.ToTable("Activity", "content");
            b.HasKey(x => x.Id).IsClustered(false);
            b.Property(x => x.Id).ValueGeneratedNever();
            b.Property(x => x.Kind).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.Title).HasMaxLength(ContentNode.MaxTitleLength);
            b.HasIndex(x => new { x.TenantId, x.OccurredAt }).IsClustered();
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.OccurredAt });
            b.HasIndex(x => new { x.TenantId, x.TaskId, x.OccurredAt });
        });

        modelBuilder.Entity<PermissionGrant>(b =>
        {
            b.ToTable("PermissionGrants", "security");
            Keys(b);
            b.Property(x => x.Scope).HasConversion<int>();
            b.Property(x => x.SubjectType).HasConversion<int>();
            b.Property(x => x.Action).HasConversion<int>();
            b.Property(x => x.Effect).HasConversion<int>();
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.Action });
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ContentRestriction>(b =>
        {
            b.ToTable("ContentRestrictions", "security");
            Keys(b);
            b.Property(x => x.Kind).HasConversion<int>();
            b.Property(x => x.SubjectType).HasConversion<int>();
            b.HasIndex(x => new { x.TenantId, x.ContentId, x.Kind, x.SubjectType, x.SubjectId }).IsUnique();
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Attachment>(b =>
        {
            b.ToTable("Attachments", "content");
            Keys(b);
            b.Property(x => x.FileName).HasMaxLength(Attachment.MaxFileNameLength);
            b.Property(x => x.MediaType).HasMaxLength(128).IsUnicode(false);
            b.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false).UseCollation(Bin2);
            b.Ignore(x => x.IsImage);
            b.HasIndex(x => new { x.TenantId, x.ContentId });
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PageDraft>(b =>
        {
            b.ToTable("PageDrafts", "content");
            Keys(b);
            b.Property(x => x.Title).HasMaxLength(ContentNode.MaxTitleLength);
            b.Property(x => x.ContentHash).HasMaxLength(64).IsUnicode(false).UseCollation(Bin2);
            b.Property(x => x.Contributors).HasMaxLength(4000).IsUnicode(false);
            b.Property(x => x.ScheduledChangeSummary).HasMaxLength(500);
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.Ignore(x => x.ContributorIds);
            b.HasIndex(x => new { x.TenantId, x.ContentId }).IsUnique();
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PageVersionContributor>(b =>
        {
            b.ToTable("PageVersionContributors", "content");
            b.HasKey(x => new { x.TenantId, x.VersionId, x.UserId }).IsClustered();
            b.HasIndex(x => new { x.TenantId, x.UserId });
            b.HasOne<PageVersion>().WithMany().HasForeignKey(x => new { x.TenantId, x.VersionId }).HasPrincipalKey(v => new { v.TenantId, v.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PublishRequest>(b =>
        {
            b.ToTable("PublishRequests", "content");
            Keys(b);
            b.Property(x => x.Title).HasMaxLength(ContentNode.MaxTitleLength);
            b.Property(x => x.ContentHash).HasMaxLength(64).IsUnicode(false).UseCollation(Bin2);
            b.Property(x => x.ChangeSummary).HasMaxLength(500);
            b.Property(x => x.Contributors).HasMaxLength(4000).IsUnicode(false);
            b.Property(x => x.DecisionComment).HasMaxLength(1000);
            b.Property(x => x.Status).HasConversion<int>();
            b.Ignore(x => x.ContributorIds);
            b.HasIndex(x => new { x.TenantId, x.ContentId, x.Status });
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CollaborationDocument>(b =>
        {
            b.ToTable("CollaborationDocuments", "content");
            Keys(b);
            b.HasIndex(x => new { x.TenantId, x.ContentId }).IsUnique();
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CollaborationUpdate>(b =>
        {
            b.ToTable("CollaborationUpdates", "content");
            Keys(b);
            b.HasIndex(x => new { x.TenantId, x.DocumentId, x.Seq }).IsUnique();
            b.HasOne<CollaborationDocument>().WithMany().HasForeignKey(x => new { x.TenantId, x.DocumentId }).HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CollaborationSnapshot>(b =>
        {
            b.ToTable("CollaborationSnapshots", "content");
            Keys(b);
            b.HasIndex(x => new { x.TenantId, x.DocumentId, x.Seq });
            b.HasOne<CollaborationDocument>().WithMany().HasForeignKey(x => new { x.TenantId, x.DocumentId }).HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Comment>(b =>
        {
            b.ToTable("Comments", "content");
            Keys(b);
            b.Property(x => x.Kind).HasConversion<int>();
            b.Property(x => x.Status).HasConversion<int>();
            b.Property(x => x.AnchorJson).HasMaxLength(2000);
            b.Property(x => x.Excerpt).HasMaxLength(Comment.MaxExcerptLength);
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.Ignore(x => x.IsRoot);
            b.Ignore(x => x.IsDeleted);
            b.HasIndex(x => new { x.TenantId, x.ContentId, x.ThreadId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.AuthorId });
            b.Property(x => x.SourceAuthorName).HasMaxLength(200);
            b.Property(x => x.SourceSystem).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.SourceId).HasMaxLength(128).UseCollation(Bin2);
            b.HasIndex(x => new { x.TenantId, x.SourceSystem, x.SourceId }).IsUnique().HasFilter("[SourceSystem] IS NOT NULL");
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Comment>().WithMany().HasForeignKey(x => new { x.TenantId, x.ParentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkTask>(b =>
        {
            b.ToTable("Tasks", "content");
            Keys(b);
            b.Property(x => x.Title).HasMaxLength(WorkTask.MaxTitleLength);
            b.Property(x => x.Notes).HasMaxLength(WorkTask.MaxNotesLength);
            b.Property(x => x.Status).HasConversion<int>();
            b.Property(x => x.Priority).HasConversion<int>();
            b.Property(x => x.DueDate).HasColumnType("date");
            b.Property(x => x.StartDate).HasColumnType("date");
            b.Property(x => x.ProgressPercent).HasColumnType("tinyint");
            b.Property(x => x.BlockCompleteWhenOpenChildren).HasDefaultValue(true);
            b.Property(x => x.BlockCompleteWhenMandatoryIncomplete).HasDefaultValue(true);
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.Status, x.DueDate });
            b.HasIndex(x => new { x.TenantId, x.AssigneeId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.ParentTaskId });
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.ArchivedAt });
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<WorkTask>().WithMany().HasForeignKey(x => new { x.TenantId, x.ParentTaskId }).HasPrincipalKey(t => new { t.TenantId, t.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaskAssignee>(b =>
        {
            b.ToTable("TaskAssignees", "content");
            Keys(b);
            b.Property(x => x.Role).HasMaxLength(TaskAssigneeRoles.MaxLength).IsUnicode(false).UseCollation(Bin2);
            b.HasIndex(x => new { x.TenantId, x.TaskId, x.UserId }).IsUnique().HasFilter("[RemovedAt] IS NULL");
            b.HasIndex(x => new { x.TenantId, x.TaskId }).IsUnique().HasFilter("[RemovedAt] IS NULL AND [IsPrimary] = CAST(1 AS bit)");
            b.HasIndex(x => new { x.TenantId, x.UserId, x.RemovedAt });
            b.HasOne<WorkTask>().WithMany().HasForeignKey(x => new { x.TenantId, x.TaskId }).HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskChecklist>(b =>
        {
            b.ToTable("TaskChecklists", "content");
            Keys(b);
            b.Property(x => x.Title).HasMaxLength(TaskChecklist.MaxTitleLength);
            b.HasIndex(x => new { x.TenantId, x.TaskId, x.SortOrder });
            b.HasOne<WorkTask>().WithMany().HasForeignKey(x => new { x.TenantId, x.TaskId }).HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskChecklistItem>(b =>
        {
            b.ToTable("TaskChecklistItems", "content");
            Keys(b);
            b.Property(x => x.Title).HasMaxLength(TaskChecklistItem.MaxTitleLength);
            b.HasIndex(x => new { x.TenantId, x.ChecklistId, x.SortOrder });
            b.HasOne<TaskChecklist>().WithMany().HasForeignKey(x => new { x.TenantId, x.ChecklistId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskComment>(b =>
        {
            b.ToTable("TaskComments", "content");
            Keys(b);
            b.Property(x => x.Excerpt).HasMaxLength(TaskComment.MaxExcerptLength);
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.Ignore(x => x.IsRoot);
            b.Ignore(x => x.IsDeleted);
            b.HasIndex(x => new { x.TenantId, x.TaskId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.ThreadId, x.CreatedAt });
            b.HasOne<WorkTask>().WithMany().HasForeignKey(x => new { x.TenantId, x.TaskId }).HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<TaskComment>().WithMany().HasForeignKey(x => new { x.TenantId, x.ParentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaskAttachment>(b =>
        {
            b.ToTable("TaskAttachments", "content");
            Keys(b);
            b.Property(x => x.FileName).HasMaxLength(TaskAttachment.MaxFileNameLength);
            b.Property(x => x.MediaType).HasMaxLength(128).IsUnicode(false);
            b.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false).UseCollation(Bin2);
            b.Property(x => x.Data).HasColumnType("varbinary(max)");
            b.Ignore(x => x.IsImage);
            b.Ignore(x => x.IsDeleted);
            b.HasIndex(x => new { x.TenantId, x.TaskId, x.DeletedAt });
            b.HasOne<WorkTask>().WithMany().HasForeignKey(x => new { x.TenantId, x.TaskId }).HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Cascade);
            b.HasOne<TaskComment>().WithMany().HasForeignKey(x => new { x.TenantId, x.CommentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TaskWatcher>(b =>
        {
            b.ToTable("TaskWatchers", "content");
            Keys(b);
            b.Property(x => x.State).HasConversion<int>();
            b.HasIndex(x => new { x.TenantId, x.TaskId, x.UserId }).IsUnique();
            b.HasOne<WorkTask>().WithMany().HasForeignKey(x => new { x.TenantId, x.TaskId }).HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<TaskTimeEntry>(b =>
        {
            b.ToTable("TaskTimeEntries", "content");
            Keys(b);
            b.Property(x => x.Description).HasMaxLength(TaskTimeEntry.MaxDescriptionLength);
            b.Property(x => x.WorkDate).HasColumnType("date");
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.Ignore(x => x.IsDeleted);
            b.HasIndex(x => new { x.TenantId, x.TaskId, x.WorkDate });
            b.HasIndex(x => new { x.TenantId, x.UserId, x.WorkDate });
            b.HasIndex(x => new { x.TenantId, x.TaskId }).HasFilter("[DeletedAt] IS NULL").HasDatabaseName("IX_TaskTimeEntries_TenantId_TaskId_Active");
            b.HasOne<WorkTask>().WithMany().HasForeignKey(x => new { x.TenantId, x.TaskId }).HasPrincipalKey(t => new { t.TenantId, t.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Calendar>(b =>
        {
            b.ToTable("Calendars", "content");
            Keys(b);
            b.Property(x => x.Scope).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.Name).HasMaxLength(Calendar.MaxNameLength);
            b.Property(x => x.Description).HasMaxLength(Calendar.MaxDescriptionLength);
            b.Property(x => x.Color).HasMaxLength(Calendar.MaxColorLength).IsUnicode(false);
            b.Property(x => x.Icon).HasMaxLength(Calendar.MaxIconLength).IsUnicode(false);
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.Scope, x.SpaceId, x.OwnerUserId });
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.IsDefault });
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CalendarAccessGrant>(b =>
        {
            b.ToTable("CalendarAccessGrants", "content");
            Keys(b);
            b.Property(x => x.SubjectType).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
            b.Property(x => x.Grant).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
            b.HasIndex(x => new { x.TenantId, x.CalendarId, x.SubjectType, x.SubjectId, x.Grant }).IsUnique();
            b.HasOne<Calendar>().WithMany().HasForeignKey(x => new { x.TenantId, x.CalendarId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CalendarEventReminder>(b =>
        {
            b.ToTable("CalendarEventReminders", "content");
            Keys(b);
            b.Property(x => x.LastDeliveredOccurrence).HasMaxLength(10).IsUnicode(false);
            b.HasIndex(x => new { x.TenantId, x.EventId, x.MinutesBefore }).IsUnique();
            b.HasOne<CalendarEvent>().WithMany().HasForeignKey(x => new { x.TenantId, x.EventId }).HasPrincipalKey(e => new { e.TenantId, e.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CalendarEventAttendee>(b =>
        {
            b.ToTable("CalendarEventAttendees", "content");
            Keys(b);
            b.Property(x => x.Response).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
            b.HasIndex(x => new { x.TenantId, x.EventId, x.UserId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId, x.Response });
            b.HasOne<CalendarEvent>().WithMany().HasForeignKey(x => new { x.TenantId, x.EventId }).HasPrincipalKey(e => new { e.TenantId, e.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CalendarUserPreference>(b =>
        {
            b.ToTable("CalendarUserPreferences", "content");
            Keys(b);
            b.Property(x => x.TtsLocale).HasMaxLength(8).IsUnicode(false);
            b.Property(x => x.TimeZone).HasMaxLength(64).IsUnicode(false);
            b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
        });

        modelBuilder.Entity<CalendarEvent>(b =>
        {
            b.ToTable("CalendarEvents", "content");
            Keys(b);
            b.Property(x => x.Title).HasMaxLength(CalendarEvent.MaxTitleLength);
            b.Property(x => x.Description).HasMaxLength(CalendarEvent.MaxDescriptionLength);
            b.Property(x => x.Location).HasMaxLength(CalendarEvent.MaxLocationLength);
            b.Property(x => x.OnlineUrl).HasMaxLength(2000);
            b.Property(x => x.Color).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.Type).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.Status).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.Priority).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
            b.Property(x => x.StartDate).HasColumnType("date");
            b.Property(x => x.EndDate).HasColumnType("date");
            b.Property(x => x.RecurUntil).HasColumnType("date");
            b.Property(x => x.StartTime).HasColumnType("time(0)");
            b.Property(x => x.EndTime).HasColumnType("time(0)");
            b.Property(x => x.TimeZone).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.RecurFrequency).HasConversion<int>();
            b.Property(x => x.RecurCalendar).HasConversion<int>();
            b.Property(x => x.ExcludedDatesRaw).HasMaxLength(CalendarEvent.MaxExcluded * 11).IsUnicode(false);
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.Ignore(x => x.Rule);
            b.Ignore(x => x.SpanDays);
            b.Ignore(x => x.ExcludedDates);
            b.HasIndex(x => new { x.TenantId, x.CalendarId, x.StartDate });
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.StartDate });
            b.HasIndex(x => new { x.TenantId, x.LinkedPageId });
            b.HasIndex(x => new { x.TenantId, x.LinkedWhiteboardId });
            b.HasOne<Calendar>().WithMany().HasForeignKey(x => new { x.TenantId, x.CalendarId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).IsRequired(false).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SpaceDatabase>(b =>
        {
            b.ToTable("Databases", "content");
            Keys(b);
            b.Property(x => x.Name).HasMaxLength(SpaceDatabase.MaxNameLength);
            b.Property(x => x.Description).HasMaxLength(SpaceDatabase.MaxDescriptionLength);
            b.Property(x => x.ColumnsJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.Ignore(x => x.Columns);
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.Name });
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DatabaseRow>(b =>
        {
            b.ToTable("DatabaseRows", "content");
            Keys(b);
            b.Property(x => x.ValuesJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.Ignore(x => x.Values);
            b.HasIndex(x => new { x.TenantId, x.DatabaseId, x.CreatedAt });
            b.HasOne<SpaceDatabase>().WithMany().HasForeignKey(x => new { x.TenantId, x.DatabaseId }).HasPrincipalKey(d => new { d.TenantId, d.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SlideDeck>(b =>
        {
            b.ToTable("SlideDecks", "content");
            Keys(b);
            b.Property(x => x.Title).HasMaxLength(SlideDeck.MaxTitleLength);
            b.Property(x => x.Direction).HasConversion<string>().HasMaxLength(3);
            b.Property(x => x.SlidesJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.Ignore(x => x.Slides);
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.Title });
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Whiteboard>(b =>
        {
            b.ToTable("Whiteboards", "content");
            Keys(b);
            b.Property(x => x.Title).HasMaxLength(Whiteboard.MaxTitleLength);
            b.Property(x => x.Description).HasMaxLength(Whiteboard.MaxDescriptionLength);
            b.Property(x => x.Background).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.GridStyle).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
            b.Property(x => x.ElementsJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.YjsState).HasColumnType("varbinary(max)");
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.Ignore(x => x.Elements);
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.Title });
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.ArchivedAt });
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WhiteboardAttachment>(b =>
        {
            b.ToTable("WhiteboardAttachments", "content");
            Keys(b);
            b.Property(x => x.FileName).HasMaxLength(WhiteboardAttachment.MaxFileNameLength);
            b.Property(x => x.MediaType).HasMaxLength(100).IsUnicode(false);
            b.Property(x => x.Sha256).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.Data).HasColumnType("varbinary(max)");
            b.HasIndex(x => new { x.TenantId, x.WhiteboardId });
            b.HasOne<Whiteboard>().WithMany().HasForeignKey(x => new { x.TenantId, x.WhiteboardId }).HasPrincipalKey(w => new { w.TenantId, w.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WhiteboardFavorite>(b =>
        {
            b.ToTable("WhiteboardFavorites", "content");
            Keys(b);
            b.HasIndex(x => new { x.TenantId, x.UserId, x.WhiteboardId }).IsUnique();
            b.HasOne<Whiteboard>().WithMany().HasForeignKey(x => new { x.TenantId, x.WhiteboardId }).HasPrincipalKey(w => new { w.TenantId, w.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WhiteboardRevision>(b =>
        {
            b.ToTable("WhiteboardRevisions", "content");
            Keys(b);
            b.Property(x => x.Title).HasMaxLength(Whiteboard.MaxTitleLength);
            b.Property(x => x.Description).HasMaxLength(Whiteboard.MaxDescriptionLength);
            b.Property(x => x.Background).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.GridStyle).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
            b.Property(x => x.ElementsJson).HasColumnType("nvarchar(max)");
            b.HasIndex(x => new { x.TenantId, x.WhiteboardId, x.CreatedAt });
            b.HasOne<Whiteboard>().WithMany().HasForeignKey(x => new { x.TenantId, x.WhiteboardId }).HasPrincipalKey(w => new { w.TenantId, w.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<WhiteboardTemplate>(b =>
        {
            b.ToTable("WhiteboardTemplates", "content");
            Keys(b);
            b.Property(x => x.Name).HasMaxLength(WhiteboardTemplate.MaxNameLength);
            b.Property(x => x.Description).HasMaxLength(Whiteboard.MaxDescriptionLength);
            b.Property(x => x.Background).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.GridStyle).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
            b.Property(x => x.ElementsJson).HasColumnType("nvarchar(max)");
            b.Ignore(x => x.Elements);
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.Name });
        });

        modelBuilder.Entity<WhiteboardComment>(b =>
        {
            b.ToTable("WhiteboardComments", "content");
            Keys(b);
            b.Property(x => x.ObjectId).HasMaxLength(40).IsUnicode(false);
            b.Property(x => x.Body).HasMaxLength(WhiteboardComment.MaxBodyLength);
            b.HasIndex(x => new { x.TenantId, x.WhiteboardId, x.CreatedAt });
            b.HasOne<Whiteboard>().WithMany().HasForeignKey(x => new { x.TenantId, x.WhiteboardId }).HasPrincipalKey(w => new { w.TenantId, w.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Workflow>(b =>
        {
            b.ToTable("Workflows", "content");
            Keys(b);
            b.Property(x => x.Name).HasMaxLength(Workflow.MaxNameLength);
            b.Property(x => x.Trigger).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.ConditionsJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.ActionsJson).HasColumnType("nvarchar(max)");
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.Ignore(x => x.Conditions);
            b.Ignore(x => x.Actions);
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.Trigger, x.Enabled });
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<WorkflowRun>(b =>
        {
            b.ToTable("WorkflowRuns", "content");
            Keys(b);
            b.Property(x => x.EventType).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.Message).HasMaxLength(WorkflowRun.MaxMessageLength);
            b.Property(x => x.Status).HasConversion<int>();
            b.HasIndex(x => new { x.TenantId, x.WorkflowId, x.EventId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.WorkflowId, x.StartedAt });
            b.HasOne<Workflow>().WithMany().HasForeignKey(x => new { x.TenantId, x.WorkflowId }).HasPrincipalKey(w => new { w.TenantId, w.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApprovalRequest>(b =>
        {
            b.ToTable("ApprovalRequests", "content");
            Keys(b);
            b.Property(x => x.Note).HasMaxLength(ApprovalRequest.MaxNoteLength);
            b.Property(x => x.Mode).HasConversion<int>();
            b.Property(x => x.Status).HasConversion<int>();
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.ContentId, x.Status });
            b.HasIndex(x => new { x.TenantId, x.RequestedBy, x.RequestedAt });
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApprovalAssignment>(b =>
        {
            b.ToTable("ApprovalAssignments", "content");
            b.HasKey(x => new { x.TenantId, x.RequestId, x.UserId }).IsClustered();
            b.Property(x => x.Comment).HasMaxLength(ApprovalRequest.MaxCommentLength);
            b.Property(x => x.Decision).HasConversion<int>();
            b.HasIndex(x => new { x.TenantId, x.UserId, x.Decision });
            b.HasOne<ApprovalRequest>().WithMany().HasForeignKey(x => new { x.TenantId, x.RequestId }).HasPrincipalKey(r => new { r.TenantId, r.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CommentReaction>(b =>
        {
            b.ToTable("CommentReactions", "content");
            b.HasKey(x => new { x.TenantId, x.CommentId, x.UserId, x.Emoji }).IsClustered();
            b.Property(x => x.Emoji).HasMaxLength(16).UseCollation(Bin2);
            b.HasIndex(x => new { x.TenantId, x.UserId });
            b.HasOne<Comment>().WithMany().HasForeignKey(x => new { x.TenantId, x.CommentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Watch>(b =>
        {
            b.ToTable("Watches", "content");
            Keys(b);
            b.Property(x => x.State).HasConversion<int>();
            b.HasIndex(x => new { x.TenantId, x.ContentId, x.UserId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.UserId });
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("Notifications", "content");
            Keys(b);
            b.Property(x => x.Kind).HasMaxLength(Notification.MaxKindLength).IsUnicode(false);
            b.Property(x => x.Title).HasMaxLength(ContentNode.MaxTitleLength);
            b.Property(x => x.Excerpt).HasMaxLength(Comment.MaxExcerptLength);
            b.Property(x => x.Email).HasConversion<int>();
            b.HasIndex(x => new { x.TenantId, x.RecipientId, x.CreatedAt });
            b.HasIndex(x => new { x.TenantId, x.RecipientId, x.ReadAt });
            b.HasIndex(x => new { x.TenantId, x.EventId, x.RecipientId }).IsUnique();
            b.HasOne<ContentNode>().WithMany().HasForeignKey(x => new { x.TenantId, x.ContentId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<NotificationPreference>(b =>
        {
            b.ToTable("NotificationPreferences", "content");
            Keys(b);
            b.HasIndex(x => new { x.TenantId, x.UserId }).IsUnique();
        });

        modelBuilder.ConfigureOutbox("content");

        base.OnModelCreating(modelBuilder);
    }

    /// <summary>Non-clustered PK on Id plus the clustered (TenantId, Id) alternate key used by composite foreign keys.</summary>
    private static void Keys<T>(EntityTypeBuilder<T> b) where T : class
    {
        b.HasKey("Id").IsClustered(false);
        b.Property<Guid>("Id").ValueGeneratedNever();
        b.HasAlternateKey("TenantId", "Id").IsClustered();
    }
}

public static class WikiMigrations
{
    public static async Task ApplyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(services);
        await using (scope.ConfigureAwait(false))
        {
            var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<WikiDbContext>(scope.ServiceProvider);
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
