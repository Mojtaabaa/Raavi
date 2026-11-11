using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Raavi.Domain.Common;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Application;
using Raavi.Wiki.Domain;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 9b: the recurrence engine (Gregorian and Persian), time-zone handling and iCalendar output — no database needed.</summary>
public sealed class CalendarRecurrenceTests
{
    private static readonly PersianCalendar Fa = new();

    private static DateOnly D(string s) => DateOnly.ParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string[] Starts(string start, RecurrenceRule rule, string from, string to) =>
        RecurrenceExpander.StartDates(D(start), rule, D(from), D(to)).Select(d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)).ToArray();

    private static RecurrenceRule Rule(RecurrenceFrequency f, int interval = 1, int days = 0, RecurrenceCalendar cal = RecurrenceCalendar.Gregorian, string? until = null, int? count = null) =>
        new(f, interval, days, cal, until is null ? null : D(until), count);

    private static (int Y, int M, int Day) Persian(DateOnly d)
    {
        var dt = d.ToDateTime(TimeOnly.MinValue);
        return (Fa.GetYear(dt), Fa.GetMonth(dt), Fa.GetDayOfMonth(dt));
    }

    [Fact]
    public void Daily_rules_honour_interval_count_until_and_the_window()
    {
        Assert.Equal(["2026-01-01", "2026-01-03", "2026-01-05"], Starts("2026-01-01", Rule(RecurrenceFrequency.Daily, 2, count: 3), "2025-01-01", "2030-01-01"));
        Assert.Equal(["2026-01-05", "2026-01-07"], Starts("2026-01-01", Rule(RecurrenceFrequency.Daily, 2, until: "2026-01-08"), "2026-01-04", "2030-01-01"));
        Assert.Equal(["2026-02-01", "2026-02-02"], Starts("2026-01-01", Rule(RecurrenceFrequency.Daily), "2026-02-01", "2026-02-02")); // window only
        Assert.Single(Starts("2026-01-01", RecurrenceRule.None, "2025-12-01", "2026-01-31"));
        Assert.Empty(Starts("2026-01-01", RecurrenceRule.None, "2026-02-01", "2026-02-28"));
    }

    [Fact]
    public void Weekly_rules_use_the_chosen_weekdays_and_skip_alternate_weeks()
    {
        // 2026-01-05 is a Monday. Mondays and Wednesdays, every second week: 5th, 7th, 19th, 21st.
        var mondayWednesday = (1 << (int)DayOfWeek.Monday) | (1 << (int)DayOfWeek.Wednesday);
        Assert.Equal(["2026-01-05", "2026-01-07", "2026-01-19", "2026-01-21"], Starts("2026-01-05", Rule(RecurrenceFrequency.Weekly, 2, mondayWednesday), "2026-01-01", "2026-01-31"));

        // Occurrences never precede the start, even if the start's own weekday is not in the set earlier in that week.
        var tuesday = 1 << (int)DayOfWeek.Tuesday;
        Assert.Equal(["2026-01-07", "2026-01-13"], Starts("2026-01-07", Rule(RecurrenceFrequency.Weekly, 1, tuesday | (1 << (int)DayOfWeek.Wednesday), count: 2), "2026-01-01", "2026-01-31"));
        Assert.DoesNotContain("2026-01-06", Starts("2026-01-07", Rule(RecurrenceFrequency.Weekly, 1, tuesday), "2026-01-01", "2026-01-31"));
    }

    [Fact]
    public void Gregorian_monthly_and_yearly_rules_skip_months_without_the_day_instead_of_clamping()
    {
        Assert.Equal(["2026-01-31", "2026-03-31", "2026-05-31", "2026-07-31", "2026-08-31"], Starts("2026-01-31", Rule(RecurrenceFrequency.Monthly), "2026-01-01", "2026-08-31"));
        Assert.Equal(["2024-02-29", "2028-02-29"], Starts("2024-02-29", Rule(RecurrenceFrequency.Yearly), "2024-01-01", "2029-01-01"));
        Assert.Equal(["2026-01-15", "2026-04-15", "2026-07-15"], Starts("2026-01-15", Rule(RecurrenceFrequency.Monthly, 3), "2026-01-01", "2026-08-01"));
    }

    [Fact]
    public void Persian_monthly_rules_follow_the_persian_months()
    {
        // 1 Farvardin 1405 = 2026-03-21: the 1st of every Persian month, i.e. on different Gregorian days.
        var first = D("2026-03-21");
        Assert.Equal((1405, 1, 1), Persian(first));
        var days = RecurrenceExpander.StartDates(first, Rule(RecurrenceFrequency.Monthly, cal: RecurrenceCalendar.Persian), first, D("2027-03-01")).ToList();
        Assert.Equal(12, days.Count);
        Assert.All(days, d => Assert.Equal(1, Persian(d).Day));
        Assert.Equal(Enumerable.Range(1, 12), days.Select(d => Persian(d).M));
        Assert.Equal("2026-04-21", days[1].ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)); // 1 Ordibehesht

        // The 31st exists only in the first six months: seven occurrences per year are impossible, six in a row then a gap to next Farvardin.
        var thirtyFirst = first.AddDays(30);
        Assert.Equal((1405, 1, 31), Persian(thirtyFirst));
        var list = RecurrenceExpander.StartDates(thirtyFirst, Rule(RecurrenceFrequency.Monthly, cal: RecurrenceCalendar.Persian), thirtyFirst, D("2027-05-30")).Select(Persian).ToList();
        Assert.Equal([(1405, 1, 31), (1405, 2, 31), (1405, 3, 31), (1405, 4, 31), (1405, 5, 31), (1405, 6, 31), (1406, 1, 31), (1406, 2, 31)], list);
    }

    [Fact]
    public void Persian_yearly_rules_skip_years_without_the_30th_of_esfand()
    {
        var start = DateOnly.FromDateTime(Fa.ToDateTime(1403, 12, 30, 0, 0, 0, 0));
        Assert.True(Fa.IsLeapYear(1403));
        var years = RecurrenceExpander.StartDates(start, Rule(RecurrenceFrequency.Yearly, cal: RecurrenceCalendar.Persian), start, D("2040-01-01")).Select(Persian).ToList();
        Assert.All(years, p => Assert.True(p.M == 12 && p.Day == 30 && Fa.IsLeapYear(p.Y)));
        Assert.True(years.Count >= 3);
        Assert.True(years[1].Y - years[0].Y >= 4); // never in a common year
    }

    [Fact]
    public void An_occurrence_can_be_tested_and_pathological_rules_terminate()
    {
        var rule = Rule(RecurrenceFrequency.Weekly, 1, 1 << (int)DayOfWeek.Monday);
        Assert.True(RecurrenceExpander.Occurs(D("2026-01-05"), rule, D("2026-01-12")));
        Assert.False(RecurrenceExpander.Occurs(D("2026-01-05"), rule, D("2026-01-13")));
        Assert.False(RecurrenceExpander.Occurs(D("2026-01-05"), rule, D("2025-12-29")));

        // A window far beyond the cap returns without hanging.
        Assert.Empty(Starts("2026-01-01", Rule(RecurrenceFrequency.Daily, count: 5), "2199-01-01", "2199-12-31"));
    }

    private static EventDetails Details(bool allDay = false, string start = "2026-07-01", string end = "2026-07-01", string? startTime = "09:00", string? endTime = "10:00", string? zone = "UTC", RecurrenceRule? rule = null) =>
        new("Standup", null, null, allDay, D(start), allDay ? null : TimeOnly.Parse(startTime!, CultureInfo.InvariantCulture), D(end), allDay ? null : TimeOnly.Parse(endTime!, CultureInfo.InvariantCulture), zone, rule);

    [Fact]
    public void Events_are_validated()
    {
        static string Code(Action a) => Assert.Throws<DomainException>(a).Code;
        var now = DateTimeOffset.UtcNow;
        Assert.Equal("calendar.title_invalid", Code(() => CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details() with { Title = "  " }, Guid.NewGuid(), now)));
        Assert.Equal("calendar.range_invalid", Code(() => CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details(end: "2026-06-30"), Guid.NewGuid(), now)));
        Assert.Equal("calendar.range_invalid", Code(() => CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details(startTime: "11:00", endTime: "10:00"), Guid.NewGuid(), now)));
        Assert.Equal("calendar.range_invalid", Code(() => CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details() with { StartTime = null }, Guid.NewGuid(), now)));
        Assert.Equal("calendar.range_too_long", Code(() => CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details(allDay: true, end: "2028-01-01"), Guid.NewGuid(), now)));
        Assert.Equal("calendar.time_zone_invalid", Code(() => CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details(zone: "Mars/Olympus"), Guid.NewGuid(), now)));
        Assert.Equal("calendar.recurrence_invalid", Code(() => CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details(rule: Rule(RecurrenceFrequency.Daily, until: "2026-08-01", count: 3)), Guid.NewGuid(), now)));
        Assert.Equal("calendar.recurrence_invalid", Code(() => CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details(rule: Rule(RecurrenceFrequency.Daily, 0)), Guid.NewGuid(), now)));
        Assert.Equal("calendar.recurrence_invalid", Code(() => CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details(rule: Rule(RecurrenceFrequency.Daily, until: "2026-06-01")), Guid.NewGuid(), now)));

        // A weekly rule must include the weekday of its start (2026-07-01 is a Wednesday), as iCalendar always treats the start as an occurrence.
        Assert.Equal("calendar.recurrence_invalid", Code(() => CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details(rule: Rule(RecurrenceFrequency.Weekly, 1, 1 << (int)DayOfWeek.Saturday)), Guid.NewGuid(), now)));

        // A weekly rule without weekdays repeats on the weekday of the start.
        var weekly = CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details(rule: Rule(RecurrenceFrequency.Weekly)), Guid.NewGuid(), now);
        Assert.Equal(1 << (int)DayOfWeek.Wednesday, weekly.RecurWeekDays); // 2026-07-01 is a Wednesday
    }

    [Fact]
    public void Cancelling_occurrences_and_changing_the_series_keep_exclusions_consistent()
    {
        var now = DateTimeOffset.UtcNow;
        var e = CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details(rule: Rule(RecurrenceFrequency.Daily)), Guid.NewGuid(), now);
        e.Exclude(D("2026-07-03"), now);
        e.Exclude(D("2026-07-03"), now); // idempotent
        e.Exclude(D("2026-07-06"), now);
        Assert.Equal(2, e.ExcludedDates.Count);
        Assert.Equal(3, e.Revision);
        Assert.Equal("calendar.occurrence_invalid", Assert.Throws<DomainException>(() => e.Exclude(D("2026-06-30"), now)).Code);

        // Moving to every second day (1st, 3rd, 5th…): the cancelled 3rd stays valid, the cancelled 6th is no longer an occurrence and is dropped.
        e.Update(Details(rule: Rule(RecurrenceFrequency.Daily, 2)), Guid.NewGuid(), now);
        Assert.Equal([D("2026-07-03")], e.ExcludedDates.ToArray());
        var single = CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details(), Guid.NewGuid(), now);
        Assert.Equal("calendar.occurrence_invalid", Assert.Throws<DomainException>(() => single.Exclude(D("2026-07-01"), now)).Code);
    }

    [Fact]
    public void Wall_clock_times_convert_to_utc_across_daylight_saving_changes()
    {
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T07:00:00Z", CultureInfo.InvariantCulture), CalendarService.ToUtc(D("2026-07-01"), new TimeOnly(9, 0), "Europe/Berlin"));
        Assert.Equal(DateTimeOffset.Parse("2026-01-15T08:00:00Z", CultureInfo.InvariantCulture), CalendarService.ToUtc(D("2026-01-15"), new TimeOnly(9, 0), "Europe/Berlin"));
        Assert.Equal(DateTimeOffset.Parse("2026-03-29T01:30:00Z", CultureInfo.InvariantCulture), CalendarService.ToUtc(D("2026-03-29"), new TimeOnly(2, 30), "Europe/Berlin")); // skipped hour → 03:30 CEST
        Assert.Equal(DateTimeOffset.Parse("2026-07-01T05:30:00Z", CultureInfo.InvariantCulture), CalendarService.ToUtc(D("2026-07-01"), new TimeOnly(9, 0), "Asia/Tehran"));
    }

    [Fact]
    public void Ics_output_is_escaped_folded_and_expresses_persian_rules_as_explicit_dates()
    {
        var now = DateTimeOffset.Parse("2026-06-01T00:00:00Z", CultureInfo.InvariantCulture);
        var weekly = CalendarEvent.Create(
            Guid.NewGuid(),
            Guid.NewGuid(),
            Details(start: "2026-07-04", end: "2026-07-04", zone: "Asia/Tehran", rule: Rule(RecurrenceFrequency.Weekly, 1, 1 << (int)DayOfWeek.Saturday, RecurrenceCalendar.Persian, count: 4)) with { Title = "Plan; review, \"Q3\"", Description = "line1\nline2 \\ end", Location = new string('ن', 80) },
            Guid.NewGuid(),
            now);
        weekly.Exclude(D("2026-07-11"), now);
        var monthly = CalendarEvent.Create(Guid.NewGuid(), Guid.NewGuid(), Details(allDay: true, start: "2026-03-21", end: "2026-03-21", rule: Rule(RecurrenceFrequency.Monthly, cal: RecurrenceCalendar.Persian)), Guid.NewGuid(), now);

        var ics = IcsWriter.Write([weekly, monthly], "Team; calendar", now);
        Assert.StartsWith("BEGIN:VCALENDAR\r\nVERSION:2.0\r\n", ics, StringComparison.Ordinal);
        Assert.EndsWith("END:VCALENDAR\r\n", ics, StringComparison.Ordinal);
        Assert.Contains("X-WR-CALNAME:Team\\; calendar", ics, StringComparison.Ordinal);
        Assert.Contains("SUMMARY:Plan\\; review\\, \"Q3\"", ics, StringComparison.Ordinal);
        Assert.Contains("DESCRIPTION:line1\\nline2 \\\\ end", ics, StringComparison.Ordinal);
        Assert.Contains("DTSTART;TZID=Asia/Tehran:20260704T090000", ics, StringComparison.Ordinal);
        Assert.Contains("RRULE:FREQ=WEEKLY;INTERVAL=1;BYDAY=SA;WKST=SA;COUNT=4", ics, StringComparison.Ordinal);
        Assert.Contains("EXDATE;TZID=Asia/Tehran:20260711T090000", ics, StringComparison.Ordinal);

        // A "monthly in the Persian calendar" rule cannot be an RRULE: it is written as explicit dates.
        Assert.Contains("DTSTART;VALUE=DATE:20260321", ics, StringComparison.Ordinal);
        Assert.Contains("DTEND;VALUE=DATE:20260322", ics, StringComparison.Ordinal);
        Assert.Contains("RDATE;VALUE=DATE:20260421,", ics, StringComparison.Ordinal);
        Assert.DoesNotContain("FREQ=MONTHLY", ics, StringComparison.Ordinal);

        // Every physical line is at most 75 octets and continuation lines start with one space; no UTF-8 sequence is cut.
        foreach (var line in ics.Split("\r\n"))
        {
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line);
        }

        Assert.Equal(ics, Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(ics)));
        Assert.Contains("\r\n ", ics, StringComparison.Ordinal);
    }
}

/// <summary>Phase 9b acceptance: calendars have persistence, tenant ownership, space-based permissions, navigation (cross-space feed) and iCalendar export.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class CalendarTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static async Task<Guid> MeAsync(Browser b) => (await b.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();

    private static async Task<System.Text.Json.JsonElement> CreateEventAsync(Browser b, string key, object body)
    {
        using var r = await b.PostAsync($"api/v1/spaces/{key}/calendar/events", body);
        return await ReadAsync(r);
    }

    private static async Task<string[]> TitlesAsync(Browser b, string url) =>
        (await b.GetJsonAsync(url)).EnumerateArray().Select(o => $"{o.GetProperty("startDate").GetString()} {o.GetProperty("title").GetString()}").ToArray();

    [Fact]
    public async Task Events_are_created_expanded_in_windows_and_individual_occurrences_can_be_cancelled()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;

        var standup = await CreateEventAsync(admin, key, new
        {
            title = "Standup", startDate = "2026-07-06", startTime = "09:30", endDate = "2026-07-06", endTime = "09:45", timeZone = "Asia/Tehran",
            recurrence = new { frequency = "Weekly", interval = 1, weekDays = new[] { 1, 3 }, calendar = "Gregorian", count = 6 },
        });
        await CreateEventAsync(admin, key, new { title = "Offsite", allDay = true, startDate = "2026-07-08", endDate = "2026-07-10", location = "Shiraz" });
        Assert.Equal(new[] { 1, 3 }, standup.GetProperty("recurrence").GetProperty("weekDays").EnumerateArray().Select(x => x.GetInt32()).ToArray());

        var window = $"api/v1/spaces/{key}/calendar/occurrences?from=2026-07-01&to=2026-07-14";
        Assert.Equal(
            ["2026-07-06 Standup", "2026-07-08 Offsite", "2026-07-08 Standup", "2026-07-13 Standup"],
            await TitlesAsync(admin, window));

        // A multi-day event shows in a window that only touches its tail; timed occurrences carry UTC instants (Tehran = +03:30 → 06:00Z).
        Assert.Equal(["2026-07-08 Offsite"], await TitlesAsync(admin, $"api/v1/spaces/{key}/calendar/occurrences?from=2026-07-10&to=2026-07-10"));
        var first = (await admin.GetJsonAsync(window)).EnumerateArray().First();
        Assert.Equal(DateTimeOffset.Parse("2026-07-06T06:00:00Z", CultureInfo.InvariantCulture), first.GetProperty("startUtc").GetDateTimeOffset());
        Assert.False(first.GetProperty("allDay").GetBoolean());

        // Cancelling one occurrence removes only that one; a date that is not an occurrence is rejected.
        var id = standup.GetProperty("id").GetGuid();
        using (var bad = await admin.PostAsync($"api/v1/calendar/events/{id}/exclusions", new { date = "2026-07-07" }))
        {
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest, "calendar.occurrence_invalid");
        }

        using (var cancel = await admin.PostAsync($"api/v1/calendar/events/{id}/exclusions", new { date = "2026-07-08" }))
        {
            Assert.Equal("2026-07-08", (await ReadAsync(cancel)).GetProperty("excludedDates")[0].GetString());
        }

        Assert.DoesNotContain("2026-07-08 Standup", await TitlesAsync(admin, window));

        // Window validation.
        using (var huge = await admin.GetAsync($"api/v1/spaces/{key}/calendar/occurrences?from=2026-01-01&to=2028-01-01"))
        {
            await Browser.AssertStatusAsync(huge, HttpStatusCode.BadRequest, "calendar.window_invalid");
        }

        using (var backwards = await admin.GetAsync($"api/v1/spaces/{key}/calendar/occurrences?from=2026-02-01&to=2026-01-01"))
        {
            await Browser.AssertStatusAsync(backwards, HttpStatusCode.BadRequest, "calendar.window_invalid");
        }

        // Request validation.
        foreach (var (body, code) in new (object, string)[]
        {
            (new { title = " ", startDate = "2026-07-01", startTime = "09:00", endTime = "10:00" }, "calendar.title_invalid"),
            (new { title = "x", startDate = "2026-07-01", startTime = "9am", endTime = "10:00" }, "calendar.time_invalid"),
            (new { title = "x", startDate = "2026-07-01", startTime = "11:00", endTime = "10:00" }, "calendar.range_invalid"),
            (new { title = "x", allDay = true, startDate = "2026-07-01", timeZone = "Nowhere/Land" }, "calendar.time_zone_invalid"),
            (new { title = "x", allDay = true, startDate = "2026-07-01", recurrence = new { frequency = "Hourly" } }, "calendar.recurrence_invalid"),
            (new { title = "x", allDay = true, startDate = "2026-07-01", recurrence = new { frequency = "Weekly", weekDays = new[] { 9 } } }, "calendar.recurrence_invalid"),
            (new { title = "x", allDay = true, startDate = "2026-07-01", recurrence = new { frequency = "Daily", count = 2, until = "2026-08-01" } }, "calendar.recurrence_invalid"),
        })
        {
            using var r = await admin.PostAsync($"api/v1/spaces/{key}/calendar/events", body);
            await Browser.AssertStatusAsync(r, HttpStatusCode.BadRequest, code);
        }
    }

    [Fact]
    public async Task Persian_recurrence_is_evaluated_in_the_persian_calendar_through_the_api()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        await CreateEventAsync(admin, key, new { title = "Month start", allDay = true, startDate = "2026-03-21", recurrence = new { frequency = "Monthly", calendar = "Persian", count = 4 } });
        Assert.Equal(
            ["2026-03-21 Month start", "2026-04-21 Month start", "2026-05-22 Month start", "2026-06-22 Month start"],
            await TitlesAsync(admin, $"api/v1/spaces/{key}/calendar/occurrences?from=2026-03-01&to=2026-08-01"));
    }

    [Fact]
    public async Task Calendar_permissions_follow_the_space_and_the_feed_spans_only_viewable_spaces()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (viewer, _, viewerId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (outsider, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var adminId = await MeAsync(admin);

        var open = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var priv = (await CreateSpaceAsync(admin, visibility: "Private")).GetProperty("key").GetString()!;
        var grants = new[] { "View", "ManagePermissions", "Edit", "Create", "Publish", "Move", "Archive", "Delete", "Comment", "ManageTemplates" }
            .Select(a => (object)new { subjectType = "User", subjectId = (Guid?)adminId, action = a, effect = "Allow" })
            .Append(new { subjectType = "User", subjectId = (Guid?)viewerId, action = "View", effect = "Allow" }).ToList();
        using (var put = await admin.PutAsync($"api/v1/spaces/{priv}/permissions", new { grants }))
        {
            await ReadAsync(put);
        }

        var secret = await CreateEventAsync(admin, priv, new { title = "Secret", allDay = true, startDate = "2026-09-01" });
        await CreateEventAsync(admin, open, new { title = "Public", allDay = true, startDate = "2026-09-02" });

        // The viewer reads but cannot write; the outsider never learns the private calendar exists, and the feed only shows what they may view.
        Assert.False((await viewer.GetJsonAsync($"api/v1/spaces/{priv}/calendar/occurrences?from=2026-09-01&to=2026-09-30")).EnumerateArray().First().GetProperty("canEdit").GetBoolean());
        using (var create = await viewer.PostAsync($"api/v1/spaces/{priv}/calendar/events", new { title = "nope", allDay = true, startDate = "2026-09-03" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, create.StatusCode);
        }

        using (var del = await viewer.DeleteAsync($"api/v1/calendar/events/{Id(secret)}"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, del.StatusCode);
        }

        using (var hidden = await outsider.GetAsync($"api/v1/spaces/{priv}/calendar/occurrences?from=2026-09-01&to=2026-09-30"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        using (var hiddenEvent = await outsider.GetAsync($"api/v1/calendar/events/{Id(secret)}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hiddenEvent.StatusCode);
        }

        using (var hiddenExport = await outsider.GetAsync($"api/v1/spaces/{priv}/calendar/export"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hiddenExport.StatusCode);
        }

        Assert.Equal(["2026-09-01 Secret", "2026-09-02 Public"], await TitlesAsync(viewer, "api/v1/calendar/occurrences?from=2026-09-01&to=2026-09-30"));
        Assert.Equal(["2026-09-02 Public"], await TitlesAsync(outsider, "api/v1/calendar/occurrences?from=2026-09-01&to=2026-09-30"));
    }

    [Fact]
    public async Task Updates_use_etags_events_can_be_deleted_exports_are_ics_and_other_tenants_see_nothing()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var ev = await CreateEventAsync(admin, key, new { title = "Review", startDate = "2026-10-01", startTime = "14:00", endDate = "2026-10-01", endTime = "15:00", timeZone = "UTC", description = "Agenda; items, here" });

        var (current, etag) = await GetWithETagAsync(admin, $"api/v1/calendar/events/{Id(ev)}");
        var edited = new { title = "Review v2", startDate = "2026-10-02", startTime = "14:00", endDate = "2026-10-02", endTime = "15:30", timeZone = "UTC" };
        using (var missing = await admin.SendAsync(HttpMethod.Put, $"api/v1/calendar/events/{Id(current)}", edited, withXsrf: true))
        {
            await Browser.AssertStatusAsync(missing, HttpStatusCode.PreconditionRequired);
        }

        using (var ok = await admin.SendAsync(HttpMethod.Put, $"api/v1/calendar/events/{Id(current)}", edited, withXsrf: true, ifMatch: etag))
        {
            var updated = await ReadAsync(ok);
            Assert.Equal(("Review v2", "2026-10-02", "15:30"), (updated.GetProperty("title").GetString(), updated.GetProperty("startDate").GetString(), updated.GetProperty("endTime").GetString()));
        }

        using (var stale = await admin.SendAsync(HttpMethod.Put, $"api/v1/calendar/events/{Id(current)}", edited, withXsrf: true, ifMatch: etag))
        {
            await Browser.AssertStatusAsync(stale, HttpStatusCode.PreconditionFailed);
        }

        using (var export = await admin.GetAsync($"api/v1/spaces/{key}/calendar/export"))
        {
            Assert.Equal(HttpStatusCode.OK, export.StatusCode);
            Assert.Equal("text/calendar", export.Content.Headers.ContentType!.MediaType);
            Assert.Contains($"calendar-{key}.ics", export.Content.Headers.ContentDisposition!.ToString(), StringComparison.Ordinal);
            var ics = await export.Content.ReadAsStringAsync();
            Assert.Contains("SUMMARY:Review v2", ics, StringComparison.Ordinal);
            Assert.Contains("DTSTART;TZID=UTC:20261002T140000", ics, StringComparison.Ordinal);
        }

        var (_, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        using (var peek = await otherAdmin.GetAsync($"api/v1/calendar/events/{Id(ev)}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, peek.StatusCode);
        }

        Assert.Empty((await otherAdmin.GetJsonAsync("api/v1/calendar/occurrences?from=2026-10-01&to=2026-10-31")).EnumerateArray());

        using (var del = await admin.DeleteAsync($"api/v1/calendar/events/{Id(ev)}"))
        {
            await Browser.AssertStatusAsync(del, HttpStatusCode.NoContent);
        }

        Assert.Empty((await admin.GetJsonAsync($"api/v1/spaces/{key}/calendar/occurrences?from=2026-10-01&to=2026-10-31")).EnumerateArray());
    }

    [Fact]
    public async Task Space_events_land_on_a_default_calendar_that_archives_restores_and_hard_deletes_with_confirm()
    {
        var (tenant, owner, _) = await _kit.Identity.TenantWithAdminAsync();
        var (viewer, _, _) = await _kit.Identity.AddMemberAsync(tenant, owner);
        var key = (await CreateSpaceAsync(owner)).GetProperty("key").GetString()!;
        await CreateEventAsync(owner, key, new { title = "Keep", allDay = true, startDate = "2026-11-01" });

        var calendars = (await owner.GetJsonAsync($"api/v1/spaces/{key}/calendars")).EnumerateArray().ToList();
        Assert.Single(calendars);
        Assert.True(calendars[0].GetProperty("isDefault").GetBoolean());
        Assert.Equal("Space", calendars[0].GetProperty("scope").GetString());
        var calendarId = calendars[0].GetProperty("id").GetGuid();

        var personal = await ReadAsync(await owner.PostAsync("api/v1/calendars", new { scope = "Personal", name = "Mine", color = "#10b981" }));
        Assert.Equal("Personal", personal.GetProperty("scope").GetString());

        using (var forbidden = await viewer.PostAsync("api/v1/calendars", new { scope = "Organization", name = "Org holidays" }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, forbidden.StatusCode);
        }

        var org = await ReadAsync(await owner.PostAsync("api/v1/calendars", new { scope = "Organization", name = "Org holidays", color = "#8b5cf6" }));
        Assert.Equal("Organization", org.GetProperty("scope").GetString());

        using (var archive = await owner.PostAsync($"api/v1/calendars/{calendarId}/archive", new { }))
        {
            Assert.NotNull((await ReadAsync(archive)).GetProperty("archivedAt").GetString());
        }

        Assert.Empty((await owner.GetJsonAsync($"api/v1/spaces/{key}/calendars")).EnumerateArray());
        Assert.Single((await owner.GetJsonAsync($"api/v1/spaces/{key}/calendars?includeArchived=true")).EnumerateArray());

        using (var restore = await owner.PostAsync($"api/v1/calendars/{calendarId}/restore", new { }))
        {
            Assert.Equal(JsonValueKind.Null, (await ReadAsync(restore)).GetProperty("archivedAt").ValueKind);
        }

        Assert.Equal(["2026-11-01 Keep"], await TitlesAsync(owner, $"api/v1/spaces/{key}/calendar/occurrences?from=2026-11-01&to=2026-11-01&calendarIds={calendarId}"));

        using (var archiveAgain = await owner.PostAsync($"api/v1/calendars/{calendarId}/archive", new { }))
        {
            await ReadAsync(archiveAgain);
        }

        using (var badName = await owner.PostAsync($"api/v1/calendars/{calendarId}/purge", new { confirmName = "wrong" }))
        {
            await Browser.AssertStatusAsync(badName, HttpStatusCode.BadRequest, "calendar.confirm_mismatch");
        }

        var spaceCalName = calendars[0].GetProperty("name").GetString()!;
        using (var purge = await owner.PostAsync($"api/v1/calendars/{calendarId}/purge", new { confirmName = spaceCalName }))
        {
            await Browser.AssertStatusAsync(purge, HttpStatusCode.NoContent);
        }

        Assert.Empty((await owner.GetJsonAsync($"api/v1/spaces/{key}/calendars?includeArchived=true")).EnumerateArray());
        Assert.Empty((await owner.GetJsonAsync($"api/v1/spaces/{key}/calendar/occurrences?from=2026-11-01&to=2026-11-01")).EnumerateArray());

        // Creating another event recreates the default space calendar (no data loss path for new writes).
        await CreateEventAsync(owner, key, new { title = "Again", allDay = true, startDate = "2026-11-02" });
        Assert.Single((await owner.GetJsonAsync($"api/v1/spaces/{key}/calendars")).EnumerateArray());
        Assert.Equal(["2026-11-02 Again"], await TitlesAsync(owner, $"api/v1/spaces/{key}/calendar/occurrences?from=2026-11-01&to=2026-11-30"));
    }
}
