using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Content;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Jobs;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Messaging;
using Raavi.Wiki.ContentTypes;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public sealed record ApproverDto(Guid UserId, string? Name, string Decision, string? Comment, DateTimeOffset? DecidedAt);

public sealed record ApprovalDto(
    Guid Id, Guid ContentId, string ContentTitle, string SpaceKey, string Note, string Mode, string Status, int VersionNumber, int CurrentVersionNumber, bool PageChanged,
    Guid RequestedBy, string? RequestedByName, DateTimeOffset RequestedAt, DateTimeOffset? DueAt, bool Overdue, DateTimeOffset? DecidedAt, IReadOnlyList<ApproverDto> Approvers, bool CanDecide, bool CanCancel);

public sealed record ApprovalRequestBody(string? Note, string? Mode, Guid[]? ApproverIds, DateTimeOffset? DueAt);

public sealed record ApprovalDecisionBody(string? Decision, string? Comment);

/// <summary>Outbox payload of approval events: ids and a title snapshot only.</summary>
public sealed record ApprovalEvent(Guid RequestId, Guid SpaceId, Guid ContentId, Guid ActorId, string Title, string Status);

public static class ApprovalEventTypes
{
    public const string Requested = "approval.requested";
    public const string Decided = "approval.decided";
    public const string Reminder = "approval.reminder";
    public const string Overdue = "approval.overdue";
}

public static class ApprovalNotificationKinds
{
    public const string Requested = "approval.requested";
    public const string Approved = "approval.approved";
    public const string Rejected = "approval.rejected";
    public const string Cancelled = "approval.cancelled";
    public const string Reminder = "approval.reminder";
    public const string Overdue = "approval.overdue";
}

/// <summary>Page approvals (Phase 10b, ADR-032): request, decide, cancel, inbox. Notifications come from <see cref="ApprovalNotifier"/>.</summary>
public sealed class ApprovalService(WikiDbContext db, PageService pages, IPrincipalDirectory directory, WikiAuthorizer authz, ICurrentUser current, IClock clock, ITenantJobScheduler jobs)
{
    public static readonly Error NotFound = new("approval.not_found", "Approval request not found.", ErrorKind.NotFound);
    public static readonly Error NotAnApprover = new("approval.not_approver", "You are not an approver of this request.", ErrorKind.Forbidden);
    public static readonly Error SelfApprover = new("approval.self_approver", "You cannot ask yourself to approve your own request.", ErrorKind.Validation);
    public static readonly Error ApproversInvalid = new("approval.approvers_invalid", $"Choose 1–{ApprovalRequest.MaxApprovers} different approvers who exist and can read the page.", ErrorKind.Validation);
    public static readonly Error TooManyPending = new("approval.already_pending", "This page already has a pending approval request.", ErrorKind.Conflict);

    public async Task<Result<ApprovalDto>> RequestAsync(Guid contentId, ApprovalRequestBody body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var loaded = await pages.LoadAsync(contentId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<ApprovalDto>.Failure(loaded.Error!);
        }

        var (node, space, provider, rights) = loaded.Value;
        if (!rights.CanEdit)
        {
            return Result<ApprovalDto>.Failure(WikiErrors.Forbidden);
        }

        if (space.Space.Status == SpaceStatus.Archived)
        {
            return Result<ApprovalDto>.Failure(WikiErrors.SpaceArchived);
        }

        var mode = ApprovalMode.All;
        if (!string.IsNullOrWhiteSpace(body.Mode) && !Enum.TryParse(body.Mode, true, out mode))
        {
            return Result<ApprovalDto>.Failure(new Error("approval.mode_invalid", "The approval mode must be All or Any.", ErrorKind.Validation));
        }

        var ids = (body.ApproverIds ?? []).Distinct().ToList();
        if (ids.Count is 0 or > ApprovalRequest.MaxApprovers)
        {
            return Result<ApprovalDto>.Failure(ApproversInvalid);
        }

        if (ids.Contains(current.UserId))
        {
            return Result<ApprovalDto>.Failure(SelfApprover);
        }

        var names = await directory.GetUserNamesAsync(ids, ct).ConfigureAwait(false);
        foreach (var userId in ids)
        {
            if (!names.ContainsKey(userId) || !await CanViewAsync(node, space.Space, provider, userId, ct).ConfigureAwait(false))
            {
                return Result<ApprovalDto>.Failure(ApproversInvalid);
            }
        }

        if (await db.ApprovalRequests.AnyAsync(r => r.ContentId == contentId && r.Status == ApprovalStatus.Pending, ct).ConfigureAwait(false))
        {
            return Result<ApprovalDto>.Failure(TooManyPending);
        }

        var now = clock.UtcNow;
        var request = ApprovalRequest.Create(node.SpaceId, node.Id, node.CurrentVersionNumber, body.Note, mode, body.DueAt, current.UserId, now);
        db.ApprovalRequests.Add(request);
        foreach (var userId in ids)
        {
            db.ApprovalAssignments.Add(ApprovalAssignment.Create(request.Id, userId));
        }

        Emit(ApprovalEventTypes.Requested, new ApprovalEvent(request.Id, node.SpaceId, node.Id, current.UserId, node.Title, request.Status.ToString()), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        ScheduleReminders(request, now);
        return await GetAsync(request.Id, ct).ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlyList<ApprovalDto>>> ForPageAsync(Guid contentId, CancellationToken ct)
    {
        var loaded = await pages.LoadAsync(contentId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<ApprovalDto>>.Failure(loaded.Error!);
        }

        var list = await db.ApprovalRequests.AsNoTracking().Where(r => r.ContentId == contentId).OrderByDescending(r => r.RequestedAt).Take(50).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<ApprovalDto>>.Success(await ToDtosAsync(list, loaded.Value.Space.CanAdminister, ct).ConfigureAwait(false));
    }

    public async Task<Result<ApprovalDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        return loaded.IsSuccess
            ? Result<ApprovalDto>.Success((await ToDtosAsync([loaded.Value.Request], loaded.Value.Content.Space.CanAdminister, ct).ConfigureAwait(false))[0])
            : Result<ApprovalDto>.Failure(loaded.Error!);
    }

    /// <summary><c>inbox</c>: requests waiting for my decision. <c>requested</c>: requests I made. Pages I can no longer read are left out.</summary>
    public async Task<Result<IReadOnlyList<ApprovalDto>>> ListAsync(string scope, CancellationToken ct)
    {
        var me = current.UserId;
        List<ApprovalRequest> requests;
        if (string.Equals(scope, "inbox", StringComparison.OrdinalIgnoreCase))
        {
            requests = await (from a in db.ApprovalAssignments.AsNoTracking()
                              join r in db.ApprovalRequests.AsNoTracking() on a.RequestId equals r.Id
                              where a.UserId == me && a.Decision == ApprovalDecision.Pending && r.Status == ApprovalStatus.Pending
                              orderby r.DueAt == null, r.DueAt, r.RequestedAt
                              select r).Take(50).ToListAsync(ct).ConfigureAwait(false);
        }
        else if (string.Equals(scope, "requested", StringComparison.OrdinalIgnoreCase))
        {
            requests = await db.ApprovalRequests.AsNoTracking().Where(r => r.RequestedBy == me).OrderByDescending(r => r.RequestedAt).Take(50).ToListAsync(ct).ConfigureAwait(false);
        }
        else
        {
            return Result<IReadOnlyList<ApprovalDto>>.Failure(new Error("approval.scope_invalid", "The scope must be inbox or requested.", ErrorKind.Validation));
        }

        var visible = new List<ApprovalRequest>();
        foreach (var r in requests)
        {
            var loaded = await pages.LoadAsync(r.ContentId, track: false, ct).ConfigureAwait(false);
            if (loaded.IsSuccess)
            {
                visible.Add(r);
            }
        }

        return Result<IReadOnlyList<ApprovalDto>>.Success(await ToDtosAsync(visible, false, ct).ConfigureAwait(false));
    }

    public async Task<Result<ApprovalDto>> DecideAsync(Guid id, ApprovalDecisionBody body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<ApprovalDto>.Failure(loaded.Error!);
        }

        var request = loaded.Value.Request;
        if (request.Status != ApprovalStatus.Pending)
        {
            return Result<ApprovalDto>.Failure(new Error("approval.not_pending", "This request has already been decided.", ErrorKind.Conflict));
        }

        var assignments = await db.ApprovalAssignments.Where(a => a.RequestId == id).ToListAsync(ct).ConfigureAwait(false);
        var mine = assignments.FirstOrDefault(a => a.UserId == current.UserId);
        if (mine is null)
        {
            return Result<ApprovalDto>.Failure(NotAnApprover);
        }

        if (!Enum.TryParse<ApprovalDecision>(body.Decision, true, out var decision) || decision == ApprovalDecision.Pending)
        {
            return Result<ApprovalDto>.Failure(new Error("approval.decision_invalid", "The decision must be Approved or Rejected.", ErrorKind.Validation));
        }

        var now = clock.UtcNow;
        mine.Decide(decision, body.Comment, now);
        if (request.Resolve(assignments, now))
        {
            Emit(ApprovalEventTypes.Decided, new ApprovalEvent(request.Id, request.SpaceId, request.ContentId, current.UserId, loaded.Value.Content.Node.Title, request.Status.ToString()), now);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    public async Task<Result<ApprovalDto>> CancelAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<ApprovalDto>.Failure(loaded.Error!);
        }

        var (request, content) = loaded.Value;
        if (request.RequestedBy != current.UserId && !content.Space.CanAdminister)
        {
            return Result<ApprovalDto>.Failure(WikiErrors.Forbidden);
        }

        if (request.Status != ApprovalStatus.Pending)
        {
            return Result<ApprovalDto>.Failure(new Error("approval.not_pending", "This request has already been decided.", ErrorKind.Conflict));
        }

        var now = clock.UtcNow;
        request.Cancel(now);
        Emit(ApprovalEventTypes.Decided, new ApprovalEvent(request.Id, request.SpaceId, request.ContentId, current.UserId, content.Node.Title, request.Status.ToString()), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>A reminder one day before the due date (when that is still ahead) and an overdue notice at the due date. Both re-check the request when they run.</summary>
    private void ScheduleReminders(ApprovalRequest request, DateTimeOffset now)
    {
        if (request.DueAt is not { } due)
        {
            return;
        }

        if (due.AddDays(-1) > now.AddMinutes(1))
        {
            jobs.Schedule<ApprovalReminderJob, ApprovalReminderArgs>(new ApprovalReminderArgs(request.Id, ApprovalReminderKind.Reminder, due), due.AddDays(-1));
        }

        jobs.Schedule<ApprovalReminderJob, ApprovalReminderArgs>(new ApprovalReminderArgs(request.Id, ApprovalReminderKind.Overdue, due), due);
    }

    /// <summary>Runs inside <see cref="ApprovalReminderJob"/> (no user): does nothing unless the request is still pending with the same due date.</summary>
    public async Task RunReminderAsync(ApprovalReminderArgs args, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(args);
        var request = await db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == args.RequestId, ct).ConfigureAwait(false);
        if (request is null || request.Status != ApprovalStatus.Pending || request.DueAt != args.DueAt)
        {
            return;
        }

        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == request.ContentId, ct).ConfigureAwait(false);
        if (node is null)
        {
            return;
        }

        Emit(args.Kind == ApprovalReminderKind.Overdue ? ApprovalEventTypes.Overdue : ApprovalEventTypes.Reminder, new ApprovalEvent(request.Id, request.SpaceId, request.ContentId, Guid.Empty, node.Title, request.Status.ToString()), clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private void Emit(string type, ApprovalEvent payload, DateTimeOffset now) => db.Outbox.Add(OutboxMessage.Create(db.CurrentTenantId, type, payload, now));

    private async Task<bool> CanViewAsync(ContentNode node, Space space, IContentProvider provider, Guid userId, CancellationToken ct)
    {
        var principal = await directory.GetPrincipalAsync(userId, fresh: true, ct).ConfigureAwait(false);
        var spaceRights = await authz.GetSpaceRightsAsync(space, principal, ct).ConfigureAwait(false);
        var rights = await authz.GetContentRightsAsync(node, spaceRights, provider, principal, ct).ConfigureAwait(false);
        return rights.CanView;
    }

    private async Task<Result<(ApprovalRequest Request, LoadedContent Content)>> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var request = await (track ? db.ApprovalRequests : db.ApprovalRequests.AsNoTracking()).FirstOrDefaultAsync(r => r.Id == id, ct).ConfigureAwait(false);
        if (request is null)
        {
            return Result<(ApprovalRequest, LoadedContent)>.Failure(NotFound);
        }

        // A request for a page the caller cannot read does not exist for them.
        var content = await pages.LoadAsync(request.ContentId, track: false, ct).ConfigureAwait(false);
        return content.IsSuccess ? Result<(ApprovalRequest, LoadedContent)>.Success((request, content.Value)) : Result<(ApprovalRequest, LoadedContent)>.Failure(NotFound);
    }

    private async Task<IReadOnlyList<ApprovalDto>> ToDtosAsync(List<ApprovalRequest> requests, bool isSpaceAdmin, CancellationToken ct)
    {
        if (requests.Count == 0)
        {
            return [];
        }

        var ids = requests.Select(r => r.Id).ToList();
        var assignments = (await db.ApprovalAssignments.AsNoTracking().Where(a => ids.Contains(a.RequestId)).ToListAsync(ct).ConfigureAwait(false)).ToLookup(a => a.RequestId);
        var contentIds = requests.Select(r => r.ContentId).Distinct().ToList();
        var nodes = await db.Nodes.AsNoTracking().Where(n => contentIds.Contains(n.Id)).ToDictionaryAsync(n => n.Id, ct).ConfigureAwait(false);
        var spaceIds = requests.Select(r => r.SpaceId).Distinct().ToList();
        var keys = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Key, ct).ConfigureAwait(false);
        var userIds = requests.Select(r => r.RequestedBy).Concat(assignments.SelectMany(g => g).Select(a => a.UserId)).Distinct().ToList();
        var names = await directory.GetUserNamesAsync(userIds, ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        var me = current.UserId;
        return requests.Select(r =>
        {
            var list = assignments[r.Id].ToList();
            nodes.TryGetValue(r.ContentId, out var node);
            var pending = r.Status == ApprovalStatus.Pending;
            return new ApprovalDto(
                r.Id, r.ContentId, node?.Title ?? string.Empty, keys.GetValueOrDefault(r.SpaceId, string.Empty), r.Note, r.Mode.ToString(), r.Status.ToString(), r.VersionNumber, node?.CurrentVersionNumber ?? 0,
                node is not null && node.CurrentVersionNumber != r.VersionNumber, r.RequestedBy, names.GetValueOrDefault(r.RequestedBy), r.RequestedAt, r.DueAt, r.IsOverdue(now), r.DecidedAt,
                list.Select(a => new ApproverDto(a.UserId, names.GetValueOrDefault(a.UserId), a.Decision.ToString(), a.Comment, a.DecidedAt)).ToList(),
                pending && list.Any(a => a.UserId == me && a.Decision == ApprovalDecision.Pending), pending && (r.RequestedBy == me || isSpaceAdmin));
        }).ToList();
    }
}

/// <summary>
/// Outbox handler: tells approvers about a new request and the requester about the outcome (Phase 10b). Idempotent per event and recipient;
/// every recipient's View right on the page is evaluated at that moment.
/// </summary>
public sealed class ApprovalNotifier(WikiDbContext db, WikiAuthorizer authz, IPrincipalDirectory directory, ContentProviderRegistry providers, IClock clock) : IOutboxHandler
{
    public bool CanHandle(string eventType) => eventType is ApprovalEventTypes.Requested or ApprovalEventTypes.Decided or ApprovalEventTypes.Reminder or ApprovalEventTypes.Overdue;

    public async Task HandleAsync(OutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);
        var ct = cancellationToken;
        var e = outboxEvent.Payload<ApprovalEvent>();
        var request = await db.ApprovalRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == e.RequestId, ct).ConfigureAwait(false);
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == e.ContentId, ct).ConfigureAwait(false);
        if (request is null || node is null || node.Status != ContentStatus.Current)
        {
            return;
        }

        string kind;
        List<Guid> recipients;
        if (outboxEvent.Type == ApprovalEventTypes.Requested)
        {
            kind = ApprovalNotificationKinds.Requested;
            recipients = await db.ApprovalAssignments.AsNoTracking().Where(a => a.RequestId == request.Id).Select(a => a.UserId).ToListAsync(ct).ConfigureAwait(false);
        }
        else if (outboxEvent.Type is ApprovalEventTypes.Reminder or ApprovalEventTypes.Overdue)
        {
            var overdue = outboxEvent.Type == ApprovalEventTypes.Overdue;
            kind = overdue ? ApprovalNotificationKinds.Overdue : ApprovalNotificationKinds.Reminder;
            recipients = await db.ApprovalAssignments.AsNoTracking().Where(a => a.RequestId == request.Id && a.Decision == ApprovalDecision.Pending).Select(a => a.UserId).ToListAsync(ct).ConfigureAwait(false);
            if (overdue)
            {
                recipients.Add(request.RequestedBy); // the requester learns that nobody has decided in time
            }
        }
        else
        {
            kind = e.Status switch
            {
                nameof(ApprovalStatus.Approved) => ApprovalNotificationKinds.Approved,
                nameof(ApprovalStatus.Rejected) => ApprovalNotificationKinds.Rejected,
                _ => ApprovalNotificationKinds.Cancelled,
            };
            recipients = kind == ApprovalNotificationKinds.Cancelled
                ? await db.ApprovalAssignments.AsNoTracking().Where(a => a.RequestId == request.Id && a.Decision == ApprovalDecision.Pending).Select(a => a.UserId).ToListAsync(ct).ConfigureAwait(false)
                : [request.RequestedBy];
        }

        recipients.Remove(e.ActorId);
        var space = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == node.SpaceId, ct).ConfigureAwait(false);
        var provider = providers.Get(node.ContentType);
        var now = clock.UtcNow;
        foreach (var userId in recipients)
        {
            var principal = await directory.GetPrincipalAsync(userId, fresh: true, ct).ConfigureAwait(false);
            var spaceRights = await authz.GetSpaceRightsAsync(space, principal, ct).ConfigureAwait(false);
            var rights = await authz.GetContentRightsAsync(node, spaceRights, provider, principal, ct).ConfigureAwait(false);
            if (!rights.CanView || await db.Notifications.AnyAsync(n => n.EventId == outboxEvent.Id && n.RecipientId == userId, ct).ConfigureAwait(false))
            {
                continue;
            }

            db.Notifications.Add(Notification.Create(outboxEvent.Id, userId, kind, node.SpaceId, node.Id, null, e.ActorId, node.Title, request.Note, false, now));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

public enum ApprovalReminderKind
{
    Reminder = 1,
    Overdue = 2,
}

public sealed record ApprovalReminderArgs(Guid RequestId, ApprovalReminderKind Kind, DateTimeOffset DueAt);

/// <summary>Tenant job behind the approval reminders and overdue notices (Phase 10c).</summary>
public sealed class ApprovalReminderJob(ApprovalService approvals) : ITenantJob<ApprovalReminderArgs>
{
    public Task ExecuteAsync(ApprovalReminderArgs args, CancellationToken cancellationToken) => approvals.RunReminderAsync(args, cancellationToken);
}
