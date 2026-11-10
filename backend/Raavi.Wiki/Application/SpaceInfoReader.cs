using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Ai;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

/// <summary>Space name, description and the titles of its pages that the current user may view (Prompt Hub space context, ADR-052).</summary>
public sealed class SpaceInfoReader(SpaceService spaces, WikiDbContext db, WikiAuthorizer authz) : ISpaceInfoReader
{
    public const int MaxTitles = 80;

    public async Task<SpaceInfo?> ReadAsync(string key, CancellationToken cancellationToken)
    {
        var space = await spaces.GetAsync(key, cancellationToken).ConfigureAwait(false);
        if (!space.IsSuccess || !space.Value.Rights.CanView)
        {
            return null;
        }

        var nodes = await db.Nodes.AsNoTracking().Where(n => n.SpaceId == space.Value.Id && n.Status == ContentStatus.Current && n.ContentType == "page")
            .OrderBy(n => n.Position).ThenBy(n => n.Title).Select(n => new { n.Id, n.Title }).Take(500).ToListAsync(cancellationToken).ConfigureAwait(false);
        var allowed = await authz.FilterViewableAsync([.. nodes.Select(n => (n.Id, space.Value.Id))], cancellationToken).ConfigureAwait(false);
        var titles = nodes.Where(n => allowed.Contains(n.Id)).Select(n => n.Title).ToList();
        return new SpaceInfo(space.Value.Key, space.Value.Name, space.Value.Description, titles.Take(MaxTitles).ToList(), titles.Count);
    }
}
