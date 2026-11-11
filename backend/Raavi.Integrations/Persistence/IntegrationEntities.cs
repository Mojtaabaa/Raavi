using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Messaging;
using Raavi.Infrastructure.Persistence;

namespace Raavi.Integrations.Persistence;

/// <summary>Event types a webhook can subscribe to. The payload of every event carries identifiers only (see <c>WebhookPayload</c>).</summary>
public static class WebhookEvents
{
    public const string Ping = "ping";

    public static readonly IReadOnlyList<string> All =
    [
        "space.created", "space.updated", "space.archived", "space.restored", "space.permissions_changed",
        "content.created", "content.updated", "content.moved", "content.archived", "content.restored", "content.trashed",
        "content.published", "content.publish_requested", "content.publish_approved", "content.publish_rejected",
        "comment.created", "comment.resolved",
        "approval.requested", "approval.decided",
        "jira.page_issue_linked", "jira.page_issue_unlinked", "jira.page_issue_metadata_updated",
        "jira.page_metadata_updated", "jira.relationship_sync_failed",
    ];

    public static bool IsKnown(string type) => All.Contains(type, StringComparer.Ordinal);
}

/// <summary>
/// An outbound webhook of a tenant (Phase 11d, ADR-036): where to POST, which events, optionally only those of one space. The signing secret is
/// stored only protected (Data Protection, tenant-bound purpose) and shown once, when it is created or rotated.
/// </summary>
public sealed class Webhook : Entity, ITenantOwned
{
    public const int MaxNameLength = 100;
    public const int MaxUrlLength = 500;
    public const int MaxEvents = 30;
    public const int AutoDisableAfterFailures = 20;

    private Webhook() { }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string Url { get; private set; } = string.Empty;

    public string ProtectedSecret { get; private set; } = string.Empty;

    /// <summary>Comma-separated event types.</summary>
    public string EventsCsv { get; private set; } = string.Empty;

    /// <summary>Only events of this space; null = every space of the tenant.</summary>
    public Guid? SpaceId { get; private set; }

    public bool Enabled { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public string? DisabledReason { get; private set; }

    public DateTimeOffset? LastSuccessAt { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public IReadOnlyList<string> Events => EventsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries);

    public static Webhook Create(string? name, string url, IEnumerable<string>? events, Guid? spaceId, string protectedSecret, Guid userId, DateTimeOffset now)
    {
        var w = new Webhook { ProtectedSecret = protectedSecret, CreatedBy = userId, CreatedAt = now, UpdatedAt = now, Enabled = true };
        w.Set(name, url, events, spaceId);
        return w;
    }

    public void Update(string? name, string url, IEnumerable<string>? events, Guid? spaceId, bool enabled, DateTimeOffset now)
    {
        Set(name, url, events, spaceId);
        if (enabled && !Enabled)
        {
            ConsecutiveFailures = 0;
            DisabledReason = null;
        }

        Enabled = enabled;
        UpdatedAt = now;
        Revision++;
    }

    public void RotateSecret(string protectedSecret, DateTimeOffset now)
    {
        ProtectedSecret = protectedSecret;
        UpdatedAt = now;
        Revision++;
    }

    public void Delivered(DateTimeOffset now)
    {
        ConsecutiveFailures = 0;
        LastSuccessAt = now;
    }

    /// <summary>Counts a failed delivery; a webhook that keeps failing is switched off so it stops costing retries.</summary>
    public void Failed(DateTimeOffset now)
    {
        ConsecutiveFailures++;
        if (Enabled && ConsecutiveFailures >= AutoDisableAfterFailures)
        {
            Enabled = false;
            DisabledReason = "repeated_failures";
            UpdatedAt = now;
            Revision++;
        }
    }

    private void Set(string? name, string url, IEnumerable<string>? events, Guid? spaceId)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length is 0 or > MaxNameLength || n.Any(char.IsControl))
        {
            throw new DomainException("webhook.name_invalid", $"A webhook needs a name of 1–{MaxNameLength} characters.");
        }

        if (url.Length > MaxUrlLength)
        {
            throw new DomainException("webhook.url_invalid", $"The address is limited to {MaxUrlLength} characters.");
        }

        var list = (events ?? []).Select(e => e.Trim()).Where(e => e.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        if (list.Count is 0 || list.Count > MaxEvents || list.Any(e => !WebhookEvents.IsKnown(e)))
        {
            throw new DomainException("webhook.events_invalid", "Choose at least one supported event.");
        }

        Name = n;
        Url = url;
        EventsCsv = string.Join(',', list);
        SpaceId = spaceId == Guid.Empty ? null : spaceId;
    }
}

public enum DeliveryStatus
{
    Succeeded = 1,
    Failed = 2,
}

/// <summary>One delivery attempt series of one event to one webhook. Unique per (webhook, event): the idempotency key of the at-least-once outbox.</summary>
public sealed class WebhookDelivery : Entity, ITenantOwned
{
    private WebhookDelivery() { }

    public Guid TenantId { get; private set; }

    public Guid WebhookId { get; private set; }

    public Guid EventId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public DeliveryStatus Status { get; private set; }

    public int? HttpStatus { get; private set; }

    public int Attempts { get; private set; }

    public int DurationMs { get; private set; }

    /// <summary>Stable error code only (never the response body).</summary>
    public string? Error { get; private set; }

    public DateTimeOffset At { get; private set; }

    public static WebhookDelivery Start(Guid webhookId, Guid eventId, string eventType) => new() { WebhookId = webhookId, EventId = eventId, EventType = eventType };

    public void Record(DeliveryStatus status, int? httpStatus, int durationMs, string? error, DateTimeOffset now)
    {
        Attempts++;
        Status = status;
        HttpStatus = httpStatus;
        DurationMs = durationMs;
        Error = error;
        At = now;
    }
}

/// <summary>Tenant-scoped integration state (schema <c>integrations</c>): webhooks, their delivery log and issue connectors. Every table is RLS-protected.</summary>
public sealed class IntegrationsDbContext(DbContextOptions<IntegrationsDbContext> options, ITenantContext tenantContext) : RaaviDbContextBase(options, tenantContext), IHasOutbox
{
    public const string HistorySchema = "integrations";

    public DbSet<Webhook> Webhooks => Set<Webhook>();

    public DbSet<WebhookDelivery> WebhookDeliveries => Set<WebhookDelivery>();

    public DbSet<IssueConnector> IssueConnectors => Set<IssueConnector>();

    public DbSet<PageIssueLink> PageIssueLinks => Set<PageIssueLink>();

    public DbSet<PageIssueLinkContext> PageIssueLinkContexts => Set<PageIssueLinkContext>();

    public DbSet<OutboxMessage> Outbox => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<Webhook>(b =>
        {
            b.ToTable("Webhooks", HistorySchema);
            Keys(b);
            b.Property(x => x.Name).HasMaxLength(Webhook.MaxNameLength);
            b.Property(x => x.Url).HasMaxLength(Webhook.MaxUrlLength);
            b.Property(x => x.ProtectedSecret).HasMaxLength(2000);
            b.Property(x => x.EventsCsv).HasMaxLength(1200).IsUnicode(false);
            b.Property(x => x.DisabledReason).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.Ignore(x => x.Events);
            b.HasIndex(x => new { x.TenantId, x.Enabled });
        });

        modelBuilder.Entity<WebhookDelivery>(b =>
        {
            b.ToTable("WebhookDeliveries", HistorySchema);
            Keys(b);
            b.Property(x => x.EventType).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.Status).HasConversion<int>();
            b.Property(x => x.Error).HasMaxLength(64).IsUnicode(false);
            b.HasIndex(x => new { x.TenantId, x.WebhookId, x.EventId }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.WebhookId, x.At });
            b.HasOne<Webhook>().WithMany().HasForeignKey(x => new { x.TenantId, x.WebhookId }).HasPrincipalKey(w => new { w.TenantId, w.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<IssueConnector>(b =>
        {
            b.ToTable("IssueConnectors", HistorySchema);
            Keys(b);
            b.Property(x => x.Name).HasMaxLength(IssueConnector.MaxNameLength);
            b.Property(x => x.Kind).HasConversion<int>();
            b.Property(x => x.BaseUrl).HasMaxLength(IssueConnector.MaxUrlLength);
            b.Property(x => x.ProtectedToken).HasMaxLength(2000);
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.HasIndex(x => new { x.TenantId, x.Name }).IsUnique();
        });

        modelBuilder.Entity<PageIssueLink>(b =>
        {
            b.ToTable("PageIssueLinks", HistorySchema);
            Keys(b);
            b.Property(x => x.IssueId).HasMaxLength(PageIssueLink.MaxIssueIdLength).IsUnicode(false);
            b.Property(x => x.IssueKey).HasMaxLength(PageIssueLink.MaxKeyLength).IsUnicode(false);
            b.Property(x => x.Summary).HasMaxLength(PageIssueLink.MaxTitleLength);
            b.Property(x => x.Status).HasMaxLength(PageIssueLink.MaxStatusLength);
            b.Property(x => x.IssueType).HasMaxLength(PageIssueLink.MaxTypeLength);
            b.Property(x => x.Priority).HasMaxLength(PageIssueLink.MaxPriorityLength);
            b.Property(x => x.Assignee).HasMaxLength(PageIssueLink.MaxAssigneeLength);
            b.Property(x => x.ProjectKey).HasMaxLength(PageIssueLink.MaxProjectLength).IsUnicode(false);
            b.Property(x => x.IssueUrl).HasMaxLength(PageIssueLink.MaxUrlLength);
            b.Property(x => x.Source).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.StatusFlag).HasConversion<string>().HasMaxLength(16).IsUnicode(false);
            b.Property(x => x.SyncState).HasConversion<string>().HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.RemoteLinkGlobalId).HasMaxLength(PageIssueLink.MaxGlobalIdLength).IsUnicode(false);
            b.Property(x => x.RemoteLinkId).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.LastSyncError).HasMaxLength(PageIssueLink.MaxErrorLength);
            b.HasIndex(x => new { x.TenantId, x.ContentId, x.StatusFlag });
            b.HasIndex(x => new { x.TenantId, x.ConnectorId, x.IssueId, x.StatusFlag });
            b.HasIndex(x => new { x.TenantId, x.ContentId, x.ConnectorId, x.IssueId, x.StatusFlag }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.SyncState, x.UpdatedAt });
            b.HasOne<IssueConnector>().WithMany().HasForeignKey(x => new { x.TenantId, x.ConnectorId }).HasPrincipalKey(c => new { c.TenantId, c.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<PageIssueLinkContext>(b =>
        {
            b.ToTable("PageIssueLinkContexts", HistorySchema);
            Keys(b);
            b.Property(x => x.Exact).HasMaxLength(PageIssueLinkContext.MaxExactLength);
            b.Property(x => x.Prefix).HasMaxLength(PageIssueLinkContext.MaxContextLength);
            b.Property(x => x.Suffix).HasMaxLength(PageIssueLinkContext.MaxContextLength);
            b.Property(x => x.AnchorJson).HasMaxLength(PageIssueLinkContext.MaxAnchorLength);
            b.HasIndex(x => new { x.TenantId, x.LinkId });
            b.HasOne<PageIssueLink>().WithMany().HasForeignKey(x => new { x.TenantId, x.LinkId }).HasPrincipalKey(l => new { l.TenantId, l.Id }).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.ConfigureOutbox(HistorySchema);
        base.OnModelCreating(modelBuilder);
    }

    private static void Keys<T>(EntityTypeBuilder<T> b) where T : class
    {
        b.HasKey("Id").IsClustered(false);
        b.Property<Guid>("Id").ValueGeneratedNever();
        b.HasAlternateKey("TenantId", "Id").IsClustered();
    }
}

public static class IntegrationsMigrations
{
    public static async Task ApplyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(services);
        await using (scope.ConfigureAwait(false))
        {
            var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IntegrationsDbContext>(scope.ServiceProvider);
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
