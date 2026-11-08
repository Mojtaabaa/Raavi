using System.Globalization;
using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

public enum RecurrenceFrequency
{
    None = 0,
    Daily = 1,
    Weekly = 2,
    Monthly = 3,
    Yearly = 4,
}

/// <summary>The calendar in which monthly and yearly rules are evaluated ("the 1st of Farvardin" differs from "the 21st of March").</summary>
public enum RecurrenceCalendar
{
    Gregorian = 0,
    Persian = 1,
}

/// <summary>
/// A repeat rule: every <see cref="Interval"/> days/weeks/months/years, optionally on weekdays (bit 0 = Sunday … bit 6 = Saturday),
/// ending after <see cref="Count"/> occurrences or on <see cref="Until"/> (never both, as in RFC 5545).
/// </summary>
public sealed record RecurrenceRule(RecurrenceFrequency Frequency, int Interval, int WeekDays, RecurrenceCalendar Calendar, DateOnly? Until, int? Count)
{
    public static readonly RecurrenceRule None = new(RecurrenceFrequency.None, 1, 0, RecurrenceCalendar.Gregorian, null, null);

    public bool IsRecurring => Frequency != RecurrenceFrequency.None;
}

/// <summary>
/// Calendar events of a space (Phase 9b): all-day or timed, in the event's own time zone, optionally recurring. Dates and times are stored as
/// wall-clock values plus the IANA zone, so a weekly 09:00 meeting stays at 09:00 across daylight-saving changes. Permissions come from the
/// space (read = View; create/edit/delete = Edit on an active space).
/// </summary>
public sealed class CalendarEvent : Entity, ITenantOwned
{
    public const int MaxTitleLength = 200;
    public const int MaxDescriptionLength = 4000;
    public const int MaxLocationLength = 200;
    public const int MaxSpanDays = 366;
    public const int MaxExcluded = 200;
    public const int MaxInterval = 99;
    public const int MaxCount = 1000;

    private CalendarEvent() { }

    public Guid TenantId { get; private set; }

    public Guid SpaceId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string? Location { get; private set; }

    public bool IsAllDay { get; private set; }

    public DateOnly StartDate { get; private set; }

    /// <summary>Wall-clock start; null for all-day events.</summary>
    public TimeOnly? StartTime { get; private set; }

    /// <summary>Last day of the event (inclusive).</summary>
    public DateOnly EndDate { get; private set; }

    public TimeOnly? EndTime { get; private set; }

    /// <summary>IANA time zone the wall-clock values belong to.</summary>
    public string TimeZone { get; private set; } = "UTC";

    public RecurrenceFrequency RecurFrequency { get; private set; }

    public int RecurInterval { get; private set; } = 1;

    public int RecurWeekDays { get; private set; }

    public RecurrenceCalendar RecurCalendar { get; private set; }

    public DateOnly? RecurUntil { get; private set; }

    public int? RecurCount { get; private set; }

    /// <summary>Cancelled occurrences, as comma-separated yyyy-MM-dd start dates.</summary>
    public string? ExcludedDatesRaw { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public RecurrenceRule Rule => new(RecurFrequency, RecurInterval, RecurWeekDays, RecurCalendar, RecurUntil, RecurCount);

    public int SpanDays => EndDate.DayNumber - StartDate.DayNumber;

    public IReadOnlySet<DateOnly> ExcludedDates =>
        string.IsNullOrEmpty(ExcludedDatesRaw)
            ? new HashSet<DateOnly>()
            : ExcludedDatesRaw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToHashSet();

    public static CalendarEvent Create(Guid spaceId, EventDetails details, Guid userId, DateTimeOffset now)
    {
        var e = new CalendarEvent { SpaceId = spaceId, CreatedBy = userId, CreatedAt = now, UpdatedAt = now };
        e.Apply(details);
        return e;
    }

    public void Update(EventDetails details, DateTimeOffset now)
    {
        Apply(details);
        Touch(now);
    }

    /// <summary>Cancels one occurrence of a recurring event; <paramref name="occurrenceStart"/> must be a start date the rule produces.</summary>
    public void Exclude(DateOnly occurrenceStart, DateTimeOffset now)
    {
        if (!Rule.IsRecurring || !RecurrenceExpander.Occurs(StartDate, Rule, occurrenceStart))
        {
            throw new DomainException("calendar.occurrence_invalid", "The date is not an occurrence of this event.");
        }

        var set = ExcludedDates.ToHashSet();
        if (!set.Add(occurrenceStart))
        {
            return;
        }

        if (set.Count > MaxExcluded)
        {
            throw new DomainException("calendar.too_many_exclusions", $"At most {MaxExcluded} occurrences can be cancelled; change the series instead.");
        }

        ExcludedDatesRaw = string.Join(',', set.OrderBy(d => d).Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        Touch(now);
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Revision++;
    }

    private void Apply(EventDetails d)
    {
        var title = (d.Title ?? string.Empty).Trim();
        if (title.Length is 0 or > MaxTitleLength || title.Any(char.IsControl))
        {
            throw new DomainException("calendar.title_invalid", $"An event needs a title of 1–{MaxTitleLength} characters.");
        }

        var description = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description.Trim();
        if (description is { Length: > MaxDescriptionLength })
        {
            throw new DomainException("calendar.description_too_long", $"The description is limited to {MaxDescriptionLength} characters.");
        }

        var location = string.IsNullOrWhiteSpace(d.Location) ? null : d.Location.Trim();
        if (location is { Length: > MaxLocationLength } || (location?.Any(char.IsControl) ?? false))
        {
            throw new DomainException("calendar.location_invalid", $"The location is limited to {MaxLocationLength} characters.");
        }

        var zone = string.IsNullOrWhiteSpace(d.TimeZone) ? "UTC" : d.TimeZone.Trim();
        if (zone.Length > 64 || !TimeZoneInfo.TryFindSystemTimeZoneById(zone, out _))
        {
            throw new DomainException("calendar.time_zone_invalid", "The time zone is not recognised.");
        }

        if (d.AllDay)
        {
            if (d.EndDate < d.StartDate)
            {
                throw new DomainException("calendar.range_invalid", "The event cannot end before it starts.");
            }
        }
        else
        {
            if (d.StartTime is null || d.EndTime is null)
            {
                throw new DomainException("calendar.range_invalid", "A timed event needs a start and an end time.");
            }

            if (d.EndDate < d.StartDate || (d.EndDate == d.StartDate && d.EndTime < d.StartTime))
            {
                throw new DomainException("calendar.range_invalid", "The event cannot end before it starts.");
            }
        }

        if (d.EndDate.DayNumber - d.StartDate.DayNumber > MaxSpanDays)
        {
            throw new DomainException("calendar.range_too_long", $"An event can span at most {MaxSpanDays} days.");
        }

        if (d.StartDate.Year < 1900 || d.StartDate.Year > 2200)
        {
            throw new DomainException("calendar.range_invalid", "The date is outside the supported range.");
        }

        var rule = Normalise(d.Recurrence ?? RecurrenceRule.None, d.StartDate);

        Title = title;
        Description = description;
        Location = location;
        IsAllDay = d.AllDay;
        StartDate = d.StartDate;
        EndDate = d.EndDate;
        StartTime = d.AllDay ? null : d.StartTime;
        EndTime = d.AllDay ? null : d.EndTime;
        TimeZone = zone;
        RecurFrequency = rule.Frequency;
        RecurInterval = rule.Interval;
        RecurWeekDays = rule.WeekDays;
        RecurCalendar = rule.Calendar;
        RecurUntil = rule.Until;
        RecurCount = rule.Count;
        // Changing the series invalidates earlier cancellations only if they no longer occur.
        ExcludedDatesRaw = Rule.IsRecurring ? Keep(ExcludedDatesRaw, StartDate, rule) : null;
    }

    private static string? Keep(string? raw, DateOnly start, RecurrenceRule rule)
    {
        if (string.IsNullOrEmpty(raw))
        {
            return null;
        }

        var kept = raw.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(s => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture))
            .Where(date => RecurrenceExpander.Occurs(start, rule, date))
            .Select(date => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            .ToList();
        return kept.Count == 0 ? null : string.Join(',', kept);
    }

    private static RecurrenceRule Normalise(RecurrenceRule r, DateOnly start)
    {
        if (!r.IsRecurring)
        {
            return RecurrenceRule.None;
        }

        if (!Enum.IsDefined(r.Frequency) || !Enum.IsDefined(r.Calendar) || r.Interval is < 1 or > MaxInterval || (r.WeekDays & ~0x7F) != 0)
        {
            throw new DomainException("calendar.recurrence_invalid", "The repeat rule is not valid.");
        }

        if (r.Until is not null && r.Count is not null)
        {
            throw new DomainException("calendar.recurrence_invalid", "Choose either an end date or a number of occurrences, not both.");
        }

        if (r.Count is < 1 or > MaxCount || (r.Until is { } u && (u < start || u.Year > 2200)))
        {
            throw new DomainException("calendar.recurrence_invalid", "The end of the repeat rule is not valid.");
        }

        var days = r.Frequency == RecurrenceFrequency.Weekly
            ? (r.WeekDays == 0 ? 1 << (int)start.DayOfWeek : r.WeekDays)
            : 0;
        if (days != 0 && (days & (1 << (int)start.DayOfWeek)) == 0)
        {
            throw new DomainException("calendar.recurrence_invalid", "A weekly event must repeat on the weekday it starts on.");
        }

        return r with { WeekDays = days };
    }
}

/// <summary>Validated-on-apply input of an event (wall-clock values in <see cref="TimeZone"/>).</summary>
public sealed record EventDetails(
    string? Title, string? Description, string? Location, bool AllDay, DateOnly StartDate, TimeOnly? StartTime, DateOnly EndDate, TimeOnly? EndTime, string? TimeZone, RecurrenceRule? Recurrence);

/// <summary>
/// Expands repeat rules into start dates. Pure and deterministic: no clock, no I/O. Monthly and yearly rules skip months that do not contain
/// the day (31st, 30th of Esfand in a common year, 29 February) instead of clamping, as RFC 5545 does.
/// </summary>
public static class RecurrenceExpander
{
    /// <summary>Upper bound of examined candidates, so a pathological rule can never loop for long.</summary>
    public const int MaxCandidates = 60000;

    private static readonly PersianCalendar Persian = new();

    /// <summary>Start dates of the series that fall in [<paramref name="from"/>, <paramref name="to"/>] (cancellations are not applied).</summary>
    public static IEnumerable<DateOnly> StartDates(DateOnly start, RecurrenceRule rule, DateOnly from, DateOnly to)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (!rule.IsRecurring)
        {
            if (start >= from && start <= to)
            {
                yield return start;
            }

            yield break;
        }

        var emitted = 0;
        foreach (var d in Candidates(start, rule))
        {
            if (d > to || (rule.Until is { } until && d > until))
            {
                yield break;
            }

            emitted++;
            if (rule.Count is { } count && emitted > count)
            {
                yield break;
            }

            if (d >= from)
            {
                yield return d;
            }
        }
    }

    public static bool Occurs(DateOnly start, RecurrenceRule rule, DateOnly date) =>
        date >= start && StartDates(start, rule, date, date).Any();

    private static IEnumerable<DateOnly> Candidates(DateOnly start, RecurrenceRule rule)
    {
        var steps = 0;
        switch (rule.Frequency)
        {
            case RecurrenceFrequency.Daily:
                for (var d = start; steps++ < MaxCandidates; d = d.AddDays(rule.Interval))
                {
                    yield return d;
                }

                break;

            case RecurrenceFrequency.Weekly:
                var weekStart = rule.Calendar == RecurrenceCalendar.Persian ? DayOfWeek.Saturday : DayOfWeek.Monday;
                var begin = start.AddDays(-(((int)start.DayOfWeek - (int)weekStart + 7) % 7));
                for (var week = begin; steps < MaxCandidates; week = week.AddDays(7 * rule.Interval))
                {
                    for (var i = 0; i < 7; i++, steps++)
                    {
                        var day = week.AddDays(i);
                        if (day >= start && (rule.WeekDays & (1 << (int)day.DayOfWeek)) != 0)
                        {
                            yield return day;
                        }
                    }
                }

                break;

            case RecurrenceFrequency.Monthly:
            case RecurrenceFrequency.Yearly:
                var (y, m, dom) = Parts(start, rule.Calendar);
                var monthStep = rule.Frequency == RecurrenceFrequency.Monthly ? rule.Interval : rule.Interval * 12;
                for (var k = 0; k < MaxCandidates; k++)
                {
                    var total = (y * 12) + (m - 1) + (k * monthStep);
                    var yy = total / 12;
                    var mm = (total % 12) + 1;
                    if (yy > 2300)
                    {
                        yield break;
                    }

                    if (dom <= DaysInMonth(yy, mm, rule.Calendar) && TryDate(yy, mm, dom, rule.Calendar, out var date))
                    {
                        yield return date;
                    }
                }

                break;
        }
    }

    private static (int Year, int Month, int Day) Parts(DateOnly d, RecurrenceCalendar cal)
    {
        if (cal == RecurrenceCalendar.Persian)
        {
            var dt = d.ToDateTime(TimeOnly.MinValue);
            return (Persian.GetYear(dt), Persian.GetMonth(dt), Persian.GetDayOfMonth(dt));
        }

        return (d.Year, d.Month, d.Day);
    }

    private static int DaysInMonth(int year, int month, RecurrenceCalendar cal) =>
        cal == RecurrenceCalendar.Persian ? Persian.GetDaysInMonth(year, month) : DateTime.DaysInMonth(Math.Min(year, 9999), month);

    private static bool TryDate(int year, int month, int day, RecurrenceCalendar cal, out DateOnly date)
    {
        date = default;
        try
        {
            date = cal == RecurrenceCalendar.Persian
                ? DateOnly.FromDateTime(Persian.ToDateTime(year, month, day, 0, 0, 0, 0))
                : new DateOnly(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }
}
