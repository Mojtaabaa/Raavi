using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Content;
using Raavi.Application.Abstractions.Time;
using Raavi.Domain.Common;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;

namespace Raavi.Wiki.Application;

/// <summary>Projects connector links into <see cref="KnowledgeRelationship"/> rows (Integration origin).</summary>
public sealed class KnowledgeRelationshipProjection(WikiDbContext db, IClock clock) : IKnowledgeRelationshipProjection
{
    public async Task UpsertExternalAsync(
        Guid sourceContentId,
        string relationshipType,
        string targetExternalKey,
        string? title,
        string sourceSystem,
        Guid actorId,
        CancellationToken cancellationToken)
    {
        var key = (targetExternalKey ?? string.Empty).Trim();
        var system = (sourceSystem ?? string.Empty).Trim();
        if (sourceContentId == Guid.Empty || key.Length == 0 || system.Length == 0)
        {
            return;
        }

        if (!await db.Nodes.AsNoTracking().AnyAsync(n => n.Id == sourceContentId, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        string type;
        try
        {
            type = KnowledgeRelationshipTypes.Normalize(relationshipType);
        }
        catch (DomainException)
        {
            type = KnowledgeRelationshipTypes.RelatedTo;
        }

        var existing = await db.KnowledgeRelationships.FirstOrDefaultAsync(
            r => r.SourceContentId == sourceContentId
                 && r.Origin == KnowledgeRelationshipOrigins.Integration
                 && r.SourceSystem == system
                 && r.TargetExternalKey == key, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            // Title/type refresh: remove + recreate (entity has no mutators for title).
            if (string.Equals(existing.RelationshipType, type, StringComparison.Ordinal)
                && string.Equals(existing.TargetTitle ?? string.Empty, title?.Trim() ?? string.Empty, StringComparison.Ordinal))
            {
                return;
            }

            db.KnowledgeRelationships.Remove(existing);
        }

        KnowledgeRelationship edge;
        try
        {
            edge = KnowledgeRelationship.Create(
                sourceContentId, type, KnowledgeTargetKinds.ExternalIssue, null, key, title,
                KnowledgeRelationshipOrigins.Integration, system, actorId, clock.UtcNow);
        }
        catch (DomainException)
        {
            return;
        }

        db.KnowledgeRelationships.Add(edge);
        var spaceId = await db.Nodes.AsNoTracking().Where(n => n.Id == sourceContentId).Select(n => n.SpaceId).FirstAsync(cancellationToken).ConfigureAwait(false);
        var pageTitle = await db.Nodes.AsNoTracking().Where(n => n.Id == sourceContentId).Select(n => n.Title).FirstAsync(cancellationToken).ConfigureAwait(false);
        db.Publish(WikiEventTypes.ContentRelationshipCreated, new WikiEvent(spaceId, sourceContentId, actorId, pageTitle, null), clock.UtcNow);
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveExternalAsync(
        Guid sourceContentId,
        string targetExternalKey,
        string sourceSystem,
        CancellationToken cancellationToken)
    {
        var key = (targetExternalKey ?? string.Empty).Trim();
        var system = (sourceSystem ?? string.Empty).Trim();
        if (sourceContentId == Guid.Empty || key.Length == 0 || system.Length == 0)
        {
            return;
        }

        var edges = await db.KnowledgeRelationships
            .Where(r => r.SourceContentId == sourceContentId
                        && r.Origin == KnowledgeRelationshipOrigins.Integration
                        && r.SourceSystem == system
                        && r.TargetExternalKey == key)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        if (edges.Count == 0)
        {
            return;
        }

        db.KnowledgeRelationships.RemoveRange(edges);
        var spaceId = await db.Nodes.AsNoTracking().Where(n => n.Id == sourceContentId).Select(n => (Guid?)n.SpaceId).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        var pageTitle = await db.Nodes.AsNoTracking().Where(n => n.Id == sourceContentId).Select(n => n.Title).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false) ?? string.Empty;
        if (spaceId is { } sid)
        {
            db.Publish(WikiEventTypes.ContentRelationshipRemoved, new WikiEvent(sid, sourceContentId, edges[0].CreatedBy, pageTitle, null), clock.UtcNow);
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
