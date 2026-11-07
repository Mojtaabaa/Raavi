using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Persistence;

namespace Raavi.Migration.Persistence;

public enum MigrationMode
{
    /// <summary>Reads and converts everything, writes nothing to the workspace, produces the report.</summary>
    DryRun = 1,
    Execute = 2,
}

public enum MigrationStatus
{
    Created = 1,
    Queued = 2,
    Running = 3,
    Paused = 4,
    Cancelled = 5,
    Completed = 6,
    CompletedWithErrors = 7,
    Failed = 8,
}

/// <summary>A request the administrator makes while a job runs; the runner honours it between pages.</summary>
public enum MigrationControl
{
    None = 0,
    Pause = 1,
    Cancel = 2,
}

public enum MigrationEventLevel
{
    Info = 1,
    Warning = 2,
    Error = 3,
}

/// <summary>
/// One import of a Confluence source into <b>this</b> tenant (CONFLUENCE_MIGRATION.md §2). The tenant is fixed when the job
/// is created from the administrator's tenant context; every batch runs in a tenant scope for it. The source access token
/// is stored only protected (ASP.NET Data Protection, purpose bound to the tenant) and is erased as soon as the job ends.
/// </summary>
public sealed class MigrationJob : Entity, ITenantOwned
{
    private MigrationJob() { }

    public Guid TenantId { get; private set; }

    public int SourceKind { get; private set; }

    public string SourceUrl { get; private set; } = string.Empty;

    /// <summary>Short stable id of the source (hash of its address); part of every imported item's provenance.</summary>
    public string SourceKey { get; private set; } = string.Empty;

    public string? SourceVersion { get; private set; }

    public MigrationMode Mode { get; private set; }

    public MigrationStatus Status { get; private set; } = MigrationStatus.Created;

    public MigrationControl Control { get; private set; }

    public string OptionsJson { get; private set; } = "{}";

    public string? ProtectedToken { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? StartedAt { get; private set; }

    public DateTimeOffset? FinishedAt { get; private set; }

    public string? CurrentOperation { get; private set; }

    public int SpacesTotal { get; private set; }

    public int SpacesDone { get; private set; }

    public int PagesTotal { get; private set; }

    public int PagesDone { get; private set; }

    public int PagesFailed { get; private set; }

    public int VersionsDone { get; private set; }

    public int WarningCount { get; private set; }

    public string? ReportJson { get; private set; }

    public static MigrationJob Create(int sourceKind, string sourceUrl, string sourceKey, MigrationMode mode, string optionsJson, string protectedToken, Guid createdBy, DateTimeOffset now) => new()
    {
        SourceKind = sourceKind,
        SourceUrl = sourceUrl,
        SourceKey = sourceKey,
        Mode = mode,
        OptionsJson = optionsJson,
        ProtectedToken = protectedToken,
        CreatedBy = createdBy,
        CreatedAt = now,
    };

    public bool IsActive => Status is MigrationStatus.Queued or MigrationStatus.Running;

    public bool IsFinished => Status is MigrationStatus.Cancelled or MigrationStatus.Completed or MigrationStatus.CompletedWithErrors or MigrationStatus.Failed;

    public void SetToken(string protectedToken) => ProtectedToken = protectedToken;

    public void Queue()
    {
        if (IsActive)
        {
            throw new DomainException("migration.already_running", "This migration is already running.");
        }

        if (Status is MigrationStatus.Cancelled or MigrationStatus.Failed && ProtectedToken is null)
        {
            throw new DomainException("migration.token_required", "Provide the source access token again to restart this migration.");
        }

        Status = MigrationStatus.Queued;
        Control = MigrationControl.None;
    }

    public void Start(DateTimeOffset now, string? sourceVersion)
    {
        Status = MigrationStatus.Running;
        StartedAt ??= now;
        FinishedAt = null;
        SourceVersion = sourceVersion ?? SourceVersion;
    }

    public void Progress(string operation, int spacesTotal, int spacesDone, int pagesTotal, int pagesDone, int pagesFailed, int versionsDone, int warnings)
    {
        CurrentOperation = operation.Length > 200 ? operation[..200] : operation;
        SpacesTotal = spacesTotal;
        SpacesDone = spacesDone;
        PagesTotal = pagesTotal;
        PagesDone = pagesDone;
        PagesFailed = pagesFailed;
        VersionsDone = versionsDone;
        WarningCount = warnings;
    }

    public void Finish(MigrationStatus status, string? reportJson, DateTimeOffset now)
    {
        Status = status;
        Control = MigrationControl.None;
        FinishedAt = now;
        ReportJson = reportJson ?? ReportJson;
        CurrentOperation = null;
        // The token has done its job; a paused migration keeps it so it can resume.
        if (status != MigrationStatus.Paused)
        {
            ProtectedToken = null;
        }
    }
}

/// <summary>A progress, warning or failure record of a job (the dry-run/validation report is built from these and the totals).</summary>
public sealed class MigrationEvent : Entity, ITenantOwned
{
    private MigrationEvent() { }

    public Guid TenantId { get; private set; }

    public Guid JobId { get; private set; }

    public DateTimeOffset At { get; private set; }

    public MigrationEventLevel Level { get; private set; }

    public string Code { get; private set; } = string.Empty;

    /// <summary>Where it happened in the source (space key, page id/title).</summary>
    public string? SourceRef { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public static MigrationEvent Create(Guid jobId, MigrationEventLevel level, string code, string? sourceRef, string message, DateTimeOffset now) => new()
    {
        JobId = jobId,
        Level = level,
        Code = code.Length > 64 ? code[..64] : code,
        SourceRef = sourceRef is { Length: > 300 } ? sourceRef[..300] : sourceRef,
        Message = message.Length > 1000 ? message[..1000] : message,
        At = now,
    };
}

/// <summary>Tenant-scoped migration state (schema <c>migration</c>): jobs and their events. Every table is RLS-protected.</summary>
public sealed class MigrationDbContext(DbContextOptions<MigrationDbContext> options, ITenantContext tenantContext) : RaaviDbContextBase(options, tenantContext)
{
    public const string HistorySchema = "migration";

    public DbSet<MigrationJob> Jobs => Set<MigrationJob>();

    public DbSet<MigrationEvent> Events => Set<MigrationEvent>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<MigrationJob>(b =>
        {
            b.ToTable("Jobs", HistorySchema);
            Keys(b);
            b.Property(x => x.SourceUrl).HasMaxLength(500);
            b.Property(x => x.SourceKey).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.SourceVersion).HasMaxLength(64);
            b.Property(x => x.Mode).HasConversion<int>();
            b.Property(x => x.Status).HasConversion<int>();
            b.Property(x => x.Control).HasConversion<int>();
            b.Property(x => x.OptionsJson).HasMaxLength(4000);
            b.Property(x => x.ProtectedToken).HasMaxLength(2000);
            b.Property(x => x.CurrentOperation).HasMaxLength(200);
            b.Ignore(x => x.IsActive);
            b.Ignore(x => x.IsFinished);
            b.HasIndex(x => new { x.TenantId, x.CreatedAt });
        });

        modelBuilder.Entity<MigrationEvent>(b =>
        {
            b.ToTable("Events", HistorySchema);
            Keys(b);
            b.Property(x => x.Level).HasConversion<int>();
            b.Property(x => x.Code).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.SourceRef).HasMaxLength(300);
            b.Property(x => x.Message).HasMaxLength(1000);
            b.HasIndex(x => new { x.TenantId, x.JobId, x.At });
            b.HasOne<MigrationJob>().WithMany().HasForeignKey(x => new { x.TenantId, x.JobId }).HasPrincipalKey(j => new { j.TenantId, j.Id }).OnDelete(DeleteBehavior.Restrict);
        });

        base.OnModelCreating(modelBuilder);
    }

    private static void Keys<T>(EntityTypeBuilder<T> b) where T : class
    {
        b.HasKey("Id").IsClustered(false);
        b.Property<Guid>("Id").ValueGeneratedNever();
        b.HasAlternateKey("TenantId", "Id").IsClustered();
    }
}

public static class MigrationMigrations
{
    public static async Task ApplyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(services);
        await using (scope.ConfigureAwait(false))
        {
            var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<MigrationDbContext>(scope.ServiceProvider);
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
