namespace Raavi.Application.Abstractions.Ai;

/// <summary>Which version of a current page exists (cheap to list; used to find what has changed since it was indexed).</summary>
public sealed record IndexablePageVersion(Guid ContentId, Guid SpaceId, Guid VersionId);

/// <summary>A current page as plain text for the search index (read without a user: only the index job uses it).</summary>
public sealed record IndexablePage(Guid ContentId, Guid SpaceId, Guid VersionId, string Title, string SpaceKey, string Text);

/// <summary>
/// What the smart-search index may read (implemented by the Wiki module, called only by the index job inside a tenant scope): the current
/// published version of every current page. Drafts, archived and trashed pages never appear, so they never enter the index.
/// </summary>
public interface IContentIndexSource
{
    Task<IReadOnlyList<IndexablePageVersion>> ListCurrentAsync(int skip, int take, CancellationToken cancellationToken);

    Task<IndexablePage?> ReadAsync(Guid contentId, CancellationToken cancellationToken);
}

/// <summary>
/// The permission check for search results, applied <b>at query time for the current user</b> (implemented by the Wiki module with the normal
/// View evaluation). Returns the ids the user may view; everything else (restricted, other space, archived, deleted since indexing) is dropped.
/// The index is a copy of content, never an authority on who may see it.
/// </summary>
public interface IViewableContentFilter
{
    Task<IReadOnlySet<Guid>> FilterAsync(IReadOnlyCollection<Guid> contentIds, CancellationToken cancellationToken);

    /// <summary>
    /// Same as <see cref="FilterAsync"/>, then drop content whose classification must not leave Raavi for an external model
    /// (e.g. Restricted). Keyword search may still return these pages; Ask/RAG model context must not.
    /// </summary>
    Task<IReadOnlySet<Guid>> FilterForExternalModelAsync(IReadOnlyCollection<Guid> contentIds, CancellationToken cancellationToken);
}

/// <summary>Optional metadata / relationship facets applied to already viewable search hits (EKOS B3).</summary>
public sealed record DiscoveryContentFilter(
    Guid? OwnerUserId = null,
    Guid? ReviewOwnerUserId = null,
    string? Classification = null,
    string? LifecycleStatus = null,
    bool? ReviewDue = null,
    string? RelationshipType = null,
    string? ContentType = null,
    DateTimeOffset? UpdatedFrom = null,
    DateTimeOffset? UpdatedTo = null)
{
    public bool IsEmpty =>
        OwnerUserId is null && ReviewOwnerUserId is null && string.IsNullOrWhiteSpace(Classification)
        && string.IsNullOrWhiteSpace(LifecycleStatus) && ReviewDue is null && string.IsNullOrWhiteSpace(RelationshipType)
        && string.IsNullOrWhiteSpace(ContentType) && UpdatedFrom is null && UpdatedTo is null;
}

/// <summary>Narrows a candidate id set using wiki governance / relationships / node fields (batch, no N+1).</summary>
public interface IDiscoveryContentFilter
{
    Task<IReadOnlySet<Guid>> FilterAsync(IReadOnlyCollection<Guid> contentIds, DiscoveryContentFilter filter, CancellationToken cancellationToken);
}
