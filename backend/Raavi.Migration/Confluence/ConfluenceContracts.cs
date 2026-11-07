namespace Raavi.Migration.Confluence;

/// <summary>
/// Contracts of the Confluence import module. Architecture: docs/migration/CONFLUENCE_MIGRATION.md and ADR-008/025.
/// Readers stream (no full-source materialization) and every run belongs to exactly one destination tenant.
/// </summary>
public enum ConfluenceSourceKind
{
    RestApi = 1,
    SiteXmlBackup = 2,
    SpaceXmlExport = 3,
}

public enum ConfluenceEdition
{
    Unknown = 0,
    Server = 1,
    DataCenter = 2,
    Cloud = 3,
}

public sealed record ConfluenceSourceInfo(ConfluenceEdition Edition, string Version, int? BuildNumber, ConfluenceSourceKind Kind);

/// <summary>A source space (neutral, adapter-independent).</summary>
public sealed record SourceSpace(string Key, string Name, string? Description, DateTimeOffset? CreatedAtUtc, string? HomePageSourceContentId = null);

/// <summary>A source page as found in the current tree (before its versions are read).</summary>
public sealed record SourcePage(string SourceContentId, string SpaceKey, string Title, string? ParentSourceContentId, int Position, int CurrentVersionNumber, IReadOnlyList<string> Labels);

/// <summary>One historical version of a page. <see cref="AuthorKey"/> is the source user key/username; never an invented user.</summary>
public sealed record SourceVersion(
    string SourceContentId,
    int SourceVersionNumber,
    string Title,
    string StorageFormatBody,
    string? AuthorKey,
    string? AuthorDisplayName,
    DateTimeOffset CreatedAtUtc,
    string? VersionComment,
    bool IsCurrent);

/// <summary>A file attached to a page (latest version only). <see cref="DownloadPath"/> is relative to the source base address.</summary>
public sealed record SourceAttachment(string Id, string FileName, string? MediaType, long Size, int VersionNumber, DateTimeOffset CreatedAtUtc, string? AuthorKey, string DownloadPath);

/// <summary>A comment of a source page (latest body). <see cref="ParentId"/> is the comment it replies to, if any.</summary>
public sealed record SourceComment(
    string Id, string? ParentId, bool Inline, string? Selection, string StorageFormatBody,
    string? AuthorKey, string? AuthorDisplayName, DateTimeOffset CreatedAtUtc, bool Resolved);

public enum SourceSubjectKind
{
    User = 1,
    Group = 2,
}

/// <summary>A user or group named by a source restriction. <see cref="Key"/> is the user key (or user name) / the group name.</summary>
public sealed record SourceSubject(SourceSubjectKind Kind, string Key, string? DisplayName);

/// <summary>View/edit restrictions of a page; a list is empty when that operation is unrestricted.</summary>
public sealed record SourceRestrictions(IReadOnlyList<SourceSubject> View, IReadOnlyList<SourceSubject> Edit)
{
    public static readonly SourceRestrictions None = new([], []);

    public bool Any => View.Count > 0 || Edit.Count > 0;
}

/// <summary>A source user as far as the source tells (the e-mail may be hidden by the source).</summary>
public sealed record SourceUser(string Key, string? UserName, string? DisplayName, string? Email);

/// <summary>A conversion warning: content is never dropped silently. <see cref="MacroName"/> is set for macro problems.</summary>
public sealed record ConversionWarning(string Code, string? MacroName, string Detail);

/// <summary>Version-specific adapter for one source kind (REST API per Confluence version range, XML backup, space export).</summary>
public interface IConfluenceSourceProvider : IDisposable
{
    ConfluenceSourceKind Kind { get; }

    Task<ConfluenceSourceInfo> DetectAsync(CancellationToken cancellationToken);

    Task<IReadOnlyList<SourceSpace>> ListSpacesAsync(CancellationToken cancellationToken);

    /// <summary>Current pages of a space (tree information only; bodies are read per version).</summary>
    IAsyncEnumerable<SourcePage> StreamPagesAsync(string spaceKey, CancellationToken cancellationToken);

    /// <summary>Every available version of a page, oldest first. Versions in <paramref name="skip"/> (already imported) are not downloaded.</summary>
    /// <summary>The files attached to a page (latest versions).</summary>
    Task<IReadOnlyList<SourceAttachment>> ListAttachmentsAsync(string sourceContentId, CancellationToken cancellationToken);

    /// <summary>Downloads one file into memory; fails with <c>attachment.too_large</c> when it exceeds <paramref name="maxBytes"/>.</summary>
    Task<byte[]> DownloadAttachmentAsync(SourceAttachment attachment, long maxBytes, CancellationToken cancellationToken);

    Task<IReadOnlyList<SourceComment>> ListCommentsAsync(string sourceContentId, CancellationToken cancellationToken);

    Task<SourceRestrictions> GetRestrictionsAsync(string sourceContentId, CancellationToken cancellationToken);

    /// <summary>The user with this key (or user name); null when unknown to the source or not readable.</summary>
    Task<SourceUser?> GetUserAsync(string keyOrUserName, CancellationToken cancellationToken);

    IAsyncEnumerable<SourceVersion> StreamVersionsAsync(string sourceContentId, CancellationToken cancellationToken, IReadOnlySet<int>? skip = null);
}
