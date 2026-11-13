using System.Globalization;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Email;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Messaging;
using Raavi.Wiki.ContentTypes;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public sealed record NotificationDto(
    Guid Id, string Kind, Guid SpaceId, string? SpaceKey, Guid ContentId, Guid? CommentId, Guid ActorId, string? ActorName,
    string Title, string Excerpt, DateTimeOffset CreatedAt, DateTimeOffset? ReadAt);

public sealed record NotificationListDto(IReadOnlyList<NotificationDto> Items, int UnreadCount);

public sealed record NotificationPreferencesDto(bool EmailEnabled, bool EmailAvailable);

public sealed record UpdateNotificationPreferencesRequest(bool EmailEnabled);

/// <summary>Payload of the e-mail step: the notifications of one source event that still need an e-mail.</summary>
public sealed record NotificationEmailEvent(Guid SourceEventId);

public static class NotificationEventTypes
{
    public const string Email = "notification.email";
}

/// <summary>
/// Turns comment and publishing events into per-recipient notifications (outbox handler, at-least-once, idempotent by
/// event id). Recipients: mentioned members, thread participants and page watchers, minus the actor. Every recipient's
/// View right on the page is evaluated <b>fresh</b> at fan-out time, so nobody is told about content they cannot open.
/// E-mail is a separate outbox step (<see cref="NotificationEmailer"/>) so a slow or failing relay never blocks in-app delivery.
/// </summary>
public sealed class NotificationFanOut(
    WikiDbContext db,
    WikiAuthorizer authz,
    IPrincipalDirectory directory,
    ContentProviderRegistry providers,
    IEmailSender email,
    IClock clock) : IOutboxHandler
{
    public const int MaxRecipients = 500;

    public bool CanHandle(string eventType) =>
        eventType is CommentEventTypes.Created or CommentEventTypes.Resolved or WikiEventTypes.ContentPublished or WikiEventTypes.ContentCreated
            or TaskCommentEventTypes.Created or TaskCommentEventTypes.MentionsAdded or TaskCommentEventTypes.AttachmentAdded;

    public async Task HandleAsync(OutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);
        var ct = cancellationToken;
        if (await db.Notifications.AnyAsync(n => n.EventId == outboxEvent.Id, ct).ConfigureAwait(false))
        {
            return;
        }

        if (outboxEvent.Type is TaskCommentEventTypes.Created or TaskCommentEventTypes.MentionsAdded or TaskCommentEventTypes.AttachmentAdded)
        {
            await HandleTaskAsync(outboxEvent, ct).ConfigureAwait(false);
            return;
        }

        var now = clock.UtcNow;
        Guid contentId, actorId;
        Guid? commentId = null;
        string excerpt = string.Empty;
        var recipients = new Dictionary<Guid, string>();
        var direct = new HashSet<Guid>(); // mentions reach muted watchers too

        if (outboxEvent.Type is WikiEventTypes.ContentPublished or WikiEventTypes.ContentCreated)
        {
            var e = outboxEvent.Payload<WikiEvent>();
            if (e.ContentId is not { } cid)
            {
                return;
            }

            // The page may be gone by the time the event is handled (purged, or never kept): nothing to watch or announce.
            if (!await db.Nodes.AnyAsync(n => n.Id == cid, ct).ConfigureAwait(false))
            {
                return;
            }

            contentId = cid;
            actorId = e.ActorId;
            await CommentService.AutoWatchAsync(db, contentId, actorId, now, ct).ConfigureAwait(false);
            if (outboxEvent.Type == WikiEventTypes.ContentPublished)
            {
                foreach (var w in await WatchersAsync(contentId, ct).ConfigureAwait(false))
                {
                    recipients.TryAdd(w, NotificationKinds.Published);
                }
            }
        }
        else
        {
            var e = outboxEvent.Payload<CommentEvent>();
            contentId = e.ContentId;
            actorId = e.ActorId;
            commentId = e.CommentId;
            excerpt = e.Excerpt;
            if (outboxEvent.Type == CommentEventTypes.Created)
            {
                foreach (var m in e.Mentions)
                {
                    recipients.TryAdd(m, NotificationKinds.Mention);
                    direct.Add(m);
                }

                if (e.IsReply)
                {
                    foreach (var p in await ParticipantsAsync(e.ThreadId, ct).ConfigureAwait(false))
                    {
                        recipients.TryAdd(p, NotificationKinds.Reply);
                    }
                }

                foreach (var w in await WatchersAsync(contentId, ct).ConfigureAwait(false))
                {
                    recipients.TryAdd(w, NotificationKinds.Comment);
                }
            }
            else
            {
                foreach (var p in await ParticipantsAsync(e.ThreadId, ct).ConfigureAwait(false))
                {
                    recipients.TryAdd(p, NotificationKinds.Resolved);
                }
            }
        }

        recipients.Remove(actorId);
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == contentId, ct).ConfigureAwait(false);
        if (node is null || node.Status != ContentStatus.Current || recipients.Count == 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        var muted = await db.Watches.AsNoTracking().Where(w => w.ContentId == contentId && w.State == WatchState.Muted).Select(w => w.UserId).ToListAsync(ct).ConfigureAwait(false);
        foreach (var m in muted.Where(m => !direct.Contains(m)))
        {
            recipients.Remove(m);
        }

        var space = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == node.SpaceId, ct).ConfigureAwait(false);
        var provider = providers.Get(node.ContentType);
        var candidates = recipients.Keys.Take(MaxRecipients).ToList();
        var contacts = await directory.GetContactsAsync(candidates, ct).ConfigureAwait(false);
        var prefs = await db.NotificationPreferences.AsNoTracking().Where(p => candidates.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, p => p.EmailEnabled, ct).ConfigureAwait(false);
        var anyEmail = false;
        foreach (var userId in candidates.Where(contacts.ContainsKey))
        {
            var principal = await directory.GetPrincipalAsync(userId, fresh: true, ct).ConfigureAwait(false);
            var spaceRights = await authz.GetSpaceRightsAsync(space, principal, ct).ConfigureAwait(false);
            var rights = await authz.GetContentRightsAsync(node, spaceRights, provider, principal, ct).ConfigureAwait(false);
            if (!rights.CanView)
            {
                continue;
            }

            var sendEmail = email.IsEnabled && prefs.GetValueOrDefault(userId, true) && contacts[userId].VerifiedEmail is not null;
            anyEmail |= sendEmail;
            db.Notifications.Add(Notification.Create(outboxEvent.Id, userId, recipients[userId], node.SpaceId, node.Id, commentId, actorId, node.Title, excerpt, sendEmail, now));
        }

        if (anyEmail)
        {
            db.Outbox.Add(OutboxMessage.Create(db.CurrentTenantId, NotificationEventTypes.Email, new NotificationEmailEvent(outboxEvent.Id), now));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Task collaboration notify (ADR-056): only when the task has a linked page (ContentId) so Notification.ContentId stays valid.
    /// Mentions always notify; watchers get comment/attachment kinds. View is re-checked on the linked page + space.
    /// </summary>
    private async Task HandleTaskAsync(OutboxEvent outboxEvent, CancellationToken ct)
    {
        var e = outboxEvent.Payload<TaskCommentEvent>();
        if (e.ContentId is not { } contentId)
        {
            return;
        }

        var now = clock.UtcNow;
        var recipients = new Dictionary<Guid, string>();
        var direct = new HashSet<Guid>();
        if (outboxEvent.Type is TaskCommentEventTypes.Created or TaskCommentEventTypes.MentionsAdded)
        {
            foreach (var m in e.Mentions)
            {
                recipients.TryAdd(m, NotificationKinds.Mention);
                direct.Add(m);
            }
        }

        if (outboxEvent.Type == TaskCommentEventTypes.Created)
        {
            if (e.IsReply && e.ThreadId != Guid.Empty)
            {
                foreach (var p in await TaskParticipantsAsync(e.ThreadId, ct).ConfigureAwait(false))
                {
                    recipients.TryAdd(p, NotificationKinds.Reply);
                }
            }

            foreach (var w in await TaskWatchersAsync(e.TaskId, ct).ConfigureAwait(false))
            {
                recipients.TryAdd(w, NotificationKinds.Comment);
            }
        }
        else if (outboxEvent.Type == TaskCommentEventTypes.AttachmentAdded)
        {
            foreach (var w in await TaskWatchersAsync(e.TaskId, ct).ConfigureAwait(false))
            {
                recipients.TryAdd(w, NotificationKinds.Comment);
            }
        }

        recipients.Remove(e.ActorId);
        var node = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == contentId, ct).ConfigureAwait(false);
        if (node is null || node.Status != ContentStatus.Current || recipients.Count == 0)
        {
            return;
        }

        var muted = await db.TaskWatchers.AsNoTracking()
            .Where(w => w.TaskId == e.TaskId && w.State == WatchState.Muted).Select(w => w.UserId).ToListAsync(ct).ConfigureAwait(false);
        foreach (var m in muted.Where(m => !direct.Contains(m)))
        {
            recipients.Remove(m);
        }

        var space = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == node.SpaceId, ct).ConfigureAwait(false);
        var provider = providers.Get(node.ContentType);
        var candidates = recipients.Keys.Take(MaxRecipients).ToList();
        var contacts = await directory.GetContactsAsync(candidates, ct).ConfigureAwait(false);
        var prefs = await db.NotificationPreferences.AsNoTracking().Where(p => candidates.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, p => p.EmailEnabled, ct).ConfigureAwait(false);
        var anyEmail = false;
        var commentId = e.CommentId == Guid.Empty ? (Guid?)null : e.CommentId;
        foreach (var userId in candidates.Where(contacts.ContainsKey))
        {
            var principal = await directory.GetPrincipalAsync(userId, fresh: true, ct).ConfigureAwait(false);
            var spaceRights = await authz.GetSpaceRightsAsync(space, principal, ct).ConfigureAwait(false);
            if (!spaceRights.CanView)
            {
                continue;
            }

            var rights = await authz.GetContentRightsAsync(node, spaceRights, provider, principal, ct).ConfigureAwait(false);
            if (!rights.CanView)
            {
                continue;
            }

            var sendEmail = email.IsEnabled && prefs.GetValueOrDefault(userId, true) && contacts[userId].VerifiedEmail is not null;
            anyEmail |= sendEmail;
            db.Notifications.Add(Notification.Create(outboxEvent.Id, userId, recipients[userId], e.SpaceId, contentId, commentId, e.ActorId, e.Title, e.Excerpt, sendEmail, now));
        }

        if (anyEmail)
        {
            db.Outbox.Add(OutboxMessage.Create(db.CurrentTenantId, NotificationEventTypes.Email, new NotificationEmailEvent(outboxEvent.Id), now));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task<List<Guid>> WatchersAsync(Guid contentId, CancellationToken ct) =>
        await db.Watches.AsNoTracking().Where(w => w.ContentId == contentId && w.State == WatchState.Watching)
            .Select(w => w.UserId).Take(MaxRecipients).ToListAsync(ct).ConfigureAwait(false);

    private async Task<List<Guid>> TaskWatchersAsync(Guid taskId, CancellationToken ct) =>
        await db.TaskWatchers.AsNoTracking().Where(w => w.TaskId == taskId && w.State == WatchState.Watching)
            .Select(w => w.UserId).Take(MaxRecipients).ToListAsync(ct).ConfigureAwait(false);

    private async Task<List<Guid>> ParticipantsAsync(Guid threadId, CancellationToken ct) =>
        await db.Comments.AsNoTracking().Where(c => c.ThreadId == threadId && c.DeletedAt == null)
            .Where(c => c.AuthorId != null).Select(c => c.AuthorId!.Value).Distinct().Take(MaxRecipients).ToListAsync(ct).ConfigureAwait(false);

    private async Task<List<Guid>> TaskParticipantsAsync(Guid threadId, CancellationToken ct) =>
        await db.TaskComments.AsNoTracking().Where(c => c.ThreadId == threadId && c.DeletedAt == null)
            .Select(c => c.AuthorId).Distinct().Take(MaxRecipients).ToListAsync(ct).ConfigureAwait(false);
}

/// <summary>
/// Sends the e-mail copies of one event's notifications. Only to verified addresses of members who did not switch
/// e-mail off (re-checked at send time). Progress is saved after every message, so a retry after a relay failure does
/// not resend what already went out.
/// </summary>
public sealed class NotificationEmailer(
    WikiDbContext db,
    IPrincipalDirectory directory,
    ITenantUrlResolver urls,
    IEmailSender sender,
    IClock clock) : IOutboxHandler
{
    public bool CanHandle(string eventType) => eventType == NotificationEventTypes.Email;

    public async Task HandleAsync(OutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);
        var ct = cancellationToken;
        var source = outboxEvent.Payload<NotificationEmailEvent>().SourceEventId;
        var pending = await db.Notifications.Where(n => n.EventId == source && n.Email == EmailDelivery.Pending).Take(NotificationFanOut.MaxRecipients).ToListAsync(ct).ConfigureAwait(false);
        if (pending.Count == 0)
        {
            return;
        }

        if (!sender.IsEnabled)
        {
            pending.ForEach(n => n.Emailed(EmailDelivery.Skipped, clock.UtcNow));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        var recipients = pending.Select(n => n.RecipientId).ToList();
        var contacts = await directory.GetContactsAsync(recipients, ct).ConfigureAwait(false);
        var prefs = await db.NotificationPreferences.AsNoTracking().Where(p => recipients.Contains(p.UserId)).ToDictionaryAsync(p => p.UserId, p => p.EmailEnabled, ct).ConfigureAwait(false);
        var actors = await directory.GetUserNamesAsync(pending.Select(n => n.ActorId).Distinct().ToList(), ct).ConfigureAwait(false);
        var spaceIds = pending.Select(n => n.SpaceId).Distinct().ToList();
        var spaceKeys = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Key, ct).ConfigureAwait(false);
        var baseUrl = await urls.GetCanonicalUrlAsync(outboxEvent.TenantId, ct).ConfigureAwait(false);

        foreach (var n in pending)
        {
            if (!contacts.TryGetValue(n.RecipientId, out var contact) || contact.VerifiedEmail is null || !prefs.GetValueOrDefault(n.RecipientId, true) || baseUrl is null)
            {
                n.Emailed(EmailDelivery.Skipped, clock.UtcNow);
                continue;
            }

            var link = new Uri(baseUrl, $"/spaces/{Uri.EscapeDataString(spaceKeys.GetValueOrDefault(n.SpaceId, string.Empty))}/pages/{n.ContentId:D}"
                + (n.CommentId is { } c ? $"#comment-{c:D}" : string.Empty));
            var message = NotificationEmailTemplate.Build(n, contact, actors.GetValueOrDefault(n.ActorId) ?? "—", link);
            await sender.SendAsync(message, ct).ConfigureAwait(false);
            n.Emailed(EmailDelivery.Sent, clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}

/// <summary>Plain, bilingual notification e-mails. Every user-supplied value is HTML-encoded.</summary>
public static class NotificationEmailTemplate
{
    public static EmailMessage Build(Notification n, PrincipalContact to, string actor, Uri link)
    {
        ArgumentNullException.ThrowIfNull(n);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(link);
        var fa = to.PreferredLocale != "en";
        var subject = Subject(n.Kind, fa, actor, n.Title);
        var open = fa ? "مشاهده در راوی" : "Open in Raavi";
        var footer = fa
            ? "این پیام را به این دلیل دریافت کرده‌اید که این صفحه را دنبال می‌کنید یا از شما نام برده شده است. می‌توانید ایمیل اعلان‌ها را در نمایهٔ خود خاموش کنید."
            : "You receive this because you watch this page or were mentioned. You can turn off notification e-mail in your profile.";
        var text = string.Join("\n\n", new[] { subject, n.Excerpt, $"{open}: {link}", footer }.Where(s => s.Length > 0));
        var e = (string s) => WebUtility.HtmlEncode(s);
        var dir = fa ? "rtl" : "ltr";
        var html = string.Create(CultureInfo.InvariantCulture, $"""
            <!doctype html><html lang="{(fa ? "fa" : "en")}" dir="{dir}"><body style="font-family:Tahoma,Arial,sans-serif;line-height:1.7">
            <p><strong>{e(subject)}</strong></p>
            {(n.Excerpt.Length > 0 ? $"<blockquote style=\"margin:0;padding:8px 12px;border-inline-start:3px solid #ccc\" dir=\"auto\">{e(n.Excerpt)}</blockquote>" : string.Empty)}
            <p><a href="{e(link.AbsoluteUri)}">{e(open)}</a></p>
            <p style="color:#777;font-size:12px">{e(footer)}</p>
            </body></html>
            """);
        return new EmailMessage(to.VerifiedEmail!, to.DisplayName, subject, text, html);
    }

    public static string Subject(string kind, bool fa, string actor, string title) => (kind, fa) switch
    {
        (NotificationKinds.Mention, true) => $"{actor} در «{title}» از شما نام برد",
        (NotificationKinds.Mention, false) => $"{actor} mentioned you on “{title}”",
        (NotificationKinds.Reply, true) => $"{actor} به گفت‌وگوی شما در «{title}» پاسخ داد",
        (NotificationKinds.Reply, false) => $"{actor} replied on “{title}”",
        (NotificationKinds.Resolved, true) => $"{actor} گفت‌وگویی را در «{title}» حل‌شده اعلام کرد",
        (NotificationKinds.Resolved, false) => $"{actor} resolved a thread on “{title}”",
        (NotificationKinds.Published, true) => $"{actor} نسخهٔ تازه‌ای از «{title}» منتشر کرد",
        (NotificationKinds.Published, false) => $"{actor} published “{title}”",
        (_, true) => $"{actor} در «{title}» نظر داد",
        _ => $"{actor} commented on “{title}”",
    };
}

/// <summary>The signed-in user's notification inbox and preferences (current tenant only).</summary>
public sealed class NotificationService(WikiDbContext db, WikiAuthorizer authz, IPrincipalDirectory directory, IEmailSender email, ICurrentUser current, IClock clock)
{
    public const int PageSize = 30;
    public const int MaxUnreadCounted = 99;

    public async Task<NotificationListDto> ListAsync(bool unreadOnly, DateTimeOffset? before, CancellationToken ct)
    {
        var me = current.UserId;
        var query = db.Notifications.AsNoTracking().Where(n => n.RecipientId == me);
        if (unreadOnly)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        if (before is { } b)
        {
            query = query.Where(n => n.CreatedAt < b);
        }

        var rows = await query.OrderByDescending(n => n.CreatedAt).Take(PageSize * 2).ToListAsync(ct).ConfigureAwait(false);

        // Titles are snapshots: hide notifications about content the user can no longer view.
        var viewable = await authz.FilterViewableAsync(rows.Select(r => (r.ContentId, r.SpaceId)).Distinct().ToList(), ct).ConfigureAwait(false);
        rows = rows.Where(r => viewable.Contains(r.ContentId)).Take(PageSize).ToList();
        var actors = await directory.GetUserNamesAsync(rows.Select(r => r.ActorId).Distinct().ToList(), ct).ConfigureAwait(false);
        var spaceIds = rows.Select(r => r.SpaceId).Distinct().ToList();
        var keys = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, s => s.Key, ct).ConfigureAwait(false);
        var items = rows.Select(r => new NotificationDto(r.Id, r.Kind, r.SpaceId, keys.GetValueOrDefault(r.SpaceId), r.ContentId, r.CommentId, r.ActorId, actors.GetValueOrDefault(r.ActorId),
            r.Title, r.Excerpt, r.CreatedAt, r.ReadAt)).ToList();
        return new NotificationListDto(items, await UnreadCountAsync(ct).ConfigureAwait(false));
    }

    /// <summary>Unread notifications about content the user can still view (capped at <see cref="MaxUnreadCounted"/>).</summary>
    public async Task<int> UnreadCountAsync(CancellationToken ct)
    {
        var me = current.UserId;
        var unread = await db.Notifications.AsNoTracking().Where(n => n.RecipientId == me && n.ReadAt == null)
            .OrderByDescending(n => n.CreatedAt).Take(MaxUnreadCounted * 2).Select(n => new { n.ContentId, n.SpaceId }).ToListAsync(ct).ConfigureAwait(false);
        if (unread.Count == 0)
        {
            return 0;
        }

        var viewable = await authz.FilterViewableAsync(unread.Select(u => (u.ContentId, u.SpaceId)).Distinct().ToList(), ct).ConfigureAwait(false);
        return Math.Min(MaxUnreadCounted, unread.Count(u => viewable.Contains(u.ContentId)));
    }

    public async Task<Result> MarkReadAsync(Guid id, CancellationToken ct)
    {
        var me = current.UserId;
        var n = await db.Notifications.FirstOrDefaultAsync(x => x.Id == id && x.RecipientId == me, ct).ConfigureAwait(false);
        if (n is null)
        {
            return Result.Failure(new Error("notification.not_found", "Notification not found.", ErrorKind.NotFound));
        }

        n.MarkRead(clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task MarkAllReadAsync(CancellationToken ct)
    {
        var me = current.UserId;
        var now = clock.UtcNow;
        await db.Notifications.Where(n => n.RecipientId == me && n.ReadAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAt, now), ct).ConfigureAwait(false);
    }

    public async Task<NotificationPreferencesDto> GetPreferencesAsync(CancellationToken ct)
    {
        var me = current.UserId;
        var pref = await db.NotificationPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == me, ct).ConfigureAwait(false);
        return new NotificationPreferencesDto(pref?.EmailEnabled ?? true, email.IsEnabled);
    }

    public async Task<NotificationPreferencesDto> SetPreferencesAsync(UpdateNotificationPreferencesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var me = current.UserId;
        var pref = await db.NotificationPreferences.FirstOrDefaultAsync(p => p.UserId == me, ct).ConfigureAwait(false);
        if (pref is null)
        {
            pref = NotificationPreference.Create(me, clock.UtcNow);
            db.NotificationPreferences.Add(pref);
        }

        pref.SetEmail(request.EmailEnabled, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return new NotificationPreferencesDto(pref.EmailEnabled, email.IsEnabled);
    }
}
