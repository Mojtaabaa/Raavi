using Raavi.Domain.Common;
using Raavi.Application.Abstractions.Tenancy;

namespace Raavi.Integrations.Persistence;

public enum PageIssueLinkSource
{
    CreatedFromRaavi = 1,
    LinkedExistingIssue = 2,
    Imported = 3,
    Other = 4,
}

public enum PageIssueLinkStatus
{
    Active = 1,
    Unlinked = 2,
}

public enum PageIssueSyncState
{
    Synced = 1,
    PendingRemoteLink = 2,
    RemoteLinkFailed = 3,
    IssueUnavailable = 4,
}

/// <summary>
/// Authoritative Raavi Page↔Jira Issue relationship (many-to-many). Issue fields are cached snapshots; Jira remains source of truth for workflow.
/// Soft-unlinking keeps audit history and never deletes the Jira issue or other pages' links.
/// </summary>
public sealed class PageIssueLink : Entity, ITenantOwned
{
    public const int MaxKeyLength = 32;
    public const int MaxIssueIdLength = 64;
    public const int MaxTitleLength = 500;
    public const int MaxStatusLength = 100;
    public const int MaxTypeLength = 100;
    public const int MaxPriorityLength = 100;
    public const int MaxAssigneeLength = 200;
    public const int MaxProjectLength = 64;
    public const int MaxUrlLength = 1000;
    public const int MaxErrorLength = 500;
    public const int MaxGlobalIdLength = 200;

    private PageIssueLink() { }

    public Guid TenantId { get; private set; }

    public Guid ContentId { get; private set; }

    public Guid SpaceId { get; private set; }

    public Guid ConnectorId { get; private set; }

    /// <summary>Jira stable issue id (numeric string).</summary>
    public string IssueId { get; private set; } = string.Empty;

    public string IssueKey { get; private set; } = string.Empty;

    public string? Summary { get; private set; }

    public string? Status { get; private set; }

    public string? IssueType { get; private set; }

    public string? Priority { get; private set; }

    public string? Assignee { get; private set; }

    public string? ProjectKey { get; private set; }

    public string? IssueUrl { get; private set; }

    public PageIssueLinkSource Source { get; private set; }

    public PageIssueLinkStatus StatusFlag { get; private set; } = PageIssueLinkStatus.Active;

    public PageIssueSyncState SyncState { get; private set; } = PageIssueSyncState.PendingRemoteLink;

    public string RemoteLinkGlobalId { get; private set; } = string.Empty;

    public string? RemoteLinkId { get; private set; }

    public DateTimeOffset? LastSyncedAt { get; private set; }

    public string? LastSyncError { get; private set; }

    public int SyncAttempts { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public Guid? UnlinkedBy { get; private set; }

    public DateTimeOffset? UnlinkedAt { get; private set; }

    public static PageIssueLink Create(
        Guid contentId,
        Guid spaceId,
        Guid connectorId,
        string issueId,
        string issueKey,
        PageIssueLinkSource source,
        string remoteLinkGlobalId,
        Guid userId,
        DateTimeOffset now,
        string? summary = null,
        string? status = null,
        string? issueType = null,
        string? priority = null,
        string? assignee = null,
        string? projectKey = null,
        string? issueUrl = null)
    {
        var link = new PageIssueLink
        {
            ContentId = contentId,
            SpaceId = spaceId,
            ConnectorId = connectorId,
            Source = source,
            RemoteLinkGlobalId = Clamp(remoteLinkGlobalId, MaxGlobalIdLength) ?? string.Empty,
            CreatedBy = userId,
            CreatedAt = now,
            UpdatedAt = now,
            SyncState = PageIssueSyncState.PendingRemoteLink,
        };
        link.ApplyIssue(issueId, issueKey, summary, status, issueType, priority, assignee, projectKey, issueUrl, now);
        if (link.RemoteLinkGlobalId.Length == 0)
        {
            throw new DomainException("jira_link.global_id_invalid", "Remote link identity is missing.");
        }

        return link;
    }

    public void ApplyIssue(
        string issueId,
        string issueKey,
        string? summary,
        string? status,
        string? issueType,
        string? priority,
        string? assignee,
        string? projectKey,
        string? issueUrl,
        DateTimeOffset now)
    {
        var id = (issueId ?? string.Empty).Trim();
        var key = (issueKey ?? string.Empty).Trim().ToUpperInvariant();
        if (id.Length is 0 or > MaxIssueIdLength || key.Length is 0 or > MaxKeyLength)
        {
            throw new DomainException("jira_link.issue_invalid", "The Jira issue identity is not valid.");
        }

        IssueId = id;
        IssueKey = key;
        Summary = Clamp(summary, MaxTitleLength);
        Status = Clamp(status, MaxStatusLength);
        IssueType = Clamp(issueType, MaxTypeLength);
        Priority = Clamp(priority, MaxPriorityLength);
        Assignee = Clamp(assignee, MaxAssigneeLength);
        ProjectKey = Clamp(projectKey, MaxProjectLength);
        IssueUrl = Clamp(issueUrl, MaxUrlLength);
        UpdatedAt = now;
    }

    public void MarkRemoteLink(string? remoteLinkId, DateTimeOffset now)
    {
        RemoteLinkId = Clamp(remoteLinkId, 64);
        SyncState = PageIssueSyncState.Synced;
        LastSyncedAt = now;
        LastSyncError = null;
        UpdatedAt = now;
    }

    public void MarkSyncFailure(string? error, DateTimeOffset now)
    {
        SyncAttempts++;
        SyncState = PageIssueSyncState.RemoteLinkFailed;
        LastSyncError = Clamp(error, MaxErrorLength);
        UpdatedAt = now;
    }

    public void MarkPending(DateTimeOffset now)
    {
        SyncState = PageIssueSyncState.PendingRemoteLink;
        UpdatedAt = now;
    }

    public void MarkUnavailable(string? error, DateTimeOffset now)
    {
        SyncState = PageIssueSyncState.IssueUnavailable;
        LastSyncError = Clamp(error, MaxErrorLength);
        UpdatedAt = now;
    }

    public void Unlink(Guid userId, DateTimeOffset now)
    {
        if (StatusFlag == PageIssueLinkStatus.Unlinked)
        {
            return;
        }

        StatusFlag = PageIssueLinkStatus.Unlinked;
        UnlinkedBy = userId;
        UnlinkedAt = now;
        UpdatedAt = now;
    }

    private static string? Clamp(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var t = value.Trim();
        if (t.Any(char.IsControl))
        {
            t = new string([.. t.Where(c => !char.IsControl(c))]);
        }

        return t.Length <= max ? t : t[..max];
    }
}

/// <summary>One selected-text / anchor context for a Page–Issue relationship (many contexts per link).</summary>
public sealed class PageIssueLinkContext : Entity, ITenantOwned
{
    public const int MaxExactLength = 500;
    public const int MaxContextLength = 64;
    public const int MaxAnchorLength = 4000;

    private PageIssueLinkContext() { }

    public Guid TenantId { get; private set; }

    public Guid LinkId { get; private set; }

    public string Exact { get; private set; } = string.Empty;

    public string? Prefix { get; private set; }

    public string? Suffix { get; private set; }

    public string? AnchorJson { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static PageIssueLinkContext Create(Guid linkId, string? exact, string? prefix, string? suffix, string? anchorJson, Guid userId, DateTimeOffset now)
    {
        var e = (exact ?? string.Empty).Trim();
        if (e.Length is 0 or > MaxExactLength)
        {
            throw new DomainException("jira_link.quote_invalid", $"Selected text must be 1–{MaxExactLength} characters.");
        }

        return new PageIssueLinkContext
        {
            LinkId = linkId,
            Exact = e,
            Prefix = Clamp(prefix, MaxContextLength),
            Suffix = Clamp(suffix, MaxContextLength),
            AnchorJson = Clamp(anchorJson, MaxAnchorLength),
            CreatedBy = userId,
            CreatedAt = now,
        };
    }

    private static string? Clamp(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var t = value.Trim();
        return t.Length <= max ? t : t[..max];
    }
}
