using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public sealed record LabelDto(
    Guid Id, string Name, string DisplayName, string Slug, string? Description, string? Color,
    Guid? ParentId, int DisplayOrder, bool IsActive, DateTimeOffset? ArchivedAt,
    int UsageCount, IReadOnlyList<string> Aliases, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record LabelDetailDto(
    Guid Id, string Name, string DisplayName, string Slug, string? Description, string? Color,
    Guid? ParentId, string? ParentSlug, string? ParentDisplayName, int DisplayOrder, bool IsActive,
    DateTimeOffset? ArchivedAt, int UsageCount,
    IReadOnlyList<LabelDto> Children, IReadOnlyList<LabelDto> Related,
    IReadOnlyList<string> Aliases, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record LabelWriteRequest(
    string? DisplayName, string? Description, string? Color, Guid? ParentId, int? DisplayOrder, string[]? Aliases);

public sealed record LabelMoveRequest(Guid? ParentId, int? DisplayOrder);

public sealed record LabelMergeRequest(Guid SourceId, Guid DestinationId);

public sealed record LabelMergePreviewDto(Guid SourceId, string SourceDisplayName, Guid DestinationId, string DestinationDisplayName, int PageLinks, int AliasesToMove, int DuplicateLinks);

public sealed record LabelSearchRequest(string? Tags, string? TagMode, bool IncludeDescendants, string? SpaceKey, int Skip, int Take);

public sealed record LabelPageSearchDto(IReadOnlyList<ContentListItemDto> Items, int Total);

public sealed record LabelAnalyticsDto(
    int TotalActive, int Archived, int Unused, int PagesWithoutLabels,
    IReadOnlyList<LabelDto> MostUsed, IReadOnlyList<LabelDto> RecentlyCreated);

public sealed record LabelSuggestForPageDto(IReadOnlyList<LabelDto> Suggestions);

public sealed class LabelService(
    WikiDbContext db,
    WikiAuthorizer authz,
    SpaceService spaces,
    DiscoveryService discovery,
    ICurrentUser current,
    IClock clock)
{
    private static readonly Error NotFound = new("label.not_found", "Label not found.", ErrorKind.NotFound);
    private static readonly Error Forbidden = new("label.forbidden", "Not allowed.", ErrorKind.Forbidden);
    private static readonly Error Duplicate = new("label.duplicate", "A label with that name already exists.", ErrorKind.Conflict);

    public async Task<Result<IReadOnlyList<LabelDto>>> AdminListAsync(string? q, bool? archived, Guid? parentId, string? sort, int skip, int take, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<LabelDto>>.Failure(Forbidden);
        }

        take = Math.Clamp(take, 1, 200);
        skip = Math.Max(0, skip);
        var query = db.Labels.AsNoTracking().AsQueryable();
        if (archived == true)
        {
            query = query.Where(l => l.ArchivedAt != null);
        }
        else if (archived == false)
        {
            query = query.Where(l => l.ArchivedAt == null);
        }

        if (parentId is { } pid)
        {
            query = pid == Guid.Empty ? query.Where(l => l.ParentId == null) : query.Where(l => l.ParentId == pid);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var folded = Label.FoldDisplay(q);
            var variants = SearchText.Variants(q.Trim());
            var aliasIds = await db.LabelAliases.AsNoTracking()
                .Where(a => a.NormalizedAlias.Contains(folded) || variants.Any(v => a.Alias.Contains(v)))
                .Select(a => a.LabelId).Distinct().ToListAsync(ct).ConfigureAwait(false);
            query = query.Where(l => l.NormalizedName.Contains(folded) || variants.Any(v => l.DisplayName.Contains(v)) || aliasIds.Contains(l.Id));
        }

        query = (sort ?? "name").ToLowerInvariant() switch
        {
            "updated" => query.OrderByDescending(l => l.UpdatedAt),
            "created" => query.OrderByDescending(l => l.CreatedAt),
            "order" => query.OrderBy(l => l.DisplayOrder).ThenBy(l => l.DisplayName),
            _ => query.OrderBy(l => l.DisplayName),
        };

        var rows = await query.Skip(skip).Take(take).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<LabelDto>>.Success(await ToDtosAsync(rows, viewSafeUsage: false, ct).ConfigureAwait(false));
    }

    public async Task<Result<LabelDetailDto>> GetBySlugAsync(string slug, CancellationToken ct)
    {
        var label = await ResolveAsync(slug, ct).ConfigureAwait(false);
        if (label is null)
        {
            return Result<LabelDetailDto>.Failure(NotFound);
        }

        return Result<LabelDetailDto>.Success(await ToDetailAsync(label, ct).ConfigureAwait(false));
    }

    public async Task<Result<LabelDto>> CreateAsync(LabelWriteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await IsAdminAsync(ct).ConfigureAwait(false))
        {
            return Result<LabelDto>.Failure(Forbidden);
        }

        try
        {
            if (request.ParentId is { } p)
            {
                var depth = await DepthOfAsync(p, ct).ConfigureAwait(false);
                if (depth < 0 || depth >= Label.MaxDepth)
                {
                    return Result<LabelDto>.Failure(new Error("label.depth", "Hierarchy is too deep.", ErrorKind.Validation));
                }
            }

            var label = Label.CreateFromDisplay(request.DisplayName ?? string.Empty, request.Description, request.Color, request.ParentId, request.DisplayOrder ?? 0, current.UserId, clock.UtcNow);
            if (await db.Labels.AnyAsync(l => l.NormalizedName == label.NormalizedName || l.Slug == label.Slug || l.Name == label.Name, ct).ConfigureAwait(false))
            {
                return Result<LabelDto>.Failure(Duplicate);
            }

            db.Labels.Add(label);
            await ReplaceAliasesAsync(label, request.Aliases, ct).ConfigureAwait(false);
            db.Publish(WikiEventTypes.LabelCreated, new WikiEvent(Guid.Empty, null, current.UserId, label.DisplayName, null), clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<LabelDto>.Success((await ToDtosAsync([label], false, ct).ConfigureAwait(false))[0]);
        }
        catch (DomainException ex)
        {
            return Result<LabelDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    public async Task<Result<LabelDto>> UpdateAsync(Guid id, LabelWriteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await IsAdminAsync(ct).ConfigureAwait(false))
        {
            return Result<LabelDto>.Failure(Forbidden);
        }

        var label = await db.Labels.FirstOrDefaultAsync(l => l.Id == id, ct).ConfigureAwait(false);
        if (label is null)
        {
            return Result<LabelDto>.Failure(NotFound);
        }

        try
        {
            var display = request.DisplayName ?? label.DisplayName;
            label.UpdateMeta(display, request.Description, request.Color, request.DisplayOrder ?? label.DisplayOrder, current.UserId, clock.UtcNow);
            if (await db.Labels.AnyAsync(l => l.Id != id && l.NormalizedName == label.NormalizedName, ct).ConfigureAwait(false))
            {
                return Result<LabelDto>.Failure(Duplicate);
            }

            if (request.Aliases is not null)
            {
                await ReplaceAliasesAsync(label, request.Aliases, ct).ConfigureAwait(false);
            }

            db.Publish(WikiEventTypes.LabelUpdated, new WikiEvent(Guid.Empty, null, current.UserId, label.DisplayName, null), clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<LabelDto>.Success((await ToDtosAsync([label], false, ct).ConfigureAwait(false))[0]);
        }
        catch (DomainException ex)
        {
            return Result<LabelDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    public async Task<Result<LabelDto>> MoveAsync(Guid id, LabelMoveRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await IsAdminAsync(ct).ConfigureAwait(false))
        {
            return Result<LabelDto>.Failure(Forbidden);
        }

        var label = await db.Labels.FirstOrDefaultAsync(l => l.Id == id, ct).ConfigureAwait(false);
        if (label is null)
        {
            return Result<LabelDto>.Failure(NotFound);
        }

        var parentId = request.ParentId;
        if (parentId == id)
        {
            return Result<LabelDto>.Failure(new Error("label.cycle", "A label cannot be its own parent.", ErrorKind.Validation));
        }

        if (parentId is { } p)
        {
            if (!await db.Labels.AnyAsync(l => l.Id == p, ct).ConfigureAwait(false))
            {
                return Result<LabelDto>.Failure(NotFound);
            }

            if (await IsDescendantOfAsync(p, id, ct).ConfigureAwait(false))
            {
                return Result<LabelDto>.Failure(new Error("label.cycle", "That move would create a cycle.", ErrorKind.Validation));
            }

            var depth = await DepthOfAsync(p, ct).ConfigureAwait(false);
            var sub = await MaxSubtreeDepthAsync(id, ct).ConfigureAwait(false);
            if (depth + 1 + sub > Label.MaxDepth)
            {
                return Result<LabelDto>.Failure(new Error("label.depth", "Hierarchy is too deep.", ErrorKind.Validation));
            }
        }

        label.Move(parentId, request.DisplayOrder ?? label.DisplayOrder, current.UserId, clock.UtcNow);
        db.Publish(WikiEventTypes.LabelMoved, new WikiEvent(Guid.Empty, null, current.UserId, label.DisplayName, null), clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<LabelDto>.Success((await ToDtosAsync([label], false, ct).ConfigureAwait(false))[0]);
    }

    public async Task<Result<LabelDto>> ArchiveAsync(Guid id, bool restore, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct).ConfigureAwait(false))
        {
            return Result<LabelDto>.Failure(Forbidden);
        }

        var label = await db.Labels.FirstOrDefaultAsync(l => l.Id == id, ct).ConfigureAwait(false);
        if (label is null)
        {
            return Result<LabelDto>.Failure(NotFound);
        }

        if (restore)
        {
            label.Restore(current.UserId, clock.UtcNow);
            db.Publish(WikiEventTypes.LabelRestored, new WikiEvent(Guid.Empty, null, current.UserId, label.DisplayName, null), clock.UtcNow);
        }
        else
        {
            label.Archive(current.UserId, clock.UtcNow);
            db.Publish(WikiEventTypes.LabelArchived, new WikiEvent(Guid.Empty, null, current.UserId, label.DisplayName, null), clock.UtcNow);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<LabelDto>.Success((await ToDtosAsync([label], false, ct).ConfigureAwait(false))[0]);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        if (!await IsAdminAsync(ct).ConfigureAwait(false))
        {
            return Result.Failure(Forbidden);
        }

        var label = await db.Labels.FirstOrDefaultAsync(l => l.Id == id, ct).ConfigureAwait(false);
        if (label is null)
        {
            return Result.Failure(NotFound);
        }

        if (await db.ContentLabels.AnyAsync(cl => cl.LabelId == id, ct).ConfigureAwait(false)
            || await db.Labels.AnyAsync(l => l.ParentId == id, ct).ConfigureAwait(false)
            || await db.LabelAliases.AnyAsync(a => a.LabelId == id, ct).ConfigureAwait(false))
        {
            return Result.Failure(new Error("label.in_use", "Remove page links, aliases and child labels before deleting.", ErrorKind.Conflict));
        }

        db.Publish(WikiEventTypes.LabelDeleted, new WikiEvent(Guid.Empty, null, current.UserId, label.DisplayName, null), clock.UtcNow);
        db.Labels.Remove(label);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<LabelMergePreviewDto>> PreviewMergeAsync(LabelMergeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await IsAdminAsync(ct).ConfigureAwait(false))
        {
            return Result<LabelMergePreviewDto>.Failure(Forbidden);
        }

        var source = await db.Labels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.SourceId, ct).ConfigureAwait(false);
        var dest = await db.Labels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == request.DestinationId, ct).ConfigureAwait(false);
        if (source is null || dest is null || source.Id == dest.Id)
        {
            return Result<LabelMergePreviewDto>.Failure(NotFound);
        }

        var sourcePages = await db.ContentLabels.AsNoTracking().Where(cl => cl.LabelId == source.Id).Select(cl => cl.ContentId).ToListAsync(ct).ConfigureAwait(false);
        var destPages = await db.ContentLabels.AsNoTracking().Where(cl => cl.LabelId == dest.Id).Select(cl => cl.ContentId).ToListAsync(ct).ConfigureAwait(false);
        var dup = sourcePages.Intersect(destPages).Count();
        var aliases = await db.LabelAliases.AsNoTracking().CountAsync(a => a.LabelId == source.Id, ct).ConfigureAwait(false);
        return Result<LabelMergePreviewDto>.Success(new LabelMergePreviewDto(source.Id, source.DisplayName, dest.Id, dest.DisplayName, sourcePages.Count, aliases, dup));
    }

    public async Task<Result<LabelDto>> MergeAsync(LabelMergeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await IsAdminAsync(ct).ConfigureAwait(false))
        {
            return Result<LabelDto>.Failure(Forbidden);
        }

        var source = await db.Labels.FirstOrDefaultAsync(l => l.Id == request.SourceId, ct).ConfigureAwait(false);
        var dest = await db.Labels.FirstOrDefaultAsync(l => l.Id == request.DestinationId, ct).ConfigureAwait(false);
        if (source is null || dest is null || source.Id == dest.Id)
        {
            return Result<LabelDto>.Failure(NotFound);
        }

        var sourceLinks = await db.ContentLabels.Where(cl => cl.LabelId == source.Id).ToListAsync(ct).ConfigureAwait(false);
        var destIds = await db.ContentLabels.Where(cl => cl.LabelId == dest.Id).Select(cl => cl.ContentId).ToListAsync(ct).ConfigureAwait(false);
        var destSet = destIds.ToHashSet();
        foreach (var link in sourceLinks)
        {
            if (destSet.Contains(link.ContentId))
            {
                db.ContentLabels.Remove(link);
            }
            else
            {
                db.ContentLabels.Remove(link);
                db.ContentLabels.Add(ContentLabel.Create(link.ContentId, dest.Id, current.UserId, clock.UtcNow));
                destSet.Add(link.ContentId);
            }
        }

        var aliases = await db.LabelAliases.Where(a => a.LabelId == source.Id).ToListAsync(ct).ConfigureAwait(false);
        var destAliasNorm = await db.LabelAliases.Where(a => a.LabelId == dest.Id).Select(a => a.NormalizedAlias).ToListAsync(ct).ConfigureAwait(false);
        var destNorm = destAliasNorm.ToHashSet(StringComparer.Ordinal);
        destNorm.Add(dest.NormalizedName);
        foreach (var a in aliases)
        {
            db.LabelAliases.Remove(a);
            if (!destNorm.Contains(a.NormalizedAlias))
            {
                db.LabelAliases.Add(LabelAlias.Create(dest.Id, a.Alias, current.UserId, clock.UtcNow));
                destNorm.Add(a.NormalizedAlias);
            }
        }

        // Point children at destination.
        var children = await db.Labels.Where(l => l.ParentId == source.Id).ToListAsync(ct).ConfigureAwait(false);
        foreach (var child in children)
        {
            child.Move(dest.Id, child.DisplayOrder, current.UserId, clock.UtcNow);
        }

        var title = $"{source.DisplayName} → {dest.DisplayName}";
        db.Publish(WikiEventTypes.LabelMerged, new WikiEvent(Guid.Empty, null, current.UserId, title, null), clock.UtcNow);
        db.Labels.Remove(source);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<LabelDto>.Success((await ToDtosAsync([dest], false, ct).ConfigureAwait(false))[0]);
    }

    public async Task<Result<LabelPageSearchDto>> SearchPagesAsync(LabelSearchRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tags = (request.Tags ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tags.Length == 0)
        {
            return Result<LabelPageSearchDto>.Success(new LabelPageSearchDto([], 0));
        }

        var labelIds = new HashSet<Guid>();
        foreach (var t in tags)
        {
            var label = await ResolveAsync(t, ct).ConfigureAwait(false);
            if (label is null)
            {
                continue;
            }

            labelIds.Add(label.Id);
            if (request.IncludeDescendants)
            {
                foreach (var d in await DescendantsAsync(label.Id, ct).ConfigureAwait(false))
                {
                    labelIds.Add(d);
                }
            }
        }

        if (labelIds.Count == 0)
        {
            return Result<LabelPageSearchDto>.Success(new LabelPageSearchDto([], 0));
        }

        Guid? spaceId = null;
        if (!string.IsNullOrWhiteSpace(request.SpaceKey))
        {
            var space = await spaces.LoadAsync(request.SpaceKey, requireView: true, ct).ConfigureAwait(false);
            if (space is null)
            {
                return Result<LabelPageSearchDto>.Failure(WikiErrors.SpaceNotFound);
            }

            spaceId = space.Space.Id;
        }

        var modeAll = string.Equals(request.TagMode, "all", StringComparison.OrdinalIgnoreCase);
        var take = Math.Clamp(request.Take <= 0 ? 50 : request.Take, 1, 100);
        var skip = Math.Max(0, request.Skip);

        List<Guid> contentIds;
        if (modeAll)
        {
            var needed = labelIds.Count;
            var grouped = await db.ContentLabels.AsNoTracking()
                .Where(cl => labelIds.Contains(cl.LabelId))
                .GroupBy(cl => cl.ContentId)
                .Where(g => g.Select(x => x.LabelId).Distinct().Count() >= needed)
                .Select(g => g.Key)
                .ToListAsync(ct).ConfigureAwait(false);
            contentIds = grouped;
        }
        else
        {
            contentIds = await db.ContentLabels.AsNoTracking()
                .Where(cl => labelIds.Contains(cl.LabelId))
                .Select(cl => cl.ContentId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        }

        var nodesQuery = db.Nodes.AsNoTracking().Where(n => contentIds.Contains(n.Id) && n.Status == ContentStatus.Current);
        if (spaceId is { } sid)
        {
            nodesQuery = nodesQuery.Where(n => n.SpaceId == sid);
        }

        var candidates = await nodesQuery.OrderByDescending(n => n.UpdatedAt).Take(500).ToListAsync(ct).ConfigureAwait(false);
        var items = await discovery.ToListItemsPublicAsync(candidates, 500, ct).ConfigureAwait(false);
        var total = items.Count;
        var page = items.Skip(skip).Take(take).ToList();
        return Result<LabelPageSearchDto>.Success(new LabelPageSearchDto(page, total));
    }

    public async Task<Result<LabelAnalyticsDto>> AnalyticsAsync(CancellationToken ct)
    {
        if (!await IsAdminAsync(ct).ConfigureAwait(false))
        {
            return Result<LabelAnalyticsDto>.Failure(Forbidden);
        }

        var all = await db.Labels.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var usedIds = await db.ContentLabels.AsNoTracking().Select(cl => cl.LabelId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var used = usedIds.ToHashSet();
        var active = all.Where(l => l.ArchivedAt is null).ToList();
        var archived = all.Count(l => l.ArchivedAt is not null);
        var unused = active.Count(l => !used.Contains(l.Id));
        var labeledPages = await db.ContentLabels.AsNoTracking().Select(cl => cl.ContentId).Distinct().CountAsync(ct).ConfigureAwait(false);
        var currentPages = await db.Nodes.AsNoTracking().CountAsync(n => n.Status == ContentStatus.Current, ct).ConfigureAwait(false);
        var most = await ToDtosAsync(active.OrderByDescending(l => used.Contains(l.Id) ? 1 : 0).ThenBy(l => l.DisplayName).Take(20).ToList(), false, ct).ConfigureAwait(false);
        // Prefer real usage ordering
        var counts = await db.ContentLabels.AsNoTracking().GroupBy(cl => cl.LabelId).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
        var countMap = counts.ToDictionary(x => x.Key, x => x.C);
        most = await ToDtosAsync(active.OrderByDescending(l => countMap.GetValueOrDefault(l.Id)).ThenBy(l => l.DisplayName).Take(15).ToList(), false, ct).ConfigureAwait(false);
        var recent = await ToDtosAsync(all.OrderByDescending(l => l.CreatedAt).Take(15).ToList(), false, ct).ConfigureAwait(false);
        return Result<LabelAnalyticsDto>.Success(new LabelAnalyticsDto(active.Count, archived, unused, Math.Max(0, currentPages - labeledPages), most, recent));
    }

    public async Task<IReadOnlyList<LabelDto>> SuggestForPageAsync(Guid pageId, CancellationToken ct)
    {
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == pageId, ct).ConfigureAwait(false);
        if (node is null)
        {
            return [];
        }

        var viewable = await authz.FilterViewableAsync([(node.Id, node.SpaceId)], ct).ConfigureAwait(false);
        if (!viewable.Contains(node.Id))
        {
            return [];
        }

        var assigned = await db.ContentLabels.AsNoTracking().Where(cl => cl.ContentId == pageId).Select(cl => cl.LabelId).ToListAsync(ct).ConfigureAwait(false);
        var assignedSet = assigned.ToHashSet();
        var titleFold = Label.FoldDisplay(node.Title);
        var tokens = titleFold.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(t => t.Length >= 3).Take(8).ToList();
        var spaceLabelIds = await (from cl in db.ContentLabels.AsNoTracking()
                                   join n in db.Nodes.AsNoTracking() on cl.ContentId equals n.Id
                                   where n.SpaceId == node.SpaceId && n.Status == ContentStatus.Current
                                   select cl.LabelId).Distinct().Take(200).ToListAsync(ct).ConfigureAwait(false);

        var candidates = await db.Labels.AsNoTracking()
            .Where(l => l.ArchivedAt == null && l.IsActive && !assignedSet.Contains(l.Id)
                && (spaceLabelIds.Contains(l.Id) || tokens.Any(t => l.NormalizedName.Contains(t))))
            .OrderBy(l => l.DisplayName).Take(30).ToListAsync(ct).ConfigureAwait(false);
        return await ToDtosAsync(candidates.Take(8).ToList(), true, ct).ConfigureAwait(false);
    }

    public async Task PublishPageLabelEventAsync(Guid spaceId, Guid contentId, string title, bool added, CancellationToken ct)
    {
        db.Publish(added ? WikiEventTypes.ContentLabelAdded : WikiEventTypes.ContentLabelRemoved,
            new WikiEvent(spaceId, contentId, current.UserId, title, null), clock.UtcNow);
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private async Task<LabelDetailDto> ToDetailAsync(Label label, CancellationToken ct)
    {
        var dto = (await ToDtosAsync([label], true, ct).ConfigureAwait(false))[0];
        string? parentSlug = null, parentDisplay = null;
        if (label.ParentId is { } pid)
        {
            var parent = await db.Labels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == pid, ct).ConfigureAwait(false);
            parentSlug = parent?.Slug;
            parentDisplay = parent?.DisplayName;
        }

        var children = await db.Labels.AsNoTracking().Where(l => l.ParentId == label.Id && l.ArchivedAt == null)
            .OrderBy(l => l.DisplayOrder).ThenBy(l => l.DisplayName).Take(100).ToListAsync(ct).ConfigureAwait(false);
        var relatedIds = await (from cl in db.ContentLabels.AsNoTracking()
                                where cl.LabelId == label.Id
                                join cl2 in db.ContentLabels.AsNoTracking() on cl.ContentId equals cl2.ContentId
                                where cl2.LabelId != label.Id
                                select cl2.LabelId).Distinct().Take(50).ToListAsync(ct).ConfigureAwait(false);
        var related = await db.Labels.AsNoTracking().Where(l => relatedIds.Contains(l.Id) && l.ArchivedAt == null).Take(12).ToListAsync(ct).ConfigureAwait(false);
        // Filter related by viewable usage
        var relatedDtos = await ToDtosAsync(related, true, ct).ConfigureAwait(false);
        relatedDtos = relatedDtos.Where(r => r.UsageCount > 0).Take(8).ToList();

        return new LabelDetailDto(
            dto.Id, dto.Name, dto.DisplayName, dto.Slug, dto.Description, dto.Color,
            label.ParentId, parentSlug, parentDisplay, dto.DisplayOrder, dto.IsActive, dto.ArchivedAt, dto.UsageCount,
            await ToDtosAsync(children, true, ct).ConfigureAwait(false), relatedDtos, dto.Aliases, dto.CreatedAt, dto.UpdatedAt);
    }

    private async Task<IReadOnlyList<LabelDto>> ToDtosAsync(List<Label> rows, bool viewSafeUsage, CancellationToken ct)
    {
        if (rows.Count == 0)
        {
            return [];
        }

        var ids = rows.Select(r => r.Id).ToList();
        var aliases = await db.LabelAliases.AsNoTracking().Where(a => ids.Contains(a.LabelId)).ToListAsync(ct).ConfigureAwait(false);
        var aliasMap = aliases.GroupBy(a => a.LabelId).ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(a => a.Alias).OrderBy(x => x).ToList());

        Dictionary<Guid, int> usage;
        if (viewSafeUsage)
        {
            var uses = await (from cl in db.ContentLabels.AsNoTracking()
                              join n in db.Nodes.AsNoTracking() on cl.ContentId equals n.Id
                              where ids.Contains(cl.LabelId) && n.Status == ContentStatus.Current
                              select new { cl.LabelId, n.Id, n.SpaceId }).Take(5000).ToListAsync(ct).ConfigureAwait(false);
            var viewable = await authz.FilterViewableAsync(uses.Select(u => (u.Id, u.SpaceId)).Distinct().ToList(), ct).ConfigureAwait(false);
            usage = uses.Where(u => viewable.Contains(u.Id)).GroupBy(u => u.LabelId).ToDictionary(g => g.Key, g => g.Select(x => x.Id).Distinct().Count());
        }
        else
        {
            var counts = await db.ContentLabels.AsNoTracking().Where(cl => ids.Contains(cl.LabelId))
                .GroupBy(cl => cl.LabelId).Select(g => new { g.Key, C = g.Count() }).ToListAsync(ct).ConfigureAwait(false);
            usage = counts.ToDictionary(x => x.Key, x => x.C);
        }

        return rows.Select(l => new LabelDto(
            l.Id, l.Name, l.DisplayName, l.Slug, l.Description, l.Color, l.ParentId, l.DisplayOrder,
            l.IsActive && l.ArchivedAt is null, l.ArchivedAt, usage.GetValueOrDefault(l.Id),
            aliasMap.GetValueOrDefault(l.Id, []), l.CreatedAt, l.UpdatedAt)).ToList();
    }

    private async Task ReplaceAliasesAsync(Label label, string[]? aliases, CancellationToken ct)
    {
        var existing = await db.LabelAliases.Where(a => a.LabelId == label.Id).ToListAsync(ct).ConfigureAwait(false);
        db.LabelAliases.RemoveRange(existing);
        if (aliases is null || aliases.Length == 0)
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal) { label.NormalizedName };
        foreach (var raw in aliases.Take(Label.MaxAliases))
        {
            var a = LabelAlias.Create(label.Id, raw, current.UserId, clock.UtcNow);
            if (!seen.Add(a.NormalizedAlias))
            {
                continue;
            }

            if (await db.LabelAliases.AnyAsync(x => x.NormalizedAlias == a.NormalizedAlias && x.LabelId != label.Id, ct).ConfigureAwait(false)
                || await db.Labels.AnyAsync(l => l.NormalizedName == a.NormalizedAlias && l.Id != label.Id, ct).ConfigureAwait(false))
            {
                throw new DomainException("label.alias_duplicate", "That alias is already used.");
            }

            db.LabelAliases.Add(a);
            db.Publish(WikiEventTypes.LabelAliasAdded, new WikiEvent(Guid.Empty, null, current.UserId, a.Alias, null), clock.UtcNow);
        }
    }

    private async Task<Label?> ResolveAsync(string token, CancellationToken ct)
    {
        var raw = (token ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            return null;
        }

        var lower = raw.ToLowerInvariant();
        var folded = Label.FoldDisplay(raw);
        var bySlug = await db.Labels.AsNoTracking().FirstOrDefaultAsync(l => l.Slug == lower || l.Name == lower, ct).ConfigureAwait(false);
        if (bySlug is not null)
        {
            return bySlug;
        }

        var byNorm = await db.Labels.AsNoTracking().FirstOrDefaultAsync(l => l.NormalizedName == folded, ct).ConfigureAwait(false);
        if (byNorm is not null)
        {
            return byNorm;
        }

        var alias = await db.LabelAliases.AsNoTracking().FirstOrDefaultAsync(a => a.NormalizedAlias == folded, ct).ConfigureAwait(false);
        if (alias is null)
        {
            return null;
        }

        return await db.Labels.AsNoTracking().FirstOrDefaultAsync(l => l.Id == alias.LabelId, ct).ConfigureAwait(false);
    }

    private async Task<int> DepthOfAsync(Guid id, CancellationToken ct)
    {
        var depth = 0;
        Guid? currentId = id;
        var guard = 0;
        while (currentId is { } cid && guard++ < Label.MaxDepth + 2)
        {
            var parent = await db.Labels.AsNoTracking().Where(l => l.Id == cid).Select(l => l.ParentId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (parent is null)
            {
                return depth;
            }

            depth++;
            currentId = parent;
        }

        return -1;
    }

    private async Task<int> MaxSubtreeDepthAsync(Guid id, CancellationToken ct)
    {
        var children = await db.Labels.AsNoTracking().Where(l => l.ParentId == id).Select(l => l.Id).ToListAsync(ct).ConfigureAwait(false);
        if (children.Count == 0)
        {
            return 0;
        }

        var max = 0;
        foreach (var c in children)
        {
            max = Math.Max(max, 1 + await MaxSubtreeDepthAsync(c, ct).ConfigureAwait(false));
        }

        return max;
    }

    private async Task<bool> IsDescendantOfAsync(Guid nodeId, Guid ancestorId, CancellationToken ct)
    {
        Guid? currentId = nodeId;
        var guard = 0;
        while (currentId is { } cid && guard++ < Label.MaxDepth + 2)
        {
            if (cid == ancestorId)
            {
                return true;
            }

            currentId = await db.Labels.AsNoTracking().Where(l => l.Id == cid).Select(l => l.ParentId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<List<Guid>> DescendantsAsync(Guid id, CancellationToken ct)
    {
        var result = new List<Guid>();
        var frontier = new Queue<Guid>();
        frontier.Enqueue(id);
        var guard = 0;
        while (frontier.Count > 0 && guard++ < 500)
        {
            var pid = frontier.Dequeue();
            var kids = await db.Labels.AsNoTracking().Where(l => l.ParentId == pid).Select(l => l.Id).ToListAsync(ct).ConfigureAwait(false);
            foreach (var k in kids)
            {
                result.Add(k);
                frontier.Enqueue(k);
            }
        }

        return result;
    }

    private async Task<bool> IsAdminAsync(CancellationToken ct) =>
        (await authz.PrincipalAsync(ct).ConfigureAwait(false)).IsTenantAdministrator;
}
