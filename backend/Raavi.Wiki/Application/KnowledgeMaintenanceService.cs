using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Content;
using Raavi.Application.Abstractions.Time;
using Raavi.Wiki.ContentTypes;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

/// <summary>On-demand stale / near-duplicate suggestions (EKOS-8). No silent writes.</summary>
public sealed class KnowledgeMaintenanceService(
    WikiDbContext db,
    WikiAuthorizer authz,
    IClock clock,
    IOptions<KnowledgeMaintenanceOptions> options) : IKnowledgeMaintenanceScanner
{
    public const string KindReviewDue = "review_due";
    public const string KindStaleContent = "stale_content";
    public const string KindNearDuplicate = "near_duplicate";

    public async Task<IReadOnlyList<KnowledgeMaintenanceSuggestion>> ScanAsync(int take, CancellationToken cancellationToken)
    {
        take = Math.Clamp(take, 1, 100);
        var cfg = options.Value;
        var staleDays = Math.Clamp(cfg.StaleAfterDays <= 0 ? 180 : cfg.StaleAfterDays, 1, 3650);
        var threshold = Math.Clamp(cfg.NearDuplicateTitleThreshold is <= 0 or > 1 ? 0.75 : cfg.NearDuplicateTitleThreshold, 0.5, 1.0);
        var now = clock.UtcNow;
        var list = new List<KnowledgeMaintenanceSuggestion>(take);

        await AppendReviewDueAsync(list, take, now, cancellationToken).ConfigureAwait(false);
        if (list.Count < take)
        {
            await AppendStaleContentAsync(list, take, now, staleDays, cancellationToken).ConfigureAwait(false);
        }

        if (list.Count < take)
        {
            await AppendNearDuplicatesAsync(list, take, threshold, cancellationToken).ConfigureAwait(false);
        }

        return list.Take(take).ToList();
    }

    private async Task AppendReviewDueAsync(List<KnowledgeMaintenanceSuggestion> list, int take, DateTimeOffset now, CancellationToken ct)
    {
        var rows = await (from g in db.ContentGovernance.AsNoTracking()
                          join n in db.Nodes.AsNoTracking() on g.Id equals n.Id
                          where n.Status == ContentStatus.Current && g.ReviewAt != null && g.ReviewAt <= now
                          orderby g.ReviewAt
                          select new { g, n }).Take(200).ToListAsync(ct).ConfigureAwait(false);
        var visible = await VisibleAsync(rows.Select(r => (r.n.Id, r.n.SpaceId, r.n, r.g.ReviewAt)).ToList(), ct).ConfigureAwait(false);
        foreach (var row in visible)
        {
            if (list.Count >= take)
            {
                break;
            }

            if (list.Any(s => s.ContentId == row.Node.Id && s.Kind == KindReviewDue))
            {
                continue;
            }

            list.Add(new KnowledgeMaintenanceSuggestion(
                KindReviewDue, row.Node.Id, row.Node.Title, row.SpaceKey, row.SpaceName, row.Node.UpdatedAt, row.ReviewAt,
                null, null, null, "Review date has passed."));
        }
    }

    private async Task AppendStaleContentAsync(List<KnowledgeMaintenanceSuggestion> list, int take, DateTimeOffset now, int staleDays, CancellationToken ct)
    {
        var cutoff = now.AddDays(-staleDays);
        var rows = await db.Nodes.AsNoTracking()
            .Where(n => n.Status == ContentStatus.Current && n.ContentType == PageContentProvider.Type && n.UpdatedAt < cutoff)
            .OrderBy(n => n.UpdatedAt)
            .Take(200)
            .ToListAsync(ct).ConfigureAwait(false);
        var visible = await VisibleAsync(rows.Select(n => (n.Id, n.SpaceId, n, (DateTimeOffset?)null)).ToList(), ct).ConfigureAwait(false);
        foreach (var row in visible)
        {
            if (list.Count >= take)
            {
                break;
            }

            if (list.Any(s => s.ContentId == row.Node.Id))
            {
                continue;
            }

            list.Add(new KnowledgeMaintenanceSuggestion(
                KindStaleContent, row.Node.Id, row.Node.Title, row.SpaceKey, row.SpaceName, row.Node.UpdatedAt, null,
                null, null, null, $"Not updated for {staleDays}+ days."));
        }
    }

    private async Task AppendNearDuplicatesAsync(List<KnowledgeMaintenanceSuggestion> list, int take, double threshold, CancellationToken ct)
    {
        var rows = await db.Nodes.AsNoTracking()
            .Where(n => n.Status == ContentStatus.Current && n.ContentType == PageContentProvider.Type)
            .OrderByDescending(n => n.UpdatedAt)
            .Take(800)
            .Select(n => new { n.Id, n.SpaceId, n.Title, n.UpdatedAt })
            .ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count < 2)
        {
            return;
        }

        var viewable = await authz.FilterViewableAsync(rows.Select(r => (r.Id, r.SpaceId)).ToList(), ct).ConfigureAwait(false);
        var visible = rows.Where(r => viewable.Contains(r.Id)).ToList();
        var spaceIds = visible.Select(r => r.SpaceId).Distinct().ToList();
        var spaces = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);

        var seenPairs = new HashSet<(Guid, Guid)>();
        foreach (var group in visible.GroupBy(r => r.SpaceId))
        {
            if (!spaces.TryGetValue(group.Key, out var space))
            {
                continue;
            }

            var pages = group.Select(r => (r.Id, r.Title, r.UpdatedAt, Tokens: TitleTokens(r.Title), Fold: Label.FoldDisplay(r.Title))).ToList();
            for (var i = 0; i < pages.Count && list.Count < take; i++)
            {
                for (var j = i + 1; j < pages.Count && list.Count < take; j++)
                {
                    var a = pages[i];
                    var b = pages[j];
                    var score = TitleSimilarity(a.Fold, a.Tokens, b.Fold, b.Tokens);
                    if (score < threshold)
                    {
                        continue;
                    }

                    var lo = a.Id.CompareTo(b.Id) < 0 ? a.Id : b.Id;
                    var hi = a.Id.CompareTo(b.Id) < 0 ? b.Id : a.Id;
                    if (!seenPairs.Add((lo, hi)))
                    {
                        continue;
                    }

                    // Prefer the older page as the primary “review this” target.
                    var (primary, related) = a.UpdatedAt <= b.UpdatedAt ? (a, b) : (b, a);
                    if (list.Any(s => s.Kind == KindNearDuplicate && s.ContentId == primary.Id && s.RelatedContentId == related.Id))
                    {
                        continue;
                    }

                    list.Add(new KnowledgeMaintenanceSuggestion(
                        KindNearDuplicate, primary.Id, primary.Title, space.Key, space.Name, primary.UpdatedAt, null,
                        related.Id, related.Title, Math.Round(score, 3), "Near-duplicate titles in the same space."));
                }
            }
        }
    }

    private async Task<IReadOnlyList<(ContentNode Node, string SpaceKey, string SpaceName, DateTimeOffset? ReviewAt)>> VisibleAsync(
        IReadOnlyList<(Guid Id, Guid SpaceId, ContentNode Node, DateTimeOffset? ReviewAt)> rows,
        CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var viewable = await authz.FilterViewableAsync(rows.Select(r => (r.Id, r.SpaceId)).ToList(), ct).ConfigureAwait(false);
        var visible = rows.Where(r => viewable.Contains(r.Id)).ToList();
        var spaceIds = visible.Select(r => r.SpaceId).Distinct().ToList();
        var spaces = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);
        return visible
            .Where(r => spaces.ContainsKey(r.SpaceId))
            .Select(r => (r.Node, spaces[r.SpaceId].Key, spaces[r.SpaceId].Name, r.ReviewAt))
            .ToList();
    }

    private static HashSet<string> TitleTokens(string title) =>
        Label.FoldDisplay(title)
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(t => t.Length >= 2)
            .ToHashSet(StringComparer.Ordinal);

    private static double TitleSimilarity(string foldA, HashSet<string> a, string foldB, HashSet<string> b)
    {
        if (string.Equals(foldA, foldB, StringComparison.Ordinal))
        {
            return 1.0;
        }

        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var inter = a.Count(t => b.Contains(t));
        var union = a.Count + b.Count - inter;
        return union == 0 ? 0 : (double)inter / union;
    }
}
