using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Ai;
using Raavi.Wiki.ContentTypes;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

/// <summary>Feeds the smart-search index (ADR-049): the published text of current pages, nothing else. Runs in a tenant scope (RLS applies).</summary>
public sealed class ContentIndexSource(WikiDbContext db) : IContentIndexSource
{
    public async Task<IReadOnlyList<IndexablePageVersion>> ListCurrentAsync(int skip, int take, CancellationToken cancellationToken) =>
        await db.Nodes.AsNoTracking()
            .Where(n => n.ContentType == PageContentProvider.Type && n.Status == ContentStatus.Current && n.CurrentVersionId != null)
            .OrderBy(n => n.Id).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 2000))
            .Select(n => new IndexablePageVersion(n.Id, n.SpaceId, n.CurrentVersionId!.Value))
            .ToListAsync(cancellationToken).ConfigureAwait(false);

    public async Task<IndexablePage?> ReadAsync(Guid contentId, CancellationToken cancellationToken)
    {
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == contentId && n.ContentType == PageContentProvider.Type && n.Status == ContentStatus.Current && n.CurrentVersionId != null, cancellationToken).ConfigureAwait(false);
        if (node is null)
        {
            return null;
        }

        var json = await db.PageVersions.AsNoTracking().Where(v => v.Id == node.CurrentVersionId).Select(v => v.ContentJson).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var key = await db.Spaces.AsNoTracking().Where(s => s.Id == node.SpaceId).Select(s => s.Key).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        return new IndexablePage(node.Id, node.SpaceId, node.CurrentVersionId!.Value, node.Title, key, json is null ? string.Empty : RaaviDocument.Parse(json).PlainText);
    }
}

/// <summary>Search results are filtered with the caller's own View right at the moment of the query.</summary>
public sealed class ViewableContentFilter(WikiDbContext db, WikiAuthorizer authz) : IViewableContentFilter
{
    public async Task<IReadOnlySet<Guid>> FilterAsync(IReadOnlyCollection<Guid> contentIds, CancellationToken cancellationToken)
    {
        if (contentIds.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var ids = contentIds.Distinct().ToList();
        var locations = await db.Nodes.AsNoTracking()
            .Where(n => ids.Contains(n.Id) && n.ContentType == PageContentProvider.Type && n.Status == ContentStatus.Current)
            .Select(n => new { n.Id, n.SpaceId }).ToListAsync(cancellationToken).ConfigureAwait(false);
        return await authz.FilterViewableAsync(locations.Select(l => (l.Id, l.SpaceId)).ToList(), cancellationToken).ConfigureAwait(false);
    }
}
