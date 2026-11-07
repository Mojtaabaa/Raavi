using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

/// <summary>
/// The shared, autosaved draft of a page (ADR-019): one per content item, edited by everyone with Edit, never visible
/// to readers. Publishing turns it into an immutable <see cref="PageVersion"/>. Phase 6 backs it with the
/// collaboration document; the stored JSON stays the canonical form.
/// </summary>
public sealed class PageDraft : Entity, ITenantOwned
{
    private PageDraft() { }

    public Guid TenantId { get; private set; }

    public Guid ContentId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string ContentJson { get; private set; } = string.Empty;

    public string ContentHash { get; private set; } = string.Empty;

    /// <summary>Published version the draft started from (0 for never-published pages).</summary>
    public int BaseVersionNumber { get; private set; }

    /// <summary>Space-separated user ids of everyone who changed the draft since the last publish.</summary>
    public string Contributors { get; private set; } = string.Empty;

    public Guid UpdatedBy { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? ScheduledPublishAt { get; private set; }

    public Guid? ScheduledBy { get; private set; }

    public string? ScheduledChangeSummary { get; private set; }

    /// <summary>Optimistic concurrency for autosave (ETag of the draft resource).</summary>
    public int Revision { get; private set; }

    public IReadOnlyList<Guid> ContributorIds =>
        Contributors.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();

    public static PageDraft Start(Guid contentId, string title, RaaviDocument body, int baseVersion, Guid userId, DateTimeOffset now)
    {
        var d = new PageDraft { ContentId = contentId, BaseVersionNumber = baseVersion };
        d.Save(title, body, userId, now);
        return d;
    }

    public void Save(string title, RaaviDocument body, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(body);
        Title = ContentNode.NormalizeTitle(title);
        ContentJson = body.Json;
        ContentHash = body.Hash;
        UpdatedBy = userId;
        UpdatedAt = now;
        if (!ContributorIds.Contains(userId))
        {
            Contributors = (Contributors + " " + userId.ToString("D")).Trim();
        }

        Revision++;
    }

    public void SaveTitle(string title, Guid userId, DateTimeOffset now)
    {
        Title = ContentNode.NormalizeTitle(title);
        UpdatedBy = userId;
        UpdatedAt = now;
        AddContributor(userId);
        Revision++;
    }

    /// <summary>Body written by the collaboration server (the live document); the title is edited separately.</summary>
    public void SaveBody(RaaviDocument body, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(body);
        ContentJson = body.Json;
        ContentHash = body.Hash;
        UpdatedBy = userId;
        UpdatedAt = now;
        AddContributor(userId);
        Revision++;
    }

    public void AddContributor(Guid userId)
    {
        if (userId != Guid.Empty && !ContributorIds.Contains(userId))
        {
            Contributors = (Contributors + " " + userId.ToString("D")).Trim();
        }
    }

    public void Schedule(DateTimeOffset at, string? changeSummary, Guid userId)
    {
        ScheduledPublishAt = at;
        ScheduledBy = userId;
        ScheduledChangeSummary = string.IsNullOrWhiteSpace(changeSummary) ? null : changeSummary.Trim();
        Revision++;
    }

    public void CancelSchedule()
    {
        ScheduledPublishAt = null;
        ScheduledBy = null;
        ScheduledChangeSummary = null;
        Revision++;
    }
}

/// <summary>Everyone who contributed to a published version (all draft editors since the previous publish).</summary>
public sealed class PageVersionContributor : ITenantOwned
{
    private PageVersionContributor() { }

    public Guid TenantId { get; private set; }

    public Guid VersionId { get; private set; }

    public Guid UserId { get; private set; }

    public static PageVersionContributor Create(Guid versionId, Guid userId) => new() { VersionId = versionId, UserId = userId };
}

public enum PublishRequestStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Withdrawn = 4,

    /// <summary>Replaced by a newer request for the same page.</summary>
    Superseded = 5,
}

/// <summary>
/// Approval foundation (ADR-019): in spaces that require approval, publishing creates a request holding an immutable
/// snapshot of the proposed version; a space administrator's approval publishes exactly that snapshot.
/// Full workflows arrive in Phase 10.
/// </summary>
public sealed class PublishRequest : Entity, ITenantOwned
{
    private PublishRequest() { }

    public Guid TenantId { get; private set; }

    public Guid ContentId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string ContentJson { get; private set; } = string.Empty;

    public string ContentHash { get; private set; } = string.Empty;

    public string? ChangeSummary { get; private set; }

    public string Contributors { get; private set; } = string.Empty;

    public int BaseVersionNumber { get; private set; }

    public PublishRequestStatus Status { get; private set; } = PublishRequestStatus.Pending;

    public Guid RequestedBy { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public Guid? DecidedBy { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public string? DecisionComment { get; private set; }

    public static PublishRequest From(PageDraft draft, string? changeSummary, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(draft);
        return new PublishRequest
        {
            ContentId = draft.ContentId,
            Title = draft.Title,
            ContentJson = draft.ContentJson,
            ContentHash = draft.ContentHash,
            ChangeSummary = string.IsNullOrWhiteSpace(changeSummary) ? null : changeSummary.Trim(),
            Contributors = draft.Contributors,
            BaseVersionNumber = draft.BaseVersionNumber,
            RequestedBy = userId,
            RequestedAt = now,
        };
    }

    public void Decide(PublishRequestStatus status, Guid userId, string? comment, DateTimeOffset now)
    {
        if (Status != PublishRequestStatus.Pending)
        {
            throw new DomainException("publish_request.not_pending", "This request has already been decided.");
        }

        var c = comment?.Trim();
        if (c is { Length: > 1000 })
        {
            throw new DomainException("publish_request.comment_too_long", "The comment is too long.");
        }

        Status = status;
        DecidedBy = userId;
        DecidedAt = now;
        DecisionComment = string.IsNullOrEmpty(c) ? null : c;
    }

    public IReadOnlyList<Guid> ContributorIds =>
        Contributors.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList();
}
