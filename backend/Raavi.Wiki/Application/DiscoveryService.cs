using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Common;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

/// <summary>
/// Permission-filtered listings: quick find by title (the full-text search engine arrives in Phase 7), labels and the
/// activity feed. Candidates are bounded, then filtered through the authorizer, so results only ever shrink.
/// </summary>
public sealed class DiscoveryService(WikiDbContext db, WikiAuthorizer authz, SpaceService spaces, IPrincipalDirectory directory)
{
    private const int CandidateLimit = 200;

    public async Task<QuickFindDto> QuickFindAsync(string query, int limit, CancellationToken ct)
    {
        var q = (query ?? string.Empty).Trim();
        limit = Math.Clamp(limit, 1, 50);
        if (q.Length == 0)
        {
            return new QuickFindDto([], []);
        }

        var viewableSpaces = (await authz.ViewableSpaceIdsAsync(ct).ConfigureAwait(false)).ToList();
        var upper = q.ToUpperInvariant();
        var matchingSpaces = await db.Spaces.AsNoTracking()
            .Where(s => viewableSpaces.Contains(s.Id) && (s.Name.Contains(q) || s.Key.Contains(upper)))
            .OrderBy(s => s.Name).Take(limit).ToListAsync(ct).ConfigureAwait(false);

        var candidates = await db.Nodes.AsNoTracking()
            .Where(n => viewableSpaces.Contains(n.SpaceId) && n.Status == ContentStatus.Current && n.Title.Contains(q))
            .OrderByDescending(n => n.UpdatedAt).Take(CandidateLimit).ToListAsync(ct).ConfigureAwait(false);
        var content = await ToListItemsAsync(candidates, limit, ct).ConfigureAwait(false);
        return new QuickFindDto(matchingSpaces.Select(SpaceService.Summary).ToList(), content);
    }

    /// <summary>Labels starting with <paramref name="prefix"/> that are attached to at least one page the caller can view.</summary>
    public async Task<IReadOnlyList<string>> SuggestLabelsAsync(string prefix, CancellationToken ct)
    {
        var p = (prefix ?? string.Empty).Trim().ToLowerInvariant();
        var labels = await db.Labels.AsNoTracking().Where(l => l.Name.StartsWith(p)).OrderBy(l => l.Name).Take(50).ToListAsync(ct).ConfigureAwait(false);
        if (labels.Count == 0)
        {
            return [];
        }

        var labelIds = labels.Select(l => l.Id).ToList();
        var uses = await (from cl in db.ContentLabels.AsNoTracking()
                          join n in db.Nodes.AsNoTracking() on cl.ContentId equals n.Id
                          where labelIds.Contains(cl.LabelId) && n.Status == ContentStatus.Current
                          select new { cl.LabelId, n.Id, n.SpaceId }).Take(2000).ToListAsync(ct).ConfigureAwait(false);
        var viewable = await authz.FilterViewableAsync(uses.Select(u => (u.Id, u.SpaceId)).Distinct().ToList(), ct).ConfigureAwait(false);
        var visibleLabels = uses.Where(u => viewable.Contains(u.Id)).Select(u => u.LabelId).ToHashSet();
        return labels.Where(l => visibleLabels.Contains(l.Id)).Select(l => l.Name).Take(20).ToList();
    }

    public async Task<IReadOnlyList<ContentListItemDto>> ContentWithLabelAsync(string name, CancellationToken ct)
    {
        var normalized = Label.Normalize(name);
        var candidates = await (from cl in db.ContentLabels.AsNoTracking()
                                join l in db.Labels.AsNoTracking() on cl.LabelId equals l.Id
                                join n in db.Nodes.AsNoTracking() on cl.ContentId equals n.Id
                                where l.Name == normalized && n.Status == ContentStatus.Current
                                orderby n.UpdatedAt descending
                                select n).Take(CandidateLimit).ToListAsync(ct).ConfigureAwait(false);
        return await ToListItemsAsync(candidates, 100, ct).ConfigureAwait(false);
    }

    /// <summary>Recently updated current content of one space (space overview).</summary>
    public async Task<Result<IReadOnlyList<ContentListItemDto>>> RecentInSpaceAsync(string spaceKey, int limit, CancellationToken ct)
    {
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<IReadOnlyList<ContentListItemDto>>.Failure(WikiErrors.SpaceNotFound);
        }

        var candidates = await db.Nodes.AsNoTracking().Where(n => n.SpaceId == space.Space.Id && n.Status == ContentStatus.Current)
            .OrderByDescending(n => n.UpdatedAt).Take(CandidateLimit).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<ContentListItemDto>>.Success(await ToListItemsAsync(candidates, Math.Clamp(limit, 1, 50), ct).ConfigureAwait(false));
    }

    /// <summary>Activity of one space, or of every space the caller can view (<paramref name="spaceKey"/> null).</summary>
    public async Task<Result<IReadOnlyList<ActivityDto>>> ActivityAsync(string? spaceKey, int limit, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        List<Guid> spaceIds;
        if (spaceKey is not null)
        {
            var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
            if (space is null)
            {
                return Result<IReadOnlyList<ActivityDto>>.Failure(WikiErrors.SpaceNotFound);
            }

            spaceIds = [space.Space.Id];
        }
        else
        {
            spaceIds = (await authz.ViewableSpaceIdsAsync(ct).ConfigureAwait(false)).ToList();
        }

        var entries = await db.Activity.AsNoTracking().Where(a => spaceIds.Contains(a.SpaceId))
            .OrderByDescending(a => a.OccurredAt).Take(CandidateLimit).ToListAsync(ct).ConfigureAwait(false);

        // Content entries follow the content's *current* visibility (a page restricted later disappears from the feed).
        var contentIds = entries.Where(e => e.ContentId != null).Select(e => e.ContentId!.Value).Distinct().ToList();
        var locations = await db.Nodes.AsNoTracking().Where(n => contentIds.Contains(n.Id)).Select(n => new { n.Id, n.SpaceId }).ToListAsync(ct).ConfigureAwait(false);
        var viewable = await authz.FilterViewableAsync(locations.Select(l => (l.Id, l.SpaceId)).ToList(), ct).ConfigureAwait(false);
        var visible = entries.Where(e => e.ContentId is null || viewable.Contains(e.ContentId.Value)).Take(limit).ToList();

        var keys = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Key, ct).ConfigureAwait(false);
        var names = await directory.GetUserNamesAsync(visible.Select(e => e.ActorId).Distinct().ToList(), ct).ConfigureAwait(false);
        return Result<IReadOnlyList<ActivityDto>>.Success(visible
            .Select(e => new ActivityDto(e.Id, e.Kind, e.SpaceId, keys.GetValueOrDefault(e.SpaceId, string.Empty), e.ContentId, e.Title, e.VersionNumber, e.ActorId, names.GetValueOrDefault(e.ActorId), e.OccurredAt))
            .ToList());
    }

    private async Task<IReadOnlyList<ContentListItemDto>> ToListItemsAsync(List<ContentNode> candidates, int limit, CancellationToken ct)
    {
        var viewable = await authz.FilterViewableAsync(candidates.Select(n => (n.Id, n.SpaceId)).ToList(), ct).ConfigureAwait(false);
        var visible = candidates.Where(n => viewable.Contains(n.Id)).Take(limit).ToList();
        var spaceIds = visible.Select(n => n.SpaceId).Distinct().ToList();
        var spaceInfo = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);
        return visible.Select(n => new ContentListItemDto(n.Id, n.ContentType, n.Title, n.Slug, n.SpaceId, spaceInfo[n.SpaceId].Key, spaceInfo[n.SpaceId].Name, n.UpdatedAt)).ToList();
    }
}
