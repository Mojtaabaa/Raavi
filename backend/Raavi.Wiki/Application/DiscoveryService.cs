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
        var variants = Raavi.Application.Common.SearchText.Variants(q); // ی/ي and ک/ك spellings of the title
        var matchingSpaces = await db.Spaces.AsNoTracking()
            .Where(s => viewableSpaces.Contains(s.Id) && (variants.Any(v => s.Name.Contains(v)) || s.Key.Contains(upper)))
            .OrderBy(s => s.Name).Take(limit).ToListAsync(ct).ConfigureAwait(false);

        var candidates = await db.Nodes.AsNoTracking()
            .Where(n => viewableSpaces.Contains(n.SpaceId) && n.Status == ContentStatus.Current && variants.Any(v => n.Title.Contains(v)))
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

    private static string Like(string q) => "%" + q.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal).Replace("[", "\\[", StringComparison.Ordinal) + "%";

    /// <summary>
    /// The activity log of the workspace (workspace administrators): every recorded event of every space, searchable by any of its fields — what was
    /// done (kind), to what (title), where (space key or name), by whom (display name), which version, and when.
    /// </summary>
    public async Task<ActivityLogPage> ActivityLogAsync(string? q, string? kind, string? spaceKey, DateTimeOffset? from, DateTimeOffset? to, Guid? actorId, int skip, int take, CancellationToken ct)
    {
        var query = db.Activity.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(kind))
        {
            var k = kind.Trim();
            query = query.Where(a => a.Kind == k || a.Kind.StartsWith(k + "."));
        }

        if (!string.IsNullOrWhiteSpace(spaceKey))
        {
            var key = spaceKey.Trim().ToUpperInvariant();
            query = query.Where(a => db.Spaces.Any(s => s.Id == a.SpaceId && s.Key == key));
        }

        if (from is { } f)
        {
            query = query.Where(a => a.OccurredAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(a => a.OccurredAt < t);
        }

        if (actorId is { } actor)
        {
            query = query.Where(a => a.ActorId == actor);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var text = q.Trim();
            var like = Like(text);
            // Names live in the identity module: find the actors whose name matches, then filter by their ids.
            var actorIds = await db.Activity.AsNoTracking().Select(a => a.ActorId).Distinct().Take(5000).ToListAsync(ct).ConfigureAwait(false);
            var names = await directory.GetUserNamesAsync(actorIds, ct).ConfigureAwait(false);
            var matchingActors = names.Where(n => n.Value.Contains(text, StringComparison.OrdinalIgnoreCase)).Select(n => n.Key).ToList();
            int? version = int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var parsedVersion) ? parsedVersion : null;
            query = query.Where(a =>
                EF.Functions.Like(a.Title, like, "\\") ||
                EF.Functions.Like(a.Kind, like, "\\") ||
                matchingActors.Contains(a.ActorId) ||
                (version != null && a.VersionNumber == version) ||
                db.Spaces.Any(s => s.Id == a.SpaceId && (EF.Functions.Like(s.Key, like, "\\") || EF.Functions.Like(s.Name, like, "\\"))));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var page = await query.OrderByDescending(a => a.OccurredAt).Skip(Math.Max(0, skip)).Take(Math.Clamp(take, 1, 200)).ToListAsync(ct).ConfigureAwait(false);
        var spaceIds = page.Select(e => e.SpaceId).Distinct().ToList();
        var keys = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Key, ct).ConfigureAwait(false);
        var pageNames = await directory.GetUserNamesAsync(page.Select(e => e.ActorId).Distinct().ToList(), ct).ConfigureAwait(false);
        var kinds = await db.Activity.AsNoTracking().Select(a => a.Kind).Distinct().OrderBy(k => k).ToListAsync(ct).ConfigureAwait(false);
        var items = page.Select(e => new ActivityDto(e.Id, e.Kind, e.SpaceId, keys.GetValueOrDefault(e.SpaceId, string.Empty), e.ContentId, e.Title, e.VersionNumber, e.ActorId, pageNames.GetValueOrDefault(e.ActorId), e.OccurredAt)).ToList();
        return new ActivityLogPage(total, items, kinds);
    }

    /// <summary>
    /// Counts for the activity chart: per day and kind, per kind, per hour of the day and the most active people, over the spaces and content the
    /// caller can view. Only numbers and names of people leave here, never titles.
    /// </summary>
    public async Task<ActivityStatsDto> ActivityStatsAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 1, 120);
        var spaceIds = (await authz.ViewableSpaceIdsAsync(ct).ConfigureAwait(false)).ToList();
        var since = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(-(days - 1)), TimeSpan.Zero);
        var entries = await db.Activity.AsNoTracking().Where(a => spaceIds.Contains(a.SpaceId) && a.OccurredAt >= since)
            .OrderByDescending(a => a.OccurredAt).Take(50_000).Select(a => new { a.ContentId, a.SpaceId, a.Kind, a.ActorId, a.OccurredAt }).ToListAsync(ct).ConfigureAwait(false);

        var contentIds = entries.Where(e => e.ContentId != null).Select(e => e.ContentId!.Value).Distinct().ToList();
        var locations = await db.Nodes.AsNoTracking().Where(n => contentIds.Contains(n.Id)).Select(n => new { n.Id, n.SpaceId }).ToListAsync(ct).ConfigureAwait(false);
        var viewable = await authz.FilterViewableAsync(locations.Select(l => (l.Id, l.SpaceId)).ToList(), ct).ConfigureAwait(false);
        var visible = entries.Where(e => e.ContentId is null || viewable.Contains(e.ContentId.Value)).ToList();

        var perDay = Enumerable.Range(0, days).Select(i => DateOnly.FromDateTime(since.UtcDateTime.AddDays(i))).Select(d =>
            new ActivityDayDto(d, visible.Where(e => DateOnly.FromDateTime(e.OccurredAt.UtcDateTime) == d).GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count()))).ToList();
        var byKind = visible.GroupBy(e => e.Kind).ToDictionary(g => g.Key, g => g.Count());
        var byHour = Enumerable.Range(0, 24).Select(h => visible.Count(e => e.OccurredAt.UtcDateTime.Hour == h)).ToList();
        var top = visible.GroupBy(e => e.ActorId).Select(g => new { g.Key, Count = g.Count() }).OrderByDescending(x => x.Count).Take(8).ToList();
        var names = await directory.GetUserNamesAsync(top.Select(x => x.Key).ToList(), ct).ConfigureAwait(false);
        return new ActivityStatsDto(visible.Count, perDay, byKind, top.Select(x => new ActivityActorDto(x.Key, names.GetValueOrDefault(x.Key), x.Count)).ToList(), byHour);
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
