using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Jobs;
using Raavi.Application.Abstractions.Time;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;

namespace Raavi.Wiki.Application;

public sealed record CalendarReminderSweepArgs(int Version = 1);

/// <summary>
/// Delivers due calendar reminders as in-app notifications (idempotent per reminder + occurrence date).
/// A system fan-out enqueues this for every active tenant every few minutes.
/// </summary>
public sealed class CalendarReminderJob(WikiDbContext db, IClock clock) : ITenantJob<CalendarReminderSweepArgs>
{
    public const string RecurringJobId = "system:calendar-reminders";
    public const string NotificationKind = "calendar.reminder";

    public async Task ExecuteAsync(CalendarReminderSweepArgs args, CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var horizon = now.AddHours(2);
        var reminders = await db.CalendarEventReminders.Where(r => r.InApp || r.Email).Take(2000).ToListAsync(cancellationToken).ConfigureAwait(false);
        if (reminders.Count == 0)
        {
            return;
        }

        var eventIds = reminders.Select(r => r.EventId).Distinct().ToList();
        var events = await db.CalendarEvents.Where(e => eventIds.Contains(e.Id)).ToDictionaryAsync(e => e.Id, cancellationToken).ConfigureAwait(false);
        var prefs = await db.CalendarUserPreferences.AsNoTracking().ToDictionaryAsync(p => p.UserId, cancellationToken).ConfigureAwait(false);

        foreach (var reminder in reminders)
        {
            if (!events.TryGetValue(reminder.EventId, out var ev))
            {
                continue;
            }

            var windowFrom = DateOnly.FromDateTime(now.UtcDateTime.AddDays(-1));
            var windowTo = DateOnly.FromDateTime(horizon.UtcDateTime.AddDays(1));
            foreach (var start in RecurrenceExpander.StartDates(ev.StartDate, ev.Rule, windowFrom, windowTo))
            {
                if (ev.ExcludedDates.Contains(start))
                {
                    continue;
                }

                var fireAt = FireAt(ev, start, reminder.MinutesBefore);
                if (fireAt > horizon || fireAt < now.AddHours(-6))
                {
                    continue;
                }

                var occKey = start.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
                if (string.Equals(reminder.LastDeliveredOccurrence, occKey, StringComparison.Ordinal))
                {
                    continue;
                }

                var recipient = ev.OrganizerId != Guid.Empty ? ev.OrganizerId : ev.CreatedBy;
                if (prefs.TryGetValue(recipient, out var pref) && pref.IsQuiet(now))
                {
                    continue;
                }

                var title = ev.IsPrivate && (prefs.GetValueOrDefault(recipient)?.HidePrivateTitlesInTts ?? true)
                    ? "Calendar reminder"
                    : ev.Title;
                var excerpt = ev.IsAllDay ? occKey : $"{occKey} {ev.StartTime:HH\\:mm}";
                // Notifications require a Content FK; only emit when the event links a real page.
                if (ev.LinkedPageId is { } contentId && contentId != Guid.Empty)
                {
                    db.Notifications.Add(Notification.Create(
                        Guid.NewGuid(), recipient, NotificationKind, ev.SpaceId ?? Guid.Empty, contentId, null, recipient, title, excerpt, reminder.Email, now));
                }

                reminder.MarkDelivered(occKey, now);
            }
        }

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private static DateTimeOffset FireAt(CalendarEvent ev, DateOnly start, int minutesBefore)
    {
        if (ev.IsAllDay)
        {
            return new DateTimeOffset(start.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero).AddMinutes(-minutesBefore);
        }

        var startUtc = CalendarService.ToUtc(start, ev.StartTime ?? TimeOnly.MinValue, ev.TimeZone);
        return startUtc.AddMinutes(-minutesBefore);
    }
}
