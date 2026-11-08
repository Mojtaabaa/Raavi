using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Infrastructure.Messaging;
using Raavi.Wiki.ContentTypes;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public static class WorkflowNotificationKinds
{
    public const string Workflow = "workflow";
}

/// <summary>
/// Runs the enabled workflows of a space for each page event (outbox handler, Phase 10a, ADR-031). Guarantees:
/// <list type="bullet">
/// <item>at-least-once delivery is made idempotent by the run log: one run per (workflow, event), and actions are idempotent themselves;</item>
/// <item>an unexpected failure is logged on the run and rethrown, so the outbox retries it with back-off (and later gives up visibly);</item>
/// <item>a content problem (page gone, condition false, rate limit) is a <i>skipped</i> run, never a retry;</item>
/// <item>actions never emit page events and notifications re-check every recipient's View right now, so rules can neither loop nor leak;</item>
/// <item>at most <see cref="Workflow.MaxRunsPerHour"/> executed runs per workflow and hour.</item>
/// </list>
/// </summary>
public sealed class WorkflowEngine(
    WikiDbContext db,
    WikiAuthorizer authz,
    IPrincipalDirectory directory,
    ContentProviderRegistry providers,
    IClock clock) : IOutboxHandler
{
    public const int MaxWorkflowsPerEvent = 50;
    public const int MaxRecipients = 200;

    public bool CanHandle(string eventType) => WorkflowTriggers.IsValid(eventType) && eventType != WorkflowTriggers.Stale;

    public async Task HandleAsync(OutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);
        var ct = cancellationToken;
        var e = outboxEvent.Payload<WikiEvent>();
        var workflows = await db.Workflows.Where(w => w.SpaceId == e.SpaceId && w.Enabled && w.Trigger == outboxEvent.Type).OrderBy(w => w.CreatedAt).Take(MaxWorkflowsPerEvent).ToListAsync(ct).ConfigureAwait(false);
        Exception? failure = null;
        foreach (var w in workflows)
        {
            try
            {
                await RunAsync(w, outboxEvent.Id, outboxEvent.Type, e, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failure ??= ex; // the other workflows still run; the event is retried for the failed one
            }
        }

        if (failure is not null)
        {
            throw failure;
        }
    }

    /// <summary>Runs one workflow for one event. <paramref name="eventId"/> is the idempotency key (outbox event id, or a deterministic id for sweeps).</summary>
    internal async Task RunAsync(Workflow w, Guid eventId, string eventType, WikiEvent e, CancellationToken ct)
    {
        var run = await db.WorkflowRuns.FirstOrDefaultAsync(r => r.WorkflowId == w.Id && r.EventId == eventId, ct).ConfigureAwait(false);
        if (run is not null && run.Status != WorkflowRunStatus.Failed)
        {
            return; // already handled
        }

        var now = clock.UtcNow;
        if (run is null)
        {
            run = WorkflowRun.Start(w.Id, eventId, eventType, e.ContentId, now);
            db.WorkflowRuns.Add(run);
        }

        var since = now.AddHours(-1);
        var recent = await db.WorkflowRuns.CountAsync(r => r.WorkflowId == w.Id && r.StartedAt >= since && r.Status != WorkflowRunStatus.Skipped && r.Id != run.Id, ct).ConfigureAwait(false);
        if (recent >= Workflow.MaxRunsPerHour)
        {
            await FinishAsync(run, WorkflowRunStatus.Skipped, "rate_limited", ct).ConfigureAwait(false);
            return;
        }

        var node = e.ContentId is { } cid ? await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == cid, ct).ConfigureAwait(false) : null;
        if (node is null || node.Status != ContentStatus.Current && eventType != WikiEventTypes.ContentTrashed && eventType != WikiEventTypes.ContentArchived)
        {
            await FinishAsync(run, WorkflowRunStatus.Skipped, "content_gone", ct).ConfigureAwait(false);
            return;
        }

        if (!await ConditionsHoldAsync(w, node, ct).ConfigureAwait(false))
        {
            await FinishAsync(run, WorkflowRunStatus.Skipped, "conditions_not_met", ct).ConfigureAwait(false);
            return;
        }

        try
        {
            var notes = new List<string>();
            foreach (var action in w.Actions)
            {
                notes.Add(action.Type == WfActionType.AddLabel ? await AddLabelAsync(w, node, action, now, ct).ConfigureAwait(false) : await NotifyAsync(w, eventId, e, node, action, now, ct).ConfigureAwait(false));
            }

            await FinishAsync(run, WorkflowRunStatus.Succeeded, string.Join("; ", notes), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            var failed = await db.WorkflowRuns.FirstOrDefaultAsync(r => r.WorkflowId == w.Id && r.EventId == eventId, ct).ConfigureAwait(false);
            if (failed is null)
            {
                failed = WorkflowRun.Start(w.Id, eventId, eventType, e.ContentId, now);
                db.WorkflowRuns.Add(failed);
            }

            await FinishAsync(failed, WorkflowRunStatus.Failed, ex.GetType().Name + ": " + ex.Message, ct).ConfigureAwait(false);
            throw;
        }
    }

    private async Task FinishAsync(WorkflowRun run, WorkflowRunStatus status, string message, CancellationToken ct)
    {
        run.Finish(status, message, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<bool> ConditionsHoldAsync(Workflow w, ContentNode node, CancellationToken ct)
    {
        foreach (var c in w.Conditions)
        {
            var ok = c.Field switch
            {
                WfField.Title => c.Operator switch
                {
                    WfOperator.Equals => string.Equals(node.Title, c.Value, StringComparison.OrdinalIgnoreCase),
                    WfOperator.StartsWith => node.Title.StartsWith(c.Value, StringComparison.OrdinalIgnoreCase),
                    _ => node.Title.Contains(c.Value, StringComparison.OrdinalIgnoreCase),
                },
                WfField.ContentType => string.Equals(node.ContentType, c.Value, StringComparison.OrdinalIgnoreCase),
                _ => await (from cl in db.ContentLabels.AsNoTracking() join l in db.Labels.AsNoTracking() on cl.LabelId equals l.Id where cl.ContentId == node.Id && l.Name == c.Value select cl.ContentId).AnyAsync(ct).ConfigureAwait(false),
            };
            if (!ok)
            {
                return false;
            }
        }

        return true;
    }

    private async Task<string> AddLabelAsync(Workflow w, ContentNode node, WfAction action, DateTimeOffset now, CancellationToken ct)
    {
        var name = Label.Normalize(action.Label);
        var label = await db.Labels.FirstOrDefaultAsync(l => l.Name == name, ct).ConfigureAwait(false);
        if (label is null)
        {
            label = Label.Create(name, now);
            db.Labels.Add(label);
        }

        if (await db.ContentLabels.AnyAsync(cl => cl.ContentId == node.Id && cl.LabelId == label.Id, ct).ConfigureAwait(false))
        {
            return $"label '{name}' already present";
        }

        if (await db.ContentLabels.CountAsync(cl => cl.ContentId == node.Id, ct).ConfigureAwait(false) >= 100)
        {
            return $"label '{name}' not added: page has 100 labels";
        }

        db.ContentLabels.Add(ContentLabel.Create(node.Id, label.Id, w.CreatedBy, now));
        return $"label '{name}' added";
    }

    private async Task<string> NotifyAsync(Workflow w, Guid eventId, WikiEvent e, ContentNode node, WfAction action, DateTimeOffset now, CancellationToken ct)
    {
        var recipients = action.Recipients == WfRecipients.Actor
            ? new List<Guid> { e.ActorId }
            : await db.Watches.AsNoTracking().Where(x => x.ContentId == node.Id && x.State == WatchState.Watching).Select(x => x.UserId).Take(MaxRecipients).ToListAsync(ct).ConfigureAwait(false);
        recipients.Remove(Guid.Empty);
        if (action.Recipients == WfRecipients.Watchers)
        {
            recipients.Remove(e.ActorId);
        }

        var space = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == node.SpaceId, ct).ConfigureAwait(false);
        var provider = providers.Get(node.ContentType);
        var notificationId = DeterministicId(w.Id, eventId, action.Message ?? string.Empty);
        var sent = 0;
        foreach (var userId in recipients)
        {
            var principal = await directory.GetPrincipalAsync(userId, fresh: true, ct).ConfigureAwait(false);
            var spaceRights = await authz.GetSpaceRightsAsync(space, principal, ct).ConfigureAwait(false);
            var rights = await authz.GetContentRightsAsync(node, spaceRights, provider, principal, ct).ConfigureAwait(false);
            if (!rights.CanView || await db.Notifications.AnyAsync(n => n.EventId == notificationId && n.RecipientId == userId, ct).ConfigureAwait(false))
            {
                continue;
            }

            db.Notifications.Add(Notification.Create(notificationId, userId, WorkflowNotificationKinds.Workflow, node.SpaceId, node.Id, null, e.ActorId, node.Title, action.Message ?? string.Empty, false, now));
            sent++;
        }

        return $"notified {sent} of {recipients.Count}";
    }

    /// <summary>Stable id per (workflow, event, message), so a retry never notifies twice.</summary>
    private static Guid DeterministicId(Guid workflowId, Guid eventId, string message) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"{workflowId:N}|{eventId:N}|{message}"))[..16]);
}
