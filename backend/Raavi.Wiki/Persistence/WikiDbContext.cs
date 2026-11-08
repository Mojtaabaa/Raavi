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

    public DbSet<ContentClosure> Closure => Set<ContentClosure>();

    public DbSet<PageVersion> PageVersions => Set<PageVersion>();

    public DbSet<Label> Labels => Set<Label>();

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

    public DbSet<CalendarEvent> CalendarEvents => Set<CalendarEvent>();

    public DbSet<SpaceDatabase> SpaceDatabases => Set<SpaceDatabase>();

    public DbSet<DatabaseRow> DatabaseRows => Set<DatabaseRow>();

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
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
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
            b.Property(x => x.DueDate).HasColumnType("date");
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.Status, x.DueDate });
            b.HasIndex(x => new { x.TenantId, x.AssigneeId, x.Status });
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<CalendarEvent>(b =>
        {
            b.ToTable("CalendarEvents", "content");
            Keys(b);
            b.Property(x => x.Title).HasMaxLength(CalendarEvent.MaxTitleLength);
            b.Property(x => x.Description).HasMaxLength(CalendarEvent.MaxDescriptionLength);
            b.Property(x => x.Location).HasMaxLength(CalendarEvent.MaxLocationLength);
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
            b.HasIndex(x => new { x.TenantId, x.SpaceId, x.StartDate });
            b.HasOne<Space>().WithMany().HasForeignKey(x => new { x.TenantId, x.SpaceId }).HasPrincipalKey(s => new { s.TenantId, s.Id }).OnDelete(DeleteBehavior.Restrict);
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
