using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public sealed record RecurrenceDto(string Frequency, int Interval, int[] WeekDays, string Calendar, string? Until, int? Count);

public sealed record CalendarEventDto(
    Guid Id, string SpaceKey, string SpaceName, string Title, string? Description, string? Location, bool AllDay, string StartDate, string? StartTime, string EndDate, string? EndTime,
    string TimeZone, RecurrenceDto? Recurrence, string[] ExcludedDates, string? CreatedByName, DateTimeOffset CreatedAt, int Revision, bool CanEdit);

/// <summary>One concrete occurrence in a requested window. <see cref="OccurrenceDate"/> is the series start date it came from (used to cancel it).</summary>
public sealed record OccurrenceDto(
    Guid EventId, string SpaceKey, string SpaceName, string Title, string? Location, bool AllDay, string StartDate, string? StartTime, string EndDate, string? EndTime,
    string TimeZone, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, bool IsRecurring, string OccurrenceDate, bool CanEdit);

public sealed record RecurrenceRequest(string? Frequency, int? Interval, int[]? WeekDays, string? Calendar, DateOnly? Until, int? Count);

public sealed record CalendarEventRequest(
    string? Title, string? Description, string? Location, bool AllDay, DateOnly StartDate, string? StartTime, DateOnly? EndDate, string? EndTime, string? TimeZone, RecurrenceRequest? Recurrence);

public sealed record CalendarExport(string FileName, byte[] Data);

/// <summary>Space calendars (Phase 9b, ADR-027): events with recurrence in Gregorian or Persian terms, permissions from the space, iCalendar export.</summary>
public sealed class CalendarService(WikiDbContext db, SpaceService spaces, WikiAuthorizer authz, IPrincipalDirectory directory, ICurrentUser current, IClock clock)
{
    public const int MaxWindowDays = 400;
    public const int MaxOccurrences = 2000;
    public const int ExportHorizonYears = 2;

    public static readonly Error NotFound = new("calendar.event_not_found", "Event not found.", ErrorKind.NotFound);
    public static readonly Error WindowInvalid = new("calendar.window_invalid", $"Choose a window of at most {MaxWindowDays} days.", ErrorKind.Validation);
    public static readonly Error TimeInvalid = new("calendar.time_invalid", "Times must look like 09:30.", ErrorKind.Validation);
    public static readonly Error RecurrenceInvalid = new("calendar.recurrence_invalid", "The repeat rule is not valid.", ErrorKind.Validation);

    private static readonly string[] TimeFormats = ["HH:mm", "HH:mm:ss"];

    /// <summary>Occurrences in [from, to] for one space, or for every space the caller may view when <paramref name="spaceKey"/> is null.</summary>
    public async Task<Result<IReadOnlyList<OccurrenceDto>>> OccurrencesAsync(string? spaceKey, DateOnly from, DateOnly to, CancellationToken ct)
    {
        if (to < from || to.DayNumber - from.DayNumber > MaxWindowDays)
        {
            return Result<IReadOnlyList<OccurrenceDto>>.Failure(WindowInvalid);
        }

        var rights = new Dictionary<Guid, SpaceRights>();
        if (spaceKey is not null)
        {
            var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
            if (space is null)
            {
                return Result<IReadOnlyList<OccurrenceDto>>.Failure(WikiErrors.SpaceNotFound);
            }

            rights[space.Space.Id] = space;
        }
        else
        {
            var viewable = (await authz.ViewableSpaceIdsAsync(ct).ConfigureAwait(false)).ToList();
            foreach (var space in await db.Spaces.AsNoTracking().Where(s => viewable.Contains(s.Id)).ToListAsync(ct).ConfigureAwait(false))
            {
                rights[space.Id] = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
            }
        }

        var ids = rights.Keys.ToList();
        var events = await db.CalendarEvents.AsNoTracking()
            .Where(e => ids.Contains(e.SpaceId) && e.StartDate <= to && (e.RecurFrequency != RecurrenceFrequency.None || e.EndDate >= from) && (e.RecurUntil == null || e.RecurUntil >= from))
            .ToListAsync(ct).ConfigureAwait(false);

        var list = new List<OccurrenceDto>();
        foreach (var e in events)
        {
            var r = rights[e.SpaceId];
            var canEdit = r.CanWrite(PermissionAction.Edit);
            var excluded = e.ExcludedDates;
            foreach (var start in RecurrenceExpander.StartDates(e.StartDate, e.Rule, from.AddDays(-e.SpanDays), to))
            {
                if (excluded.Contains(start))
                {
                    continue;
                }

                list.Add(ToOccurrence(e, r, start, canEdit));
                if (list.Count >= MaxOccurrences)
                {
                    break;
                }
            }
        }

        IReadOnlyList<OccurrenceDto> ordered = list.OrderBy(o => o.StartDate, StringComparer.Ordinal).ThenBy(o => o.StartTime ?? string.Empty, StringComparer.Ordinal).ThenBy(o => o.Title, StringComparer.Ordinal).Take(MaxOccurrences).ToList();
        return Result<IReadOnlyList<OccurrenceDto>>.Success(ordered);
    }

    public async Task<Result<CalendarEventDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        return loaded.IsSuccess ? Result<CalendarEventDto>.Success(await ToDtoAsync(loaded.Value.Event, loaded.Value.Space, ct).ConfigureAwait(false)) : Result<CalendarEventDto>.Failure(loaded.Error!);
    }

    public async Task<Result<CalendarEventDto>> CreateAsync(string spaceKey, CalendarEventRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<CalendarEventDto>.Failure(WikiErrors.SpaceNotFound);
        }

        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<CalendarEventDto>.Failure(space.Space.Status == SpaceStatus.Archived ? WikiErrors.SpaceArchived : WikiErrors.Forbidden);
        }

        var details = ToDetails(request);
        if (!details.IsSuccess)
        {
            return Result<CalendarEventDto>.Failure(details.Error!);
        }

        var ev = CalendarEvent.Create(space.Space.Id, details.Value, current.UserId, clock.UtcNow);
        db.CalendarEvents.Add(ev);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<CalendarEventDto>.Success(await ToDtoAsync(ev, space, ct).ConfigureAwait(false));
    }

    public async Task<Result<CalendarEventDto>> UpdateAsync(Guid id, int expectedRevision, CalendarEventRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<CalendarEventDto>.Failure(loaded.Error!);
        }

        var (ev, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<CalendarEventDto>.Failure(WikiErrors.Forbidden);
        }

        if (ev.Revision != expectedRevision)
        {
            return Result<CalendarEventDto>.Failure(ETags.PreconditionFailed);
        }

        var details = ToDetails(request);
        if (!details.IsSuccess)
        {
            return Result<CalendarEventDto>.Failure(details.Error!);
        }

        ev.Update(details.Value, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<CalendarEventDto>.Success(await ToDtoAsync(ev, space, ct).ConfigureAwait(false));
    }

    /// <summary>Cancels a single occurrence of a recurring event.</summary>
    public async Task<Result<CalendarEventDto>> ExcludeAsync(Guid id, DateOnly occurrence, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<CalendarEventDto>.Failure(loaded.Error!);
        }

        var (ev, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<CalendarEventDto>.Failure(WikiErrors.Forbidden);
        }

        ev.Exclude(occurrence, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<CalendarEventDto>.Success(await ToDtoAsync(ev, space, ct).ConfigureAwait(false));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        if (!loaded.Value.Space.CanWrite(PermissionAction.Edit))
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        db.CalendarEvents.Remove(loaded.Value.Event);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>iCalendar (RFC 5545) file of all events of a space.</summary>
    public async Task<Result<CalendarExport>> ExportAsync(string spaceKey, CancellationToken ct)
    {
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<CalendarExport>.Failure(WikiErrors.SpaceNotFound);
        }

        var events = await db.CalendarEvents.AsNoTracking().Where(e => e.SpaceId == space.Space.Id).OrderBy(e => e.StartDate).ThenBy(e => e.Title).Take(5000).ToListAsync(ct).ConfigureAwait(false);
        var ics = IcsWriter.Write(events, space.Space.Name, clock.UtcNow);
        return Result<CalendarExport>.Success(new CalendarExport($"calendar-{Raavi.Wiki.Domain.Space.NormalizeKey(spaceKey)}.ics", Encoding.UTF8.GetBytes(ics)));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static Result<EventDetails> ToDetails(CalendarEventRequest r)
    {
        TimeOnly? start = null, end = null;
        if (!r.AllDay)
        {
            if (!TryTime(r.StartTime, out start) || !TryTime(r.EndTime, out end))
            {
                return Result<EventDetails>.Failure(TimeInvalid);
            }
        }

        var rule = RecurrenceRule.None;
        if (r.Recurrence is { Frequency: { } f } rr && !string.Equals(f, "None", StringComparison.OrdinalIgnoreCase))
        {
            if (!Enum.TryParse<RecurrenceFrequency>(f, true, out var freq) || (rr.Calendar is not null && !Enum.TryParse<RecurrenceCalendar>(rr.Calendar, true, out _)))
            {
                return Result<EventDetails>.Failure(RecurrenceInvalid);
            }

            var cal = rr.Calendar is null ? RecurrenceCalendar.Gregorian : Enum.Parse<RecurrenceCalendar>(rr.Calendar, true);
            var mask = 0;
            foreach (var day in rr.WeekDays ?? [])
            {
                if (day is < 0 or > 6)
                {
                    return Result<EventDetails>.Failure(RecurrenceInvalid);
                }

                mask |= 1 << day;
            }

            rule = new RecurrenceRule(freq, rr.Interval ?? 1, mask, cal, rr.Until, rr.Count);
        }

        return Result<EventDetails>.Success(new EventDetails(r.Title, r.Description, r.Location, r.AllDay, r.StartDate, start, r.EndDate ?? r.StartDate, end, r.TimeZone, rule));
    }

    private static bool TryTime(string? text, out TimeOnly? time)
    {
        time = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        if (!TimeOnly.TryParseExact(text.Trim(), TimeFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
        {
            return false;
        }

        time = t;
        return true;
    }

    private async Task<Result<(CalendarEvent Event, SpaceRights Space)>> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var ev = await (track ? db.CalendarEvents : db.CalendarEvents.AsNoTracking()).FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        var space = ev is null ? null : await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == ev.SpaceId, ct).ConfigureAwait(false);
        if (ev is null || space is null)
        {
            return Result<(CalendarEvent, SpaceRights)>.Failure(NotFound);
        }

        // An event of a space the caller cannot view does not exist for them.
        var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        return rights.CanView ? Result<(CalendarEvent, SpaceRights)>.Success((ev, rights)) : Result<(CalendarEvent, SpaceRights)>.Failure(NotFound);
    }

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Time(TimeOnly? t) => t?.ToString("HH:mm", CultureInfo.InvariantCulture);

    private async Task<CalendarEventDto> ToDtoAsync(CalendarEvent e, SpaceRights r, CancellationToken ct)
    {
        var names = await directory.GetUserNamesAsync([e.CreatedBy], ct).ConfigureAwait(false);
        var rule = e.Rule;
        var recurrence = rule.IsRecurring
            ? new RecurrenceDto(rule.Frequency.ToString(), rule.Interval, Enumerable.Range(0, 7).Where(i => (rule.WeekDays & (1 << i)) != 0).ToArray(), rule.Calendar.ToString(), rule.Until is { } u ? Date(u) : null, rule.Count)
            : null;
        return new CalendarEventDto(
            e.Id, r.Space.Key, r.Space.Name, e.Title, e.Description, e.Location, e.IsAllDay, Date(e.StartDate), Time(e.StartTime), Date(e.EndDate), Time(e.EndTime), e.TimeZone,
            recurrence, e.ExcludedDates.OrderBy(d => d).Select(Date).ToArray(), names.GetValueOrDefault(e.CreatedBy), e.CreatedAt, e.Revision, r.CanWrite(PermissionAction.Edit));
    }

    private static OccurrenceDto ToOccurrence(CalendarEvent e, SpaceRights r, DateOnly start, bool canEdit)
    {
        var end = start.AddDays(e.SpanDays);
        DateTimeOffset? startUtc = null, endUtc = null;
        if (!e.IsAllDay && e.StartTime is { } st && e.EndTime is { } et)
        {
            startUtc = ToUtc(start, st, e.TimeZone);
            endUtc = ToUtc(end, et, e.TimeZone);
        }

        return new OccurrenceDto(
            e.Id, r.Space.Key, r.Space.Name, e.Title, e.Location, e.IsAllDay, Date(start), Time(e.StartTime), Date(end), Time(e.EndTime), e.TimeZone, startUtc, endUtc,
            e.Rule.IsRecurring, Date(start), canEdit);
    }

    /// <summary>Wall-clock time in an IANA zone to UTC. A time skipped by a spring-forward change moves to the first valid instant after the gap.</summary>
    public static DateTimeOffset ToUtc(DateOnly date, TimeOnly time, string zoneId)
    {
        var zone = TimeZoneInfo.FindSystemTimeZoneById(zoneId);
        var local = date.ToDateTime(time, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(local))
        {
            local = local.AddHours(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset).ToUniversalTime();
    }
}

/// <summary>Minimal RFC 5545 writer: escaping, 75-octet line folding, RRULE for rules iCalendar can express, RDATE expansion for Persian month/year rules.</summary>
public static class IcsWriter
{
    public static string Write(IReadOnlyList<CalendarEvent> events, string calendarName, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(events);
        var sb = new StringBuilder();
        Line(sb, "BEGIN:VCALENDAR");
        Line(sb, "VERSION:2.0");
        Line(sb, "PRODID:-//Raavi//Calendar//EN");
        Line(sb, "CALSCALE:GREGORIAN");
        Line(sb, "X-WR-CALNAME:" + Escape(calendarName));
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        foreach (var e in events)
        {
            Event(sb, e, stamp);
        }

        Line(sb, "END:VCALENDAR");
        return sb.ToString();
    }

    public static string Escape(string text) =>
        new string((text ?? string.Empty).Where(c => !char.IsControl(c) || c == '\n').ToArray())
            .Replace("\\", "\\\\", StringComparison.Ordinal).Replace(";", "\\;", StringComparison.Ordinal).Replace(",", "\\,", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);

    private static void Event(StringBuilder sb, CalendarEvent e, string stamp)
    {
        Line(sb, "BEGIN:VEVENT");
        Line(sb, $"UID:{e.Id:N}@raavi");
        Line(sb, "DTSTAMP:" + stamp);
        Line(sb, "SUMMARY:" + Escape(e.Title));
        if (e.Description is not null)
        {
            Line(sb, "DESCRIPTION:" + Escape(e.Description.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')));
        }

        if (e.Location is not null)
        {
            Line(sb, "LOCATION:" + Escape(e.Location));
        }

        Line(sb, "DTSTART" + Stamp(e, e.StartDate, e.StartTime));
        Line(sb, "DTEND" + (e.IsAllDay ? Stamp(e, e.EndDate.AddDays(1), null) : Stamp(e, e.EndDate, e.EndTime)));

        var rule = e.Rule;
        var persianCalendarRule = rule.Calendar == RecurrenceCalendar.Persian && rule.Frequency is RecurrenceFrequency.Monthly or RecurrenceFrequency.Yearly;
        if (rule.IsRecurring && !persianCalendarRule)
        {
            Line(sb, "RRULE:" + RRule(e, rule));
        }
        else if (rule.IsRecurring)
        {
            // iCalendar cannot express "every Farvardin 1st": list the dates of the next years explicitly (the first one is DTSTART itself).
            var horizon = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(CalendarService.ExportHorizonYears);
            var last = e.StartDate > horizon ? e.StartDate : horizon;
            var dates = RecurrenceExpander.StartDates(e.StartDate, rule, e.StartDate.AddDays(1), last).Take(200).ToList();
            if (dates.Count > 0)
            {
                Line(sb, "RDATE" + (e.IsAllDay ? ";VALUE=DATE:" : ";TZID=" + e.TimeZone + ":") + string.Join(',', dates.Select(d => e.IsAllDay ? Day(d) : Local(d, e.StartTime!.Value))));
            }
        }

        var excluded = e.ExcludedDates.OrderBy(d => d).ToList();
        if (excluded.Count > 0)
        {
            Line(sb, "EXDATE" + (e.IsAllDay ? ";VALUE=DATE:" : ";TZID=" + e.TimeZone + ":") + string.Join(',', excluded.Select(d => e.IsAllDay ? Day(d) : Local(d, e.StartTime!.Value))));
        }

        Line(sb, "END:VEVENT");
    }

    private static string RRule(CalendarEvent e, RecurrenceRule r)
    {
        var parts = new List<string> { "FREQ=" + r.Frequency.ToString().ToUpperInvariant(), "INTERVAL=" + r.Interval.ToString(CultureInfo.InvariantCulture) };
        if (r.Frequency == RecurrenceFrequency.Weekly)
        {
            string[] names = ["SU", "MO", "TU", "WE", "TH", "FR", "SA"];
            parts.Add("BYDAY=" + string.Join(',', Enumerable.Range(0, 7).Where(i => (r.WeekDays & (1 << i)) != 0).Select(i => names[i])));
            parts.Add("WKST=" + (r.Calendar == RecurrenceCalendar.Persian ? "SA" : "MO"));
        }

        if (r.Count is { } c)
        {
            parts.Add("COUNT=" + c.ToString(CultureInfo.InvariantCulture));
        }
        else if (r.Until is { } until)
        {
            // With a time zone the UNTIL must be UTC: the last instant of the final day, local.
            parts.Add("UNTIL=" + (e.IsAllDay
                ? Day(until)
                : CalendarService.ToUtc(until, new TimeOnly(23, 59, 59), e.TimeZone).UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture)));
        }

        return string.Join(';', parts);
    }

    private static string Stamp(CalendarEvent e, DateOnly date, TimeOnly? time) =>
        e.IsAllDay ? ";VALUE=DATE:" + Day(date) : ";TZID=" + e.TimeZone + ":" + Local(date, time ?? TimeOnly.MinValue);

    private static string Day(DateOnly d) => d.ToString("yyyyMMdd", CultureInfo.InvariantCulture);

    private static string Local(DateOnly d, TimeOnly t) => Day(d) + "T" + t.ToString("HHmmss", CultureInfo.InvariantCulture);

    /// <summary>Folds lines longer than 75 octets (continuation lines start with one space), never splitting a UTF-8 sequence.</summary>
    private static void Line(StringBuilder sb, string line)
    {
        var bytes = 0;
        var firstLine = true;
        foreach (var rune in line.EnumerateRunes())
        {
            var size = rune.Utf8SequenceLength;
            var limit = firstLine ? 75 : 74;
            if (bytes + size > limit)
            {
                sb.Append("\r\n ");
                bytes = 0;
                firstLine = false;
            }

            sb.Append(rune.ToString());
            bytes += size;
        }

        sb.Append("\r\n");
    }
}
