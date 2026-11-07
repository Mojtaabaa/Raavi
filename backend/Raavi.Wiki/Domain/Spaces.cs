using System.Text.RegularExpressions;
using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

/// <summary>Values are persisted; never renumber.</summary>
public enum SpaceType
{
    Team = 1,
    Project = 2,
    Documentation = 3,
    Personal = 4,
    KnowledgeBase = 5,
}

public enum SpaceStatus
{
    Active = 1,
    Archived = 2,
}

/// <summary>A space: the top-level container of content, with its own permissions (ADR-012 "Space" scope).</summary>
public sealed partial class Space : Entity, ITenantOwned
{
    public const int MaxKeyLength = 32;
    public const int MaxNameLength = 200;
    public const int MaxDescriptionLength = 2000;

    private Space() { }

    public Guid TenantId { get; private set; }

    /// <summary>Upper-case machine key, unique per tenant, used in URLs (e.g. <c>ENG</c>). Immutable.</summary>
    public string Key { get; private set; } = string.Empty;

    public string Name { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public SpaceType Type { get; private set; }

    public SpaceStatus Status { get; private set; } = SpaceStatus.Active;

    public Guid? HomePageId { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>Optimistic concurrency revision, exposed as the ETag.</summary>
    public int Revision { get; private set; } = 1;

    /// <summary>Publishing needs a space administrator's approval (ADR-019 approval foundation).</summary>
    public bool RequirePublishApproval { get; private set; }

    /// <summary>Import provenance (e.g. <c>confluence:ab12cd34ef56</c>) and the space's identifier in the source; null for native spaces.</summary>
    public string? SourceSystem { get; private set; }

    public string? SourceId { get; private set; }

    public void MarkImported(string sourceSystem, string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        SourceSystem = sourceSystem;
        SourceId = sourceId;
    }

    public void SetPublishApproval(bool required, Guid userId, DateTimeOffset now)
    {
        EnsureActive();
        RequirePublishApproval = required;
        Touch(userId, now);
    }

    public static string NormalizeKey(string? key) => (key ?? string.Empty).Trim().ToUpperInvariant();

    public static Space Create(string key, string name, string? description, SpaceType type, Guid userId, DateTimeOffset now)
    {
        var k = NormalizeKey(key);
        if (!KeyPattern().IsMatch(k))
        {
            throw new DomainException("space.key_invalid", "Space key must be 2–32 characters: A–Z and digits, starting with a letter.");
        }

        var space = new Space { Key = k, CreatedBy = userId, CreatedAt = now, UpdatedBy = userId, UpdatedAt = now };
        space.Apply(name, description, type);
        return space;
    }

    public void Update(string name, string? description, SpaceType type, Guid userId, DateTimeOffset now)
    {
        EnsureActive();
        Apply(name, description, type);
        Touch(userId, now);
    }

    public void SetHomePage(Guid? contentId, Guid userId, DateTimeOffset now)
    {
        EnsureActive();
        HomePageId = contentId;
        Touch(userId, now);
    }

    public void Archive(Guid userId, DateTimeOffset now)
    {
        EnsureActive();
        Status = SpaceStatus.Archived;
        Touch(userId, now);
    }

    public void Restore(Guid userId, DateTimeOffset now)
    {
        if (Status != SpaceStatus.Archived)
        {
            throw new DomainException("space.not_archived", "The space is not archived.");
        }

        Status = SpaceStatus.Active;
        Touch(userId, now);
    }

    public void EnsureActive()
    {
        if (Status != SpaceStatus.Active)
        {
            throw new DomainException("space.archived", "The space is archived and read-only.");
        }
    }

    private void Apply(string name, string? description, SpaceType type)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length is 0 or > MaxNameLength)
        {
            throw new DomainException("space.name_invalid", "Space name is required (max 200 characters).");
        }

        var d = description?.Trim();
        if (d is { Length: > MaxDescriptionLength })
        {
            throw new DomainException("space.description_too_long", "Description is too long.");
        }

        if (!Enum.IsDefined(type))
        {
            throw new DomainException("space.type_invalid", "Unknown space type.");
        }

        Name = n;
        Description = string.IsNullOrEmpty(d) ? null : d;
        Type = type;
    }

    private void Touch(Guid userId, DateTimeOffset now)
    {
        UpdatedBy = userId;
        UpdatedAt = now;
        Revision++;
    }

    [GeneratedRegex("^[A-Z][A-Z0-9]{1,31}$", RegexOptions.CultureInvariant)]
    private static partial Regex KeyPattern();
}
