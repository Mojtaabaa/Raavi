using Microsoft.EntityFrameworkCore;
using Raavi.Infrastructure.Messaging;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;

namespace Raavi.Wiki.Application;

/// <summary>Outbox payload of every wiki event: ids and a title snapshot only (no bodies, no secrets).</summary>
public sealed record WikiEvent(Guid SpaceId, Guid? ContentId, Guid ActorId, string Title, int? Version, Guid? TaskId = null);

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
    public const string ContentLabelAdded = "content.label_added";
    public const string ContentLabelRemoved = "content.label_removed";
    public const string ContentOwnerChanged = "content.owner_changed";
    public const string ContentReviewScheduled = "content.review_scheduled";
    public const string ContentReviewed = "content.reviewed";
    public const string ContentClassificationChanged = "content.classification_changed";
    public const string ContentGovernanceUpdated = "content.governance_updated";
    public const string ContentRelationshipCreated = "content.relationship_created";
    public const string ContentRelationshipRemoved = "content.relationship_removed";
    public const string LabelCreated = "label.created";
    public const string LabelUpdated = "label.updated";
    public const string LabelArchived = "label.archived";
    public const string LabelRestored = "label.restored";
    public const string LabelDeleted = "label.deleted";
    public const string LabelMerged = "label.merged";
    public const string LabelMoved = "label.moved";
    public const string LabelAliasAdded = "label.alias_added";
    public const string LabelAliasRemoved = "label.alias_removed";
    public const string TaskCreated = "task.created";
    public const string TaskUpdated = "task.updated";
    public const string TaskAssigned = "task.assigned";
    public const string TaskUnassigned = "task.unassigned";
    public const string TaskCompleted = "task.completed";
    public const string TaskReopened = "task.reopened";
    public const string TaskArchived = "task.archived";
    public const string TaskCommentCreated = "task.comment.created";
    public const string TaskCommentUpdated = "task.comment.updated";
    public const string TaskCommentDeleted = "task.comment.deleted";
    public const string TaskAttachmentAdded = "task.attachment.added";
    public const string TaskAttachmentRemoved = "task.attachment.removed";
    public const string TaskWatched = "task.watched";
    public const string TaskUnwatched = "task.unwatched";
    public const string TaskTimeLogged = "task.time.logged";
    public const string TaskTimeUpdated = "task.time.updated";
    public const string TaskTimeDeleted = "task.time.deleted";
    public const string TaskEstimateOriginalChanged = "task.estimate.original_changed";
    public const string TaskEstimateRemainingChanged = "task.estimate.remaining_changed";
    public const string TaskProgressChanged = "task.progress.changed";

    public static bool IsWiki(string type) =>
        type.StartsWith("space.", StringComparison.Ordinal)
        || type.StartsWith("content.", StringComparison.Ordinal)
        || type.StartsWith("label.", StringComparison.Ordinal)
        || type.StartsWith("task.", StringComparison.Ordinal);
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
        if (e.SpaceId == Guid.Empty)
        {
            return; // Tenant-scoped label governance — not projected into a space activity feed.
        }

        db.Activity.Add(ActivityEntry.Create(outboxEvent.Id, e.SpaceId, e.ContentId, e.ActorId, outboxEvent.Type, e.Title, e.Version, outboxEvent.OccurredAt, e.TaskId));
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }
}
