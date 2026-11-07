using Microsoft.EntityFrameworkCore;
using Raavi.Infrastructure.Messaging;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;

namespace Raavi.Wiki.Application;

/// <summary>Outbox payload of every wiki event: ids and a title snapshot only (no bodies, no secrets).</summary>
public sealed record WikiEvent(Guid SpaceId, Guid? ContentId, Guid ActorId, string Title, int? Version);

public static class WikiEventTypes
{
    public const string SpaceCreated = "space.created";
    public const string SpaceUpdated = "space.updated";
    public const string SpaceArchived = "space.archived";
    public const string SpaceRestored = "space.restored";
    public const string SpacePermissionsChanged = "space.permissions_changed";
    public const string ContentCreated = "content.created";
    public const string ContentUpdated = "content.updated";
    public const string ContentMoved = "content.moved";
    public const string ContentArchived = "content.archived";
    public const string ContentRestored = "content.restored";
    public const string ContentTrashed = "content.trashed";
    public const string ContentRestrictionsChanged = "content.restrictions_changed";
    public const string ContentPublished = "content.published";
    public const string ContentPublishRequested = "content.publish_requested";
    public const string ContentPublishApproved = "content.publish_approved";
    public const string ContentPublishRejected = "content.publish_rejected";
    public const string ContentPurged = "content.purged";

    public static bool IsWiki(string type) => type.StartsWith("space.", StringComparison.Ordinal) || type.StartsWith("content.", StringComparison.Ordinal);
}

public static class WikiOutbox
{
    /// <summary>Adds the event to the same unit of work as the change (written by the caller's SaveChanges).</summary>
    public static void Publish(this WikiDbContext db, string type, WikiEvent payload, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(db);
        db.Outbox.Add(OutboxMessage.Create(db.CurrentTenantId, type, payload, now));
    }
}

/// <summary>
/// First outbox consumer (Phase 3): projects wiki events into the activity feed. Idempotent — the entry id is the event
/// id. Runs in the event's tenant scope, so RLS confines it to that tenant.
/// </summary>
public sealed class ActivityProjection(WikiDbContext db) : IOutboxHandler
{
    public bool CanHandle(string eventType) => WikiEventTypes.IsWiki(eventType);

    public async Task HandleAsync(OutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);
        if (await db.Activity.AnyAsync(a => a.Id == outboxEvent.Id, cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var e = outboxEvent.Payload<WikiEvent>();
        db.Activity.Add(ActivityEntry.Create(outboxEvent.Id, e.SpaceId, e.ContentId, e.ActorId, outboxEvent.Type, e.Title, e.Version, outboxEvent.OccurredAt));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
