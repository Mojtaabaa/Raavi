using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

/// <summary>Typed knowledge edges (ADR-055). Authorization on both ends; edge delete never deletes Content.</summary>
public sealed class KnowledgeRelationshipService(
    WikiDbContext db,
    WikiAuthorizer authz,
    PageService pages,
    ICurrentUser current,
    IClock clock)
{
    public async Task<Result<IReadOnlyList<KnowledgeRelationshipDto>>> ListAsync(Guid contentId, CancellationToken ct)
    {
        var loaded = await pages.LoadAsync(contentId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<KnowledgeRelationshipDto>>.Failure(loaded.Error!);
        }

        var outgoing = await db.KnowledgeRelationships.AsNoTracking()
            .Where(r => r.SourceContentId == contentId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(200)
            .ToListAsync(ct).ConfigureAwait(false);
        var incoming = await db.KnowledgeRelationships.AsNoTracking()
            .Where(r => r.TargetContentId == contentId)
            .OrderByDescending(r => r.CreatedAt)
            .Take(200)
            .ToListAsync(ct).ConfigureAwait(false);

        var neighbourIds = outgoing.Where(r => r.TargetContentId is not null).Select(r => r.TargetContentId!.Value)
            .Concat(incoming.Select(r => r.SourceContentId))
            .Distinct().ToList();
        var nodes = neighbourIds.Count == 0
            ? new Dictionary<Guid, ContentNode>()
            : await db.Nodes.AsNoTracking().Where(n => neighbourIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct).ConfigureAwait(false);
        var spaceIds = nodes.Values.Select(n => n.SpaceId).Distinct().ToList();
        var spaces = spaceIds.Count == 0
            ? new Dictionary<Guid, Space>()
            : await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);

        var viewPairs = nodes.Values.Select(n => (n.Id, n.SpaceId)).ToList();
        var viewable = (await authz.FilterViewableAsync(viewPairs, ct).ConfigureAwait(false)).ToHashSet();

        var result = new List<KnowledgeRelationshipDto>();
        foreach (var r in outgoing)
        {
            var dto = MapOutgoing(r, nodes, spaces, viewable, canEditSource: loaded.Value.Rights.CanEdit);
            if (dto is not null)
            {
                result.Add(dto);
            }
        }

        foreach (var r in incoming)
        {
            if (!viewable.Contains(r.SourceContentId) || !nodes.TryGetValue(r.SourceContentId, out var src))
            {
                continue;
            }

            spaces.TryGetValue(src.SpaceId, out var space);
            result.Add(new KnowledgeRelationshipDto(
                r.Id, r.RelationshipType, "Incoming", r.Origin, r.SourceSystem,
                r.TargetKind, r.SourceContentId, null, src.Title, space?.Key, src.Slug,
                CanRemove: false, r.CreatedAt));
        }

        return Result<IReadOnlyList<KnowledgeRelationshipDto>>.Success(result);
    }

    public async Task<Result<KnowledgeRelationshipDto>> CreateAsync(Guid sourceContentId, CreateKnowledgeRelationshipRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var source = await pages.LoadAsync(sourceContentId, track: false, ct).ConfigureAwait(false);
        if (!source.IsSuccess)
        {
            return Result<KnowledgeRelationshipDto>.Failure(source.Error!);
        }

        if (!source.Value.Rights.CanEdit)
        {
            return Result<KnowledgeRelationshipDto>.Failure(WikiErrors.Forbidden);
        }

        var target = await pages.LoadAsync(request.TargetContentId, track: false, ct).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return Result<KnowledgeRelationshipDto>.Failure(WikiErrors.ContentNotFound);
        }

        KnowledgeRelationship edge;
        try
        {
            edge = KnowledgeRelationship.CreateManualContentLink(sourceContentId, request.TargetContentId, request.RelationshipType, current.UserId, clock.UtcNow);
        }
        catch (DomainException ex)
        {
            return Result<KnowledgeRelationshipDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }

        var duplicate = await db.KnowledgeRelationships.AsNoTracking().AnyAsync(
            r => r.SourceContentId == edge.SourceContentId
                 && r.RelationshipType == edge.RelationshipType
                 && r.TargetKind == edge.TargetKind
                 && r.TargetContentId == edge.TargetContentId, ct).ConfigureAwait(false);
        if (duplicate)
        {
            return Result<KnowledgeRelationshipDto>.Failure(new Error("relationship.duplicate", "This relationship already exists.", ErrorKind.Conflict));
        }

        db.KnowledgeRelationships.Add(edge);
        db.Publish(
            WikiEventTypes.ContentRelationshipCreated,
            new WikiEvent(source.Value.Space.Space.Id, sourceContentId, current.UserId, source.Value.Node.Title, null),
            clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var t = target.Value.Node;
        return Result<KnowledgeRelationshipDto>.Success(new KnowledgeRelationshipDto(
            edge.Id, edge.RelationshipType, "Outgoing", edge.Origin, edge.SourceSystem,
            edge.TargetKind, t.Id, null, t.Title, target.Value.Space.Space.Key, t.Slug,
            CanRemove: true, edge.CreatedAt));
    }

    public async Task<Result> RemoveAsync(Guid sourceContentId, Guid relationshipId, CancellationToken ct)
    {
        var source = await pages.LoadAsync(sourceContentId, track: false, ct).ConfigureAwait(false);
        if (!source.IsSuccess)
        {
            return Result.Failure(source.Error!);
        }

        if (!source.Value.Rights.CanEdit)
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        var edge = await db.KnowledgeRelationships.FirstOrDefaultAsync(r => r.Id == relationshipId && r.SourceContentId == sourceContentId, ct).ConfigureAwait(false);
        if (edge is null)
        {
            return Result.Failure(WikiErrors.ContentNotFound);
        }

        if (!KnowledgeRelationshipOrigins.UserMayDelete(edge.Origin))
        {
            return Result.Failure(new Error("relationship.not_manual", "System or integration relationships cannot be removed here.", ErrorKind.Conflict));
        }

        db.KnowledgeRelationships.Remove(edge);
        db.Publish(
            WikiEventTypes.ContentRelationshipRemoved,
            new WikiEvent(source.Value.Space.Space.Id, sourceContentId, current.UserId, source.Value.Node.Title, null),
            clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    private static KnowledgeRelationshipDto? MapOutgoing(
        KnowledgeRelationship r,
        Dictionary<Guid, ContentNode> nodes,
        Dictionary<Guid, Space> spaces,
        HashSet<Guid> viewable,
        bool canEditSource)
    {
        if (r.TargetKind == KnowledgeTargetKinds.Content)
        {
            if (r.TargetContentId is not { } tid || !viewable.Contains(tid) || !nodes.TryGetValue(tid, out var n))
            {
                return null;
            }

            spaces.TryGetValue(n.SpaceId, out var space);
            return new KnowledgeRelationshipDto(
                r.Id, r.RelationshipType, "Outgoing", r.Origin, r.SourceSystem,
                r.TargetKind, tid, null, n.Title, space?.Key, n.Slug,
                CanRemove: canEditSource && KnowledgeRelationshipOrigins.UserMayDelete(r.Origin), r.CreatedAt);
        }

        // External targets: visible if the caller can edit/view the source (already gated); no second Content ACL.
        return new KnowledgeRelationshipDto(
            r.Id, r.RelationshipType, "Outgoing", r.Origin, r.SourceSystem,
            r.TargetKind, null, r.TargetExternalKey, r.TargetTitle, null, null,
            CanRemove: canEditSource && KnowledgeRelationshipOrigins.UserMayDelete(r.Origin), r.CreatedAt);
    }
}
