namespace Raavi.Application.Abstractions.Content;

/// <summary>
/// Contract every content type (page, live doc, blog post, whiteboard, database, folder, shortcut, slide deck) implements.
/// The platform composes navigation, search, permissions, export and lifecycle through these providers instead of
/// switching on content type. See docs/architecture/adr/ADR-009-content-storage.md.
/// </summary>
public interface IContentProvider
{
    /// <summary>Stable key, e.g. "page", "whiteboard". Persisted in the content table's ContentType column.</summary>
    string ContentType { get; }

    ContentTypeCapabilities Capabilities { get; }

    /// <summary>Plain-text + structured fields for the search index. Called inside the owning tenant's context only.</summary>
    ValueTask<SearchDocumentPayload?> ExtractSearchDocumentAsync(Guid contentId, CancellationToken cancellationToken);

    /// <summary>Writes an export of the content. Callers must have passed the <c>View</c> permission check first.</summary>
    ValueTask<ExportResult> ExportAsync(Guid contentId, string format, Stream destination, CancellationToken cancellationToken);
}

[Flags]
public enum ContentTypeCapabilities
{
    None = 0,
    CanHaveChildren = 1,
    Versioned = 2,
    RealtimeCollaborative = 4,
    Commentable = 8,
    Searchable = 16,
    Exportable = 32,
    Archivable = 64,
    Templatable = 128,
}

public sealed record SearchDocumentPayload(string Title, string Body, IReadOnlyDictionary<string, string> Fields);

public sealed record ExportResult(string MediaType, string FileName);

/// <summary>Resolves the provider for a content type. Unknown types are an error, never a silent fallback.</summary>
public interface IContentProviderRegistry
{
    IContentProvider Get(string contentType);

    IReadOnlyCollection<IContentProvider> All { get; }
}
