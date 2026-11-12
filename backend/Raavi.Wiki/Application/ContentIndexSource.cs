using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Ai;
using Raavi.Application.Abstractions.Time;
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
        var labels = await (from cl in db.ContentLabels.AsNoTracking()
                            join l in db.Labels.AsNoTracking() on cl.LabelId equals l.Id
                            where cl.ContentId == contentId
                            select l.DisplayName).ToListAsync(cancellationToken).ConfigureAwait(false);
        var body = json is null ? string.Empty : RaaviDocument.Parse(json).PlainText;
        if (labels.Count > 0)
        {
            body = string.IsNullOrEmpty(body) ? string.Join(' ', labels) : body + "\n" + string.Join(' ', labels);
        }

        return new IndexablePage(node.Id, node.SpaceId, node.CurrentVersionId!.Value, node.Title, key, body);
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

    public async Task<IReadOnlySet<Guid>> FilterForExternalModelAsync(IReadOnlyCollection<Guid> contentIds, CancellationToken cancellationToken)
    {
        var viewable = await FilterAsync(contentIds, cancellationToken).ConfigureAwait(false);
        if (viewable.Count == 0)
        {
            return viewable;
        }

        var restricted = await db.ContentGovernance.AsNoTracking()
            .Where(g => viewable.Contains(g.Id) && g.Classification == ContentClassifications.Restricted)
            .Select(g => g.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (restricted.Count == 0)
        {
            return viewable;
        }

        return viewable.Where(id => !restricted.Contains(id)).ToHashSet();
    }
}

/// <summary>Batch metadata / relationship narrowing for smart-search hits (EKOS B3). Caller already applied View.</summary>
public sealed class DiscoveryContentFilterService(WikiDbContext db, IClock clock) : IDiscoveryContentFilter
{
    public async Task<IReadOnlySet<Guid>> FilterAsync(IReadOnlyCollection<Guid> contentIds, DiscoveryContentFilter filter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (contentIds.Count == 0 || filter.IsEmpty)
        {
            return contentIds.ToHashSet();
        }

        var ids = contentIds.Distinct().ToList();
        var q = db.Nodes.AsNoTracking().Where(n => ids.Contains(n.Id) && n.Status == ContentStatus.Current);
        if (!string.IsNullOrWhiteSpace(filter.ContentType))
        {
            var type = filter.ContentType.Trim();
            q = q.Where(n => n.ContentType == type);
        }

        if (filter.UpdatedFrom is { } from)
        {
            q = q.Where(n => n.UpdatedAt >= from);
        }

        if (filter.UpdatedTo is { } to)
        {
            q = q.Where(n => n.UpdatedAt <= to);
        }

        var matched = await q.Select(n => n.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (matched.Count == 0)
        {
            return new HashSet<Guid>();
        }

        var needsGov = filter.OwnerUserId is not null || filter.ReviewOwnerUserId is not null
            || !string.IsNullOrWhiteSpace(filter.Classification) || !string.IsNullOrWhiteSpace(filter.LifecycleStatus)
            || filter.ReviewDue is not null;
        if (needsGov)
        {
            var now = clock.UtcNow;
            var gq = db.ContentGovernance.AsNoTracking().Where(g => matched.Contains(g.Id));
            if (filter.OwnerUserId is { } owner)
            {
                gq = gq.Where(g => g.OwnerUserId == owner);
            }

            if (filter.ReviewOwnerUserId is { } reviewOwner)
            {
                gq = gq.Where(g => g.ReviewOwnerUserId == reviewOwner);
            }

            if (!string.IsNullOrWhiteSpace(filter.Classification))
            {
                var c = filter.Classification.Trim();
                gq = gq.Where(g => g.Classification == c);
            }

            if (!string.IsNullOrWhiteSpace(filter.LifecycleStatus))
            {
                var life = filter.LifecycleStatus.Trim();
                gq = gq.Where(g => g.LifecycleStatus == life);
            }

            if (filter.ReviewDue is true)
            {
                gq = gq.Where(g => g.ReviewAt != null && g.ReviewAt <= now);
            }
            else if (filter.ReviewDue is false)
            {
                gq = gq.Where(g => g.ReviewAt == null || g.ReviewAt > now);
            }

            matched = await gq.Select(g => g.Id).ToListAsync(cancellationToken).ConfigureAwait(false);
            if (matched.Count == 0)
            {
                return new HashSet<Guid>();
            }
        }

        if (!string.IsNullOrWhiteSpace(filter.RelationshipType))
        {
            var rel = filter.RelationshipType.Trim();
            matched = await db.KnowledgeRelationships.AsNoTracking()
                .Where(r => matched.Contains(r.SourceContentId) && r.RelationshipType == rel)
                .Select(r => r.SourceContentId)
                .Distinct()
                .ToListAsync(cancellationToken).ConfigureAwait(false);
        }

        return matched.ToHashSet();
    }
}
