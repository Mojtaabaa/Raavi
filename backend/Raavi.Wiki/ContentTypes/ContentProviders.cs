using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Content;
using Raavi.Domain.Common;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;

namespace Raavi.Wiki.ContentTypes;

/// <summary>Registry of content providers (ADR-009). Unknown types are an error, never a silent fallback.</summary>
public sealed class ContentProviderRegistry(IEnumerable<IContentProvider> providers) : IContentProviderRegistry
{
    private readonly Dictionary<string, IContentProvider> _byType = providers.ToDictionary(p => p.ContentType, StringComparer.Ordinal);

    public IReadOnlyCollection<IContentProvider> All => _byType.Values;

    public IContentProvider Get(string contentType) =>
        _byType.TryGetValue(contentType ?? string.Empty, out var p)
            ? p
            : throw new DomainException("content.type_unknown", $"Unknown content type '{contentType}'.");

    public bool IsKnown(string contentType) => _byType.ContainsKey(contentType ?? string.Empty);
}

/// <summary>Pages: versioned Raavi Document bodies, may have children.</summary>
public sealed class PageContentProvider(WikiDbContext db) : IContentProvider
{
    public const string Type = "page";

    public string ContentType => Type;

    public ContentTypeCapabilities Capabilities =>
        ContentTypeCapabilities.CanHaveChildren | ContentTypeCapabilities.Versioned | ContentTypeCapabilities.Searchable
        | ContentTypeCapabilities.Archivable | ContentTypeCapabilities.Templatable;

    public async ValueTask<SearchDocumentPayload?> ExtractSearchDocumentAsync(Guid contentId, CancellationToken cancellationToken)
    {
        var row = await (from n in db.Nodes.AsNoTracking()
                         join v in db.PageVersions.AsNoTracking() on n.CurrentVersionId equals v.Id into versions
                         from v in versions.DefaultIfEmpty()
                         where n.Id == contentId && n.ContentType == Type
                         select new { n.Title, n.SpaceId, Json = v == null ? null : v.ContentJson })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (row is null)
        {
            return null;
        }

        var body = row.Json is null ? string.Empty : RaaviDocument.Parse(row.Json).PlainText;
        return new SearchDocumentPayload(row.Title, body, new Dictionary<string, string> { ["spaceId"] = row.SpaceId.ToString() });
    }

    public ValueTask<ExportResult> ExportAsync(Guid contentId, string format, Stream destination, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Page export arrives with the export phase; the provider does not declare Exportable.");
}

/// <summary>Folders: titled containers without a body.</summary>
public sealed class FolderContentProvider(WikiDbContext db) : IContentProvider
{
    public const string Type = "folder";

    public string ContentType => Type;

    public ContentTypeCapabilities Capabilities =>
        ContentTypeCapabilities.CanHaveChildren | ContentTypeCapabilities.Searchable | ContentTypeCapabilities.Archivable;

    public async ValueTask<SearchDocumentPayload?> ExtractSearchDocumentAsync(Guid contentId, CancellationToken cancellationToken)
    {
        var row = await db.Nodes.AsNoTracking().Where(n => n.Id == contentId && n.ContentType == Type).Select(n => new { n.Title, n.SpaceId })
            .FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        return row is null ? null : new SearchDocumentPayload(row.Title, string.Empty, new Dictionary<string, string> { ["spaceId"] = row.SpaceId.ToString() });
    }

    public ValueTask<ExportResult> ExportAsync(Guid contentId, string format, Stream destination, CancellationToken cancellationToken) =>
        throw new NotSupportedException("Folders are not exportable.");
}
