using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Persistence;

namespace Raavi.AI.Persistence;

/// <summary>
/// The AI provider of one tenant (one row per tenant). The API key is stored only protected (ASP.NET Data Protection, purpose bound to the
/// tenant), is never returned by the API and never logged.
/// </summary>
public sealed class AiSettings : Entity, ITenantOwned
{
    public const int MaxUrlLength = 300;
    public const int MaxModelLength = 100;
    public const int MaxOutputTokens = 4096;

    private AiSettings() { }

    public Guid TenantId { get; private set; }

    public bool Enabled { get; private set; }

    public string BaseUrl { get; private set; } = string.Empty;

    public string Model { get; private set; } = string.Empty;

    public string? ProtectedApiKey { get; private set; }

    /// <summary>Embedding model for smart search (same provider and key). Empty = keyword search only.</summary>
    public string EmbeddingModel { get; private set; } = string.Empty;

    /// <summary>Tokens (prompt + completion) all members together may use per calendar month (UTC); 0 = unlimited.</summary>
    public int MonthlyTokenCap { get; private set; }

    public int MaxOutputTokensPerRequest { get; private set; } = 800;

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>0 until the first save, so the first stored revision is 1.</summary>
    public int Revision { get; private set; }

    public static AiSettings Create(Guid userId, DateTimeOffset now) => new() { UpdatedBy = userId, UpdatedAt = now };

    /// <summary>Replaces the settings. <paramref name="protectedKey"/> null keeps the stored key; an empty string removes it.</summary>
    public void Update(bool enabled, string baseUrl, string? model, string? protectedKey, int monthlyTokenCap, int maxOutputTokens, Guid userId, DateTimeOffset now, string? embeddingModel = null)
    {
        var em = (embeddingModel ?? string.Empty).Trim();
        if (em.Length > MaxModelLength || em.Any(char.IsControl))
        {
            throw new DomainException("ai.model_invalid", $"The model name needs 1–{MaxModelLength} characters.");
        }

        EmbeddingModel = em;
        var m = (model ?? string.Empty).Trim();
        if (m.Length is 0 or > MaxModelLength || m.Any(char.IsControl))
        {
            throw new DomainException("ai.model_invalid", $"The model name needs 1–{MaxModelLength} characters.");
        }

        if (monthlyTokenCap is < 0 or > 1_000_000_000)
        {
            throw new DomainException("ai.cap_invalid", "The monthly token cap must be between 0 and 1 000 000 000.");
        }

        if (maxOutputTokens is < 64 or > MaxOutputTokens)
        {
            throw new DomainException("ai.output_invalid", $"The answer length must be between 64 and {MaxOutputTokens} tokens.");
        }

        Enabled = enabled;
        BaseUrl = baseUrl;
        Model = m;
        if (protectedKey is not null)
        {
            ProtectedApiKey = protectedKey.Length == 0 ? null : protectedKey;
        }

        MonthlyTokenCap = monthlyTokenCap;
        MaxOutputTokensPerRequest = maxOutputTokens;
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }
}

public enum AiOutcome
{
    Succeeded = 1,
    Failed = 2,
}

/// <summary>One model call: who, which feature, how many tokens. No prompt, no answer, no page content is stored.</summary>
public sealed class AiUsage : Entity, ITenantOwned
{
    private AiUsage() { }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public string Feature { get; private set; } = string.Empty;

    public string Model { get; private set; } = string.Empty;

    public int PromptTokens { get; private set; }

    public int CompletionTokens { get; private set; }

    public AiOutcome Outcome { get; private set; }

    public string? ErrorCode { get; private set; }

    public DateTimeOffset At { get; private set; }

    public static AiUsage Record(Guid userId, string feature, string model, int prompt, int completion, AiOutcome outcome, string? errorCode, DateTimeOffset now) =>
        new() { UserId = userId, Feature = feature, Model = model, PromptTokens = prompt, CompletionTokens = completion, Outcome = outcome, ErrorCode = errorCode, At = now };
}

/// <summary>One searchable passage of a page (ADR-049). A copy of content: it never decides who may see it (the Wiki module's permission check does, at query time).</summary>
public sealed class AiChunk : Entity, ITenantOwned
{
    public const int MaxTextLength = 1300;

    private AiChunk() { }

    public Guid TenantId { get; private set; }

    public Guid ContentId { get; private set; }

    public Guid SpaceId { get; private set; }

    public Guid VersionId { get; private set; }

    public int Position { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string SpaceKey { get; private set; } = string.Empty;

    public string Text { get; private set; } = string.Empty;

    /// <summary>The text folded for searching (<see cref="Raavi.Application.Common.SearchText"/>): the keyword search matches against this, so spelling variants meet.</summary>
    public string Norm { get; private set; } = string.Empty;

    public string TitleNorm { get; private set; } = string.Empty;

    /// <summary>Unit-length float32 vector (little-endian), or null when no embedding model was configured when the page was indexed.</summary>
    public byte[]? Embedding { get; private set; }

    public static AiChunk Create(Guid contentId, Guid spaceId, Guid versionId, int position, string title, string spaceKey, string text, byte[]? embedding) =>
        new()
        {
            ContentId = contentId, SpaceId = spaceId, VersionId = versionId, Position = position, Title = title, SpaceKey = spaceKey, Text = text, Embedding = embedding,
            Norm = Raavi.Application.Common.SearchText.Normalize(text), TitleNorm = Raavi.Application.Common.SearchText.Normalize(title),
        };
}

/// <summary>Which version of which page the index holds, and with which embedding model: the index job's memory (a changed version or model means re-index).</summary>
public sealed class AiIndexedPage : Entity, ITenantOwned
{
    private AiIndexedPage() { }

    public Guid TenantId { get; private set; }

    public Guid ContentId { get; private set; }

    public Guid VersionId { get; private set; }

    public int ChunkCount { get; private set; }

    /// <summary>The embedding model used ("" = none).</summary>
    public string EmbeddingModel { get; private set; } = string.Empty;

    public DateTimeOffset IndexedAt { get; private set; }

    public static AiIndexedPage Create(Guid contentId, Guid versionId, int chunks, string embeddingModel, DateTimeOffset now) =>
        new() { ContentId = contentId, VersionId = versionId, ChunkCount = chunks, EmbeddingModel = embeddingModel, IndexedAt = now };

    public void Update(Guid versionId, int chunks, string embeddingModel, DateTimeOffset now)
    {
        VersionId = versionId;
        ChunkCount = chunks;
        EmbeddingModel = embeddingModel;
        IndexedAt = now;
    }
}

/// <summary>Tenant-scoped AI state (schema <c>ai</c>): settings, usage and the search index. Every table is RLS-protected.</summary>
public sealed class AiDbContext(DbContextOptions<AiDbContext> options, ITenantContext tenantContext) : RaaviDbContextBase(options, tenantContext)
{
    public const string HistorySchema = "ai";

    public DbSet<AiSettings> Settings => Set<AiSettings>();

    public DbSet<AiUsage> Usage => Set<AiUsage>();

    public DbSet<AiChunk> Chunks => Set<AiChunk>();

    public DbSet<AiIndexedPage> IndexedPages => Set<AiIndexedPage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<AiSettings>(b =>
        {
            b.ToTable("Settings", HistorySchema);
            Keys(b);
            b.Property(x => x.BaseUrl).HasMaxLength(AiSettings.MaxUrlLength);
            b.Property(x => x.Model).HasMaxLength(AiSettings.MaxModelLength);
            b.Property(x => x.ProtectedApiKey).HasMaxLength(2000);
            b.Property(x => x.EmbeddingModel).HasMaxLength(AiSettings.MaxModelLength);
            b.Property(x => x.Revision).IsConcurrencyToken();
            b.HasIndex(x => x.TenantId).IsUnique();
        });

        modelBuilder.Entity<AiUsage>(b =>
        {
            b.ToTable("Usage", HistorySchema);
            Keys(b);
            b.Property(x => x.Feature).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.Model).HasMaxLength(AiSettings.MaxModelLength);
            b.Property(x => x.ErrorCode).HasMaxLength(64).IsUnicode(false);
            b.Property(x => x.Outcome).HasConversion<int>();
            b.HasIndex(x => new { x.TenantId, x.At });
            b.HasIndex(x => new { x.TenantId, x.UserId, x.At });
        });

        modelBuilder.Entity<AiChunk>(b =>
        {
            b.ToTable("Chunks", HistorySchema);
            Keys(b);
            b.Property(x => x.Title).HasMaxLength(255);
            b.Property(x => x.SpaceKey).HasMaxLength(32).IsUnicode(false);
            b.Property(x => x.Text).HasMaxLength(AiChunk.MaxTextLength);
            b.Property(x => x.Norm).HasMaxLength(AiChunk.MaxTextLength);
            b.Property(x => x.TitleNorm).HasMaxLength(255);
            b.Property(x => x.Embedding).HasColumnType("varbinary(max)");
            b.HasIndex(x => new { x.TenantId, x.ContentId, x.Position }).IsUnique();
            b.HasIndex(x => new { x.TenantId, x.SpaceKey });
        });

        modelBuilder.Entity<AiIndexedPage>(b =>
        {
            b.ToTable("IndexedPages", HistorySchema);
            Keys(b);
            b.Property(x => x.EmbeddingModel).HasMaxLength(AiSettings.MaxModelLength);
            b.HasIndex(x => new { x.TenantId, x.ContentId }).IsUnique();
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

public static class AiMigrations
{
    public static async Task ApplyAsync(IServiceProvider services, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(services);
        var scope = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.CreateAsyncScope(services);
        await using (scope.ConfigureAwait(false))
        {
            var db = Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<AiDbContext>(scope.ServiceProvider);
            await db.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
