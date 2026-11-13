using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

/// <summary>Extensible classification labels (not ACL). Initial set: Public, Internal, Confidential, Restricted.</summary>
public static class ContentClassifications
{
    public const string Public = "Public";
    public const string Internal = "Internal";
    public const string Confidential = "Confidential";
    public const string Restricted = "Restricted";
    public const int MaxLength = 32;

    public static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        Public, Internal, Confidential, Restricted,
    };

    public static string Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length is 0 or > MaxLength || v.Any(char.IsControl))
        {
            throw new DomainException("governance.classification_invalid", "Classification is not valid.");
        }

        foreach (var known in BuiltIn)
        {
            if (string.Equals(known, v, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        // Extensible: allow tenant-configured future values that look like identifiers.
        if (!v.All(c => char.IsLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new DomainException("governance.classification_invalid", "Classification is not valid.");
        }

        return v;
    }

    /// <summary>External AI / broad export should not receive Restricted bodies by default.</summary>
    public static bool AllowsExternalAi(string classification) =>
        !string.Equals(classification, Restricted, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Stewardship lifecycle (separate from <see cref="ContentStatus"/>).</summary>
public static class GovernanceLifecycles
{
    public const string Draft = "Draft";
    public const string Current = "Current";
    public const string InReview = "InReview";
    public const string Approved = "Approved";
    public const string Archived = "Archived";
    public const string Expired = "Expired";
    public const int MaxLength = 32;

    public static readonly HashSet<string> BuiltIn = new(StringComparer.OrdinalIgnoreCase)
    {
        Draft, Current, InReview, Approved, Archived, Expired,
    };

    public static string Normalize(string? value)
    {
        var v = (value ?? string.Empty).Trim();
        if (v.Length is 0 or > MaxLength)
        {
            throw new DomainException("governance.lifecycle_invalid", "Lifecycle status is not valid.");
        }

        foreach (var known in BuiltIn)
        {
            if (string.Equals(known, v, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        throw new DomainException("governance.lifecycle_invalid", "Lifecycle status is not valid.");
    }

    public static string FromContentStatus(ContentStatus status) => status switch
    {
        ContentStatus.Draft => Draft,
        ContentStatus.Archived => Archived,
        ContentStatus.Trashed => Archived,
        _ => Current,
    };
}

/// <summary>1:1 governance metadata for a content node (ADR-054). Id equals Content.Id.</summary>
public sealed class ContentGovernance : Entity, ITenantOwned
{
    private ContentGovernance() { }

    public Guid TenantId { get; private set; }

    public Guid OwnerUserId { get; private set; }

    public Guid? ReviewOwnerUserId { get; private set; }

    public DateTimeOffset? ReviewAt { get; private set; }

    public DateTimeOffset? LastReviewedAt { get; private set; }

    public string Classification { get; private set; } = ContentClassifications.Internal;

    public string LifecycleStatus { get; private set; } = GovernanceLifecycles.Current;

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static ContentGovernance CreateForNew(Guid contentId, Guid ownerUserId, ContentStatus contentStatus, DateTimeOffset now) => new()
    {
        Id = contentId,
        OwnerUserId = ownerUserId,
        Classification = ContentClassifications.Internal,
        LifecycleStatus = GovernanceLifecycles.FromContentStatus(contentStatus),
        UpdatedBy = ownerUserId,
        UpdatedAt = now,
    };

    public void Apply(Guid? ownerUserId, Guid? reviewOwnerUserId, bool clearReviewOwner, DateTimeOffset? reviewAt, bool clearReviewAt,
        string? classification, string? lifecycleStatus, bool markReviewed, Guid actorId, DateTimeOffset now)
    {
        if (ownerUserId is { } o && o != Guid.Empty)
        {
            OwnerUserId = o;
        }

        if (clearReviewOwner)
        {
            ReviewOwnerUserId = null;
        }
        else if (reviewOwnerUserId is { } ro)
        {
            ReviewOwnerUserId = ro == Guid.Empty ? null : ro;
        }

        if (clearReviewAt)
        {
            ReviewAt = null;
        }
        else if (reviewAt.HasValue)
        {
            ReviewAt = reviewAt;
        }

        if (classification is not null)
        {
            Classification = ContentClassifications.Normalize(classification);
        }

        if (lifecycleStatus is not null)
        {
            LifecycleStatus = GovernanceLifecycles.Normalize(lifecycleStatus);
        }

        if (markReviewed)
        {
            LastReviewedAt = now;
            if (LifecycleStatus is GovernanceLifecycles.InReview or GovernanceLifecycles.Draft)
            {
                LifecycleStatus = GovernanceLifecycles.Current;
            }
        }

        UpdatedBy = actorId;
        UpdatedAt = now;
    }
}
