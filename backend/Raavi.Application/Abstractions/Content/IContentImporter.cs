namespace Raavi.Application.Abstractions.Content;

/// <param name="SourceSystem">Provenance of everything imported from one source, e.g. <c>confluence:ab12cd34ef56</c>.</param>
/// <param name="SourceId">The space's key in the source system.</param>
/// <param name="OwnerId">Tenant member who owns the imported space (gets every space right; nobody else gets any — access is never widened).</param>
public sealed record ImportSpaceRequest(string SourceSystem, string SourceId, string Key, string Name, string? Description, DateTimeOffset? CreatedAt, Guid OwnerId);

public sealed record ImportedSpace(Guid SpaceId, string Key, bool Created);

/// <summary>One historical version, oldest first. <see cref="DocJson"/> is the document's <c>doc</c> node; the importer wraps it in the current envelope and validates it with the editor schema.</summary>
public sealed record ImportVersion(int SourceVersionNumber, string Title, string DocJson, string? AuthorKey, string? AuthorDisplayName, Guid? AuthorUserId, DateTimeOffset CreatedAt, string? Comment);

public sealed record ImportPageRequest(
    string SourceSystem,
    string SourceId,
    Guid SpaceId,
    string? ParentSourceId,
    int Position,
    string? SourceUrl,
    IReadOnlyList<ImportVersion> Versions,
    IReadOnlyList<string> Labels,
    Guid ActorId,
    bool Reconvert = false);

public sealed record ImportedPage(Guid ContentId, bool Created, int VersionsImported, int VersionsSkipped, IReadOnlyList<string> Warnings, int VersionsRewritten = 0);

/// <summary>A file of a source page (latest version). Its id is <see cref="ImportIds.Attachment"/>.</summary>
public sealed record ImportAttachment(string FileName, byte[] Data, DateTimeOffset CreatedAt);

public sealed record AttachmentImportResult(int Created, int Replaced);

public sealed record UnresolvedLink(string SpaceKey, string Title, Guid FromContentId);

public sealed record LinkPassResult(int Resolved, int Unresolved, IReadOnlyList<UnresolvedLink> Examples);

public sealed record ImportedCounts(int Pages, int Versions);

/// <summary>One comment of a source page. Replies at any depth are flattened into their thread (parent chain via <see cref="ParentSourceId"/>).</summary>
public sealed record ImportComment(string SourceId, string? ParentSourceId, bool Inline, string? Selection, string DocJson, string? AuthorName, Guid? AuthorUserId, DateTimeOffset CreatedAt, bool Resolved);

public sealed record CommentImportResult(int Created, int Skipped, IReadOnlyList<string> Warnings);

public enum ImportSubjectKind
{
    User = 1,
    Group = 2,
}

/// <summary>A Raavi user or group that a source restriction was mapped to.</summary>
public sealed record ImportSubject(ImportSubjectKind Kind, Guid Id);

/// <summary>
/// Writes imported content into the current tenant (implemented by the Wiki module; MODULE_BOUNDARIES.md). Idempotent by
/// construction: items are keyed by (source system, source id) and versions by their source number, both enforced by unique
/// indexes, so re-running an import never duplicates anything. Each page is written atomically.
/// </summary>
public interface IContentImporter
{
    Task<ImportedSpace> EnsureSpaceAsync(ImportSpaceRequest request, CancellationToken cancellationToken);

    /// <summary>Source version numbers already imported for a page (empty when the page does not exist yet).</summary>
    Task<IReadOnlySet<int>> ImportedVersionsAsync(string sourceSystem, string sourceId, CancellationToken cancellationToken);

    Task<ImportedPage> ImportPageAsync(ImportPageRequest request, CancellationToken cancellationToken);

    /// <summary>Ids (see <see cref="ImportIds.Attachment"/>) of the attachments already imported for a page.</summary>
    Task<IReadOnlySet<Guid>> ImportedAttachmentIdsAsync(string sourceSystem, string pageSourceId, CancellationToken cancellationToken);

    /// <summary>
    /// Stores files for an imported page. Files that already exist are skipped, or — with <paramref name="replaceChanged"/> — their bytes
    /// are replaced when the source now delivers different content. The page must have been imported.
    /// </summary>
    Task<AttachmentImportResult> ImportAttachmentsAsync(string sourceSystem, string pageSourceId, IReadOnlyList<ImportAttachment> files, Guid actorId, bool replaceChanged, CancellationToken cancellationToken);

    /// <summary>
    /// Restricts an imported page to <paramref name="view"/> / <paramref name="edit"/> plus <paramref name="ownerId"/> (the importing
    /// administrator, so nothing becomes unreachable). A kind is only applied when the page has none yet — later changes made by
    /// administrators are never overwritten by a re-run. Subjects that could not be mapped are simply absent: a restriction can
    /// become narrower than in the source, never wider.
    /// </summary>
    Task<bool> ApplyRestrictionsAsync(string sourceSystem, string pageSourceId, bool restrictView, IReadOnlyList<ImportSubject> view, bool restrictEdit, IReadOnlyList<ImportSubject> edit, Guid ownerId, CancellationToken cancellationToken);

    /// <summary>
    /// Imports a page's comments (idempotent by source id; nothing is notified). Authors that are not workspace members are kept
    /// by name only. The page must have been imported.
    /// </summary>
    Task<CommentImportResult> ImportCommentsAsync(string sourceSystem, string pageSourceId, IReadOnlyList<ImportComment> comments, CancellationToken cancellationToken);

    Task SetHomePageAsync(string sourceSystem, string sourceSpaceId, string sourcePageId, Guid actorId, CancellationToken cancellationToken);

    /// <summary>Rewrites <c>raavi:confluence-page</c> placeholders of everything imported from <paramref name="sourceSystem"/> into page links.</summary>
    Task<LinkPassResult> ResolveLinksAsync(string sourceSystem, CancellationToken cancellationToken);

    Task<ImportedCounts> CountAsync(string sourceSystem, string sourceSpaceId, CancellationToken cancellationToken);
}
