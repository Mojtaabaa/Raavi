using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Calendar;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;
using WikiCalendar = Raavi.Wiki.Domain.Calendar;

namespace Raavi.Wiki.Application;

public sealed record RecurrenceDto(string Frequency, int Interval, int[] WeekDays, string Calendar, string? Until, int? Count, int MonthWeek = 0);

public sealed record CalendarAttendeeDto(Guid UserId, string? DisplayName, string Response, bool Optional, DateTimeOffset InvitedAt, DateTimeOffset? RespondedAt);

public sealed record CalendarEventDto(
    Guid Id, Guid CalendarId, string? SpaceKey, string? SpaceName, string Title, string? Description, string? Location, bool AllDay, string StartDate, string? StartTime, string EndDate, string? EndTime,
    string TimeZone, RecurrenceDto? Recurrence, string[] ExcludedDates, string Type, string Status, string Priority, string? Color, string? OnlineUrl, bool IsPrivate,
    string? CreatedByName, DateTimeOffset CreatedAt, int Revision, bool CanEdit, IReadOnlyList<CalendarReminderDto> Reminders,
    IReadOnlyList<CalendarAttendeeDto> Attendees, Guid? LinkedPageId, Guid? LinkedWhiteboardId, string? MyResponse);

/// <summary>One concrete occurrence in a requested window. <see cref="OccurrenceDate"/> is the series start date it came from (used to cancel it).</summary>
public sealed record OccurrenceDto(
    Guid? EventId, Guid? CalendarId, string? SpaceKey, string? SpaceName, string Title, string? Location, bool AllDay, string StartDate, string? StartTime, string EndDate, string? EndTime,
    string TimeZone, DateTimeOffset? StartUtc, DateTimeOffset? EndUtc, bool IsRecurring, string OccurrenceDate, bool CanEdit,
    string SourceKind, string? ExternalUrl, bool FreeBusyOnly, string? Type, string? Color);

public sealed record RecurrenceRequest(string? Frequency, int? Interval, int[]? WeekDays, string? Calendar, DateOnly? Until, int? Count, int? MonthWeek = null);

public sealed record CalendarReminderDto(Guid Id, int MinutesBefore, bool InApp, bool Email);

public sealed record CalendarReminderInput(int MinutesBefore, bool InApp, bool Email);

public sealed record CalendarAttendeeInput(Guid? UserId, bool? Optional);

public sealed record CalendarRsvpRequest(string? Response);

public sealed record CalendarEventRequest(
    string? Title, string? Description, string? Location, bool AllDay, DateOnly StartDate, string? StartTime, DateOnly? EndDate, string? EndTime, string? TimeZone, RecurrenceRequest? Recurrence,
    string? Type = null, string? Status = null, string? Priority = null, string? Color = null, string? OnlineUrl = null, bool? IsPrivate = null,
    string? EditScope = null, DateOnly? OccurrenceDate = null, IReadOnlyList<CalendarReminderInput>? Reminders = null,
    IReadOnlyList<CalendarAttendeeInput>? Attendees = null, Guid? LinkedPageId = null, Guid? LinkedWhiteboardId = null);

public sealed record CalendarGrantDto(Guid Id, string SubjectType, Guid SubjectId, string Grant, DateTimeOffset CreatedAt);

public sealed record CalendarGrantRequest(string? SubjectType, Guid? SubjectId, string? Grant);

public sealed record CalendarUserPrefsDto(
    bool BrowserAlerts, bool TtsEnabled, string TtsLocale, double TtsRate, bool HidePrivateTitlesInTts, int? QuietStartMinutes, int? QuietEndMinutes, string TimeZone);

public sealed record CalendarUserPrefsRequest(
    bool? BrowserAlerts, bool? TtsEnabled, string? TtsLocale, double? TtsRate, bool? HidePrivateTitlesInTts, int? QuietStartMinutes, int? QuietEndMinutes, string? TimeZone);

public sealed record CalendarExport(string FileName, byte[] Data);

public sealed record CalendarDto(
    Guid Id, string Scope, Guid? SpaceId, Guid? OwnerUserId, string Name, string? Description, string Color, string? Icon, bool IsDefault, DateTimeOffset? ArchivedAt, int Revision);

public sealed record CalendarRequest(string? Scope, string? SpaceKey, string? Name, string? Description, string? Color, string? Icon);

public sealed record CalendarHardDeleteRequest(string? ConfirmName);

public sealed record CalendarImportRequest(string? Ics);

public sealed record CalendarImportResult(int Imported, int Skipped, string[] Warnings);

public sealed record CalendarMoveRequest(Guid? TargetCalendarId);

/// <summary>Calendars + space events (ADR-027 extended): wall-clock recurrence, calendar containers, iCalendar export.</summary>
public sealed class CalendarService(
    WikiDbContext db,
    SpaceService spaces,
    WikiAuthorizer authz,
    IPrincipalDirectory directory,
    ICurrentUser current,
    IClock clock,
    IEnumerable<ICalendarOverlaySource> overlays,
    ITemplarCalendarProvider templar)
{
    public const int MaxWindowDays = 400;
    public const int MaxOccurrences = 2000;
    public const int ExportHorizonYears = 2;

    public static readonly Error NotFound = new("calendar.event_not_found", "Event not found.", ErrorKind.NotFound);
    public static readonly Error CalendarNotFound = new("calendar.not_found", "Calendar not found.", ErrorKind.NotFound);
    public static readonly Error WindowInvalid = new("calendar.window_invalid", $"Choose a window of at most {MaxWindowDays} days.", ErrorKind.Validation);
    public static readonly Error TimeInvalid = new("calendar.time_invalid", "Times must look like 09:30.", ErrorKind.Validation);
    public static readonly Error RecurrenceInvalid = new("calendar.recurrence_invalid", "The repeat rule is not valid.", ErrorKind.Validation);
    public static readonly Error ConfirmMismatch = new("calendar.confirm_mismatch", "Type the calendar name to permanently delete it.", ErrorKind.Validation);
    public static readonly Error NotArchived = new("calendar.not_archived", "Archive the calendar before permanently deleting it.", ErrorKind.Conflict);
    public static readonly Error ScopeInvalid = new("calendar.edit_scope_invalid", "Choose ThisOccurrence, ThisAndFollowing or EntireSeries.", ErrorKind.Validation);
    public static readonly Error OccurrenceRequired = new("calendar.occurrence_required", "occurrenceDate is required for this edit scope.", ErrorKind.Validation);
    public static readonly Error ImportEmpty = new("calendar.import_empty", "Paste or upload an iCalendar (.ics) file.", ErrorKind.Validation);
    public static readonly Error ImportTooLarge = new("calendar.import_too_large", "The iCalendar file is too large (max 1 MB).", ErrorKind.Validation);
    public const int MaxImportChars = 1_000_000;
    public const int MaxImportEvents = 200;

    private static readonly string[] TimeFormats = ["HH:mm", "HH:mm:ss"];

    /// <summary>Occurrences in [from, to] for one space, or for every accessible calendar when <paramref name="spaceKey"/> is null.</summary>
    public async Task<Result<IReadOnlyList<OccurrenceDto>>> OccurrencesAsync(string? spaceKey, DateOnly from, DateOnly to, CancellationToken ct, IReadOnlyList<Guid>? calendarIds = null, string? query = null, string? type = null, string? sourceKind = null)
    {
        if (to < from || to.DayNumber - from.DayNumber > MaxWindowDays)
        {
            return Result<IReadOnlyList<OccurrenceDto>>.Failure(WindowInvalid);
        }

        var q = string.IsNullOrWhiteSpace(query) ? null : query.Trim();
        if (q is { Length: > 200 })
        {
            q = q[..200];
        }

        var typeFilter = string.IsNullOrWhiteSpace(type) ? null : type.Trim();
        var sourceFilter = string.IsNullOrWhiteSpace(sourceKind) ? null : sourceKind.Trim();

        var access = await ResolveAccessibleCalendarsAsync(spaceKey, ct).ConfigureAwait(false);
        if (!access.IsSuccess)
        {
            return Result<IReadOnlyList<OccurrenceDto>>.Failure(access.Error!);
        }

        var accessible = access.Value;
        if (calendarIds is { Count: > 0 })
        {
            var filter = calendarIds.Distinct().ToHashSet();
            accessible = accessible.Where(a => filter.Contains(a.Calendar.Id)).ToList();
        }

        var calendarMap = accessible.ToDictionary(a => a.Calendar.Id);
        var allowedIds = calendarMap.Keys.ToList();
        var events = allowedIds.Count == 0
            ? []
            : await db.CalendarEvents.AsNoTracking()
                .Where(e => allowedIds.Contains(e.CalendarId) && e.StartDate <= to && (e.RecurFrequency != RecurrenceFrequency.None || e.EndDate >= from) && (e.RecurUntil == null || e.RecurUntil >= from))
                .ToListAsync(ct).ConfigureAwait(false);

        var list = new List<OccurrenceDto>();
        foreach (var e in events)
        {
            var a = calendarMap[e.CalendarId];
            // Private events: owner sees details; FreeBusy sees a busy slot; anyone with less than FreeBusy sees nothing.
            if (e.IsPrivate && a.Calendar.OwnerUserId != current.UserId && a.Grant < CalendarGrant.View && a.Grant != CalendarGrant.FreeBusy)
            {
                continue;
            }

            var freeBusyOnly = a.Grant == CalendarGrant.FreeBusy && a.Calendar.OwnerUserId != current.UserId;
            var canEdit = CallerCanEdit(a.Grant, e.CreatedBy);
            var excluded = e.ExcludedDates;
            foreach (var start in RecurrenceExpander.StartDates(e.StartDate, e.Rule, from.AddDays(-e.SpanDays), to))
            {
                if (excluded.Contains(start))
                {
                    continue;
                }

                list.Add(ToOccurrence(e, a, start, canEdit, freeBusyOnly));
                if (list.Count >= MaxOccurrences)
                {
                    break;
                }
            }

            if (list.Count >= MaxOccurrences)
            {
                break;
            }
        }

        foreach (var source in overlays)
        {
            foreach (var o in await source.GetOccurrencesAsync(from, to, ct).ConfigureAwait(false))
            {
                list.Add(FromOverlay(o));
                if (list.Count >= MaxOccurrences)
                {
                    break;
                }
            }

            if (list.Count >= MaxOccurrences)
            {
                break;
            }
        }

        if (templar.IsConfigured)
        {
            foreach (var o in await templar.GetOccurrencesAsync(from, to, ct).ConfigureAwait(false))
            {
                list.Add(FromOverlay(o));
                if (list.Count >= MaxOccurrences)
                {
                    break;
                }
            }
        }

        IEnumerable<OccurrenceDto> filtered = list;
        if (q is not null)
        {
            filtered = filtered.Where(o =>
                (!string.IsNullOrEmpty(o.Title) && o.Title.Contains(q, StringComparison.OrdinalIgnoreCase))
                || (o.Location?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (o.SpaceName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)
                || (o.SourceKind?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false));
        }

        if (typeFilter is not null)
        {
            filtered = filtered.Where(o => string.Equals(o.Type, typeFilter, StringComparison.OrdinalIgnoreCase));
        }

        if (sourceFilter is not null)
        {
            filtered = filtered.Where(o => string.Equals(o.SourceKind, sourceFilter, StringComparison.OrdinalIgnoreCase));
        }

        IReadOnlyList<OccurrenceDto> ordered = filtered.OrderBy(o => o.StartDate, StringComparer.Ordinal).ThenBy(o => o.StartTime ?? string.Empty, StringComparer.Ordinal).ThenBy(o => o.Title, StringComparer.Ordinal).Take(MaxOccurrences).ToList();
        return Result<IReadOnlyList<OccurrenceDto>>.Success(ordered);
    }

    /// <summary>Imports VEVENT entries from an .ics payload onto a writable calendar (sanitized; max <see cref="MaxImportEvents"/>).</summary>
    public async Task<Result<CalendarImportResult>> ImportAsync(Guid calendarId, CalendarImportRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var ics = request.Ics ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ics))
        {
            return Result<CalendarImportResult>.Failure(ImportEmpty);
        }

        if (ics.Length > MaxImportChars)
        {
            return Result<CalendarImportResult>.Failure(ImportTooLarge);
        }

        var cal = await db.Calendars.FirstOrDefaultAsync(c => c.Id == calendarId && c.ArchivedAt == null, ct).ConfigureAwait(false);
        if (cal is null)
        {
            return Result<CalendarImportResult>.Failure(CalendarNotFound);
        }

        var grant = await EffectiveGrantAsync(cal, ct).ConfigureAwait(false);
        if (grant is null || grant < CalendarGrant.Create)
        {
            return Result<CalendarImportResult>.Failure(CalendarNotFound);
        }

        var parsed = IcsReader.Parse(ics);
        var imported = 0;
        var skipped = 0;
        var warnings = new List<string>();
        foreach (var draft in parsed.Take(MaxImportEvents))
        {
            try
            {
                var details = draft.ToDetails();
                var ev = CalendarEvent.Create(cal.Id, cal.SpaceId, details, current.UserId, clock.UtcNow);
                db.CalendarEvents.Add(ev);
                imported++;
            }
            catch (DomainException ex)
            {
                skipped++;
                if (warnings.Count < 20)
                {
                    warnings.Add(ex.Message);
                }
            }
        }

        if (parsed.Count > MaxImportEvents)
        {
            skipped += parsed.Count - MaxImportEvents;
            warnings.Add($"Only the first {MaxImportEvents} events were considered.");
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<CalendarImportResult>.Success(new CalendarImportResult(imported, skipped, warnings.ToArray()));
    }

    public async Task<Result<CalendarEventDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        return loaded.IsSuccess
            ? Result<CalendarEventDto>.Success(await ToDtoAsync(loaded.Value.Event, loaded.Value.Calendar, loaded.Value.Grant, ct).ConfigureAwait(false))
            : Result<CalendarEventDto>.Failure(loaded.Error!);
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

        var calendar = await EnsureSpaceDefaultCalendarAsync(space.Space, ct).ConfigureAwait(false);
        return await CreateOnCalendarAsync(calendar, space.Space.Id, request, ct).ConfigureAwait(false);
    }

    public async Task<Result<CalendarEventDto>> CreateOnCalendarAsync(Guid calendarId, CalendarEventRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cal = await db.Calendars.FirstOrDefaultAsync(c => c.Id == calendarId && c.ArchivedAt == null, ct).ConfigureAwait(false);
        if (cal is null)
        {
            return Result<CalendarEventDto>.Failure(CalendarNotFound);
        }

        var grant = await EffectiveGrantAsync(cal, ct).ConfigureAwait(false);
        if (grant is null || grant < CalendarGrant.Create)
        {
            return Result<CalendarEventDto>.Failure(CalendarNotFound);
        }

        return await CreateOnCalendarAsync(cal, cal.SpaceId, request, ct).ConfigureAwait(false);
    }

    private async Task<Result<CalendarEventDto>> CreateOnCalendarAsync(WikiCalendar calendar, Guid? spaceId, CalendarEventRequest request, CancellationToken ct)
    {
        var details = ToDetails(request);
        if (!details.IsSuccess)
        {
            return Result<CalendarEventDto>.Failure(details.Error!);
        }

        var links = await ValidateLinksAsync(details.Value.LinkedPageId, details.Value.LinkedWhiteboardId, ct).ConfigureAwait(false);
        if (!links.IsSuccess)
        {
            return Result<CalendarEventDto>.Failure(links.Error!);
        }

        try
        {
            var ev = CalendarEvent.Create(calendar.Id, spaceId, details.Value, current.UserId, clock.UtcNow);
            db.CalendarEvents.Add(ev);
            ReplaceReminders(ev.Id, request.Reminders, isNew: true);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await ReplaceAttendeesAsync(ev, request.Attendees, ct).ConfigureAwait(false);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<CalendarEventDto>.Success(await ToDtoAsync(ev, calendar, grant: CalendarGrant.Manage, ct).ConfigureAwait(false));
        }
        catch (DomainException ex)
        {
            return Result<CalendarEventDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    public async Task<Result<CalendarEventDto>> UpdateAsync(Guid id, int expectedRevision, CalendarEventRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<CalendarEventDto>.Failure(loaded.Error!);
        }

        var (ev, calendar, grant) = loaded.Value;
        if (!CallerCanEdit(grant, ev.CreatedBy))
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

        var links = await ValidateLinksAsync(details.Value.LinkedPageId, details.Value.LinkedWhiteboardId, ct).ConfigureAwait(false);
        if (!links.IsSuccess)
        {
            return Result<CalendarEventDto>.Failure(links.Error!);
        }

        var scope = RecurrenceEditScope.EntireSeries;
        if (!string.IsNullOrWhiteSpace(request.EditScope) && !Enum.TryParse(request.EditScope, true, out scope))
        {
            return Result<CalendarEventDto>.Failure(ScopeInvalid);
        }

        try
        {
            if (scope == RecurrenceEditScope.EntireSeries || !ev.Rule.IsRecurring)
            {
                ev.Update(details.Value, current.UserId, clock.UtcNow);
                ReplaceReminders(ev.Id, request.Reminders, isNew: false);
                await ReplaceAttendeesAsync(ev, request.Attendees, ct).ConfigureAwait(false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Result<CalendarEventDto>.Success(await ToDtoAsync(ev, calendar, grant, ct).ConfigureAwait(false));
            }

            if (request.OccurrenceDate is not { } occ)
            {
                return Result<CalendarEventDto>.Failure(OccurrenceRequired);
            }

            if (scope == RecurrenceEditScope.ThisOccurrence)
            {
                ev.Exclude(occ, clock.UtcNow);
                var span = details.Value.EndDate.DayNumber - details.Value.StartDate.DayNumber;
                var exceptionDetails = details.Value with
                {
                    StartDate = occ,
                    EndDate = occ.AddDays(Math.Max(0, span)),
                    Recurrence = RecurrenceRule.None,
                };
                var exception = CalendarEvent.Create(calendar.Id, calendar.SpaceId, exceptionDetails, current.UserId, clock.UtcNow);
                db.CalendarEvents.Add(exception);
                ReplaceReminders(exception.Id, request.Reminders, isNew: true);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Result<CalendarEventDto>.Success(await ToDtoAsync(exception, calendar, grant, ct).ConfigureAwait(false));
            }

            // ThisAndFollowing: truncate original series the day before, create a new series from occurrence.
            var until = occ.AddDays(-1);
            if (until < ev.StartDate)
            {
                // Editing the first occurrence and following = entire series.
                ev.Update(details.Value, current.UserId, clock.UtcNow);
                ReplaceReminders(ev.Id, request.Reminders, isNew: false);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Result<CalendarEventDto>.Success(await ToDtoAsync(ev, calendar, grant, ct).ConfigureAwait(false));
            }

            var truncated = new EventDetails(
                ev.Title, ev.Description, ev.Location, ev.IsAllDay, ev.StartDate, ev.StartTime, ev.EndDate, ev.EndTime, ev.TimeZone,
                ev.Rule with { Until = until, Count = null },
                ev.Type, ev.Status, ev.Priority, ev.Color, ev.OnlineUrl, ev.IsPrivate, ev.OrganizerId, ev.LinkedPageId, ev.LinkedWhiteboardId);
            ev.Update(truncated, current.UserId, clock.UtcNow);
            var spanDays = details.Value.EndDate.DayNumber - details.Value.StartDate.DayNumber;
            var following = details.Value with { StartDate = occ, EndDate = occ.AddDays(Math.Max(0, spanDays)) };
            var neo = CalendarEvent.Create(calendar.Id, calendar.SpaceId, following, current.UserId, clock.UtcNow);
            db.CalendarEvents.Add(neo);
            ReplaceReminders(neo.Id, request.Reminders, isNew: true);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<CalendarEventDto>.Success(await ToDtoAsync(neo, calendar, grant, ct).ConfigureAwait(false));
        }
        catch (DomainException ex)
        {
            return Result<CalendarEventDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    /// <summary>Cancels a single occurrence of a recurring event.</summary>
    public async Task<Result<CalendarEventDto>> ExcludeAsync(Guid id, DateOnly occurrence, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<CalendarEventDto>.Failure(loaded.Error!);
        }

        var (ev, calendar, grant) = loaded.Value;
        if (!CallerCanEdit(grant, ev.CreatedBy))
        {
            return Result<CalendarEventDto>.Failure(WikiErrors.Forbidden);
        }

        try
        {
            ev.Exclude(occurrence, clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<CalendarEventDto>.Success(await ToDtoAsync(ev, calendar, grant, ct).ConfigureAwait(false));
        }
        catch (DomainException ex)
        {
            return Result<CalendarEventDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        if (!CallerCanEdit(loaded.Value.Grant, loaded.Value.Event.CreatedBy))
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        var reminders = await db.CalendarEventReminders.Where(r => r.EventId == id).ToListAsync(ct).ConfigureAwait(false);
        db.CalendarEventReminders.RemoveRange(reminders);
        db.CalendarEvents.Remove(loaded.Value.Event);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Moves an editable event onto another writable calendar (Create+ on target, Edit on source).</summary>
    public async Task<Result<CalendarEventDto>> MoveAsync(Guid id, CalendarMoveRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.TargetCalendarId is not { } targetId || targetId == Guid.Empty)
        {
            return Result<CalendarEventDto>.Failure(CalendarNotFound);
        }

        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<CalendarEventDto>.Failure(loaded.Error!);
        }

        var (ev, _, grant) = loaded.Value;
        if (!CallerCanEdit(grant, ev.CreatedBy))
        {
            return Result<CalendarEventDto>.Failure(WikiErrors.Forbidden);
        }

        if (ev.CalendarId == targetId)
        {
            return Result<CalendarEventDto>.Success(await ToDtoAsync(ev, loaded.Value.Calendar, grant, ct).ConfigureAwait(false));
        }

        var target = await db.Calendars.FirstOrDefaultAsync(c => c.Id == targetId && c.ArchivedAt == null, ct).ConfigureAwait(false);
        if (target is null)
        {
            return Result<CalendarEventDto>.Failure(CalendarNotFound);
        }

        var targetGrant = await EffectiveGrantAsync(target, ct).ConfigureAwait(false);
        if (targetGrant is null || targetGrant < CalendarGrant.Create)
        {
            return Result<CalendarEventDto>.Failure(CalendarNotFound);
        }

        try
        {
            ev.MoveTo(target.Id, target.SpaceId, current.UserId, clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<CalendarEventDto>.Success(await ToDtoAsync(ev, target, targetGrant.Value, ct).ConfigureAwait(false));
        }
        catch (DomainException ex)
        {
            return Result<CalendarEventDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
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

            rule = new RecurrenceRule(freq, rr.Interval ?? 1, mask, cal, rr.Until, rr.Count, rr.MonthWeek ?? 0);
        }

        var type = CalendarEventType.Event;
        if (!string.IsNullOrWhiteSpace(r.Type) && !Enum.TryParse(r.Type, true, out type))
        {
            return Result<EventDetails>.Failure(new Error("calendar.event_meta_invalid", "The event type, status or priority is not valid.", ErrorKind.Validation));
        }

        var status = CalendarEventStatus.Scheduled;
        if (!string.IsNullOrWhiteSpace(r.Status) && !Enum.TryParse(r.Status, true, out status))
        {
            return Result<EventDetails>.Failure(new Error("calendar.event_meta_invalid", "The event type, status or priority is not valid.", ErrorKind.Validation));
        }

        var priority = CalendarEventPriority.Normal;
        if (!string.IsNullOrWhiteSpace(r.Priority) && !Enum.TryParse(r.Priority, true, out priority))
        {
            return Result<EventDetails>.Failure(new Error("calendar.event_meta_invalid", "The event type, status or priority is not valid.", ErrorKind.Validation));
        }

        return Result<EventDetails>.Success(new EventDetails(
            r.Title, r.Description, r.Location, r.AllDay, r.StartDate, start, r.EndDate ?? r.StartDate, end, r.TimeZone, rule,
            type, status, priority, r.Color, r.OnlineUrl, r.IsPrivate == true, null, r.LinkedPageId, r.LinkedWhiteboardId));
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

    /// <summary>Ensures the space has a default calendar (idempotent). Used by create-event and migration backfill.</summary>
    public async Task<WikiCalendar> EnsureSpaceDefaultCalendarAsync(Space space, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(space);
        var existing = await db.Calendars.FirstOrDefaultAsync(c => c.SpaceId == space.Id && c.IsDefault && c.Scope == CalendarScope.Space && c.ArchivedAt == null, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            return existing;
        }

        var cal = WikiCalendar.CreateSpaceDefault(space.Id, space.Name, current.UserId == Guid.Empty ? space.CreatedBy : current.UserId, clock.UtcNow);
        db.Calendars.Add(cal);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return cal;
    }

    public async Task<Result<IReadOnlyList<CalendarDto>>> ListCalendarsAsync(string? spaceKey, bool includeArchived, CancellationToken ct)
    {
        var q = db.Calendars.AsNoTracking().AsQueryable();
        if (!includeArchived)
        {
            q = q.Where(c => c.ArchivedAt == null);
        }

        if (spaceKey is not null)
        {
            var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
            if (space is null)
            {
                return Result<IReadOnlyList<CalendarDto>>.Failure(WikiErrors.SpaceNotFound);
            }

            q = q.Where(c => c.Scope == CalendarScope.Space && c.SpaceId == space.Space.Id);
        }
        else
        {
            var viewable = (await authz.ViewableSpaceIdsAsync(ct).ConfigureAwait(false)).ToList();
            var principal = await authz.PrincipalAsync(ct).ConfigureAwait(false);
            var shared = await db.CalendarAccessGrants.AsNoTracking()
                .Where(g => (g.SubjectType == CalendarSubjectType.User && g.SubjectId == current.UserId)
                    || (g.SubjectType == CalendarSubjectType.Group && principal.GroupIds.Contains(g.SubjectId)))
                .Select(g => g.CalendarId).Distinct().ToListAsync(ct).ConfigureAwait(false);
            q = q.Where(c =>
                (c.Scope == CalendarScope.Personal && c.OwnerUserId == current.UserId)
                || (c.Scope == CalendarScope.Organization)
                || (c.Scope == CalendarScope.Space && c.SpaceId != null && viewable.Contains(c.SpaceId.Value))
                || shared.Contains(c.Id));
        }

        var rows = await q.OrderBy(c => c.Name).Take(500).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<CalendarDto>>.Success(rows.Select(ToCalendarDto).ToList());
    }

    public async Task<Result<CalendarDto>> CreateCalendarAsync(CalendarRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.TryParse<CalendarScope>(request.Scope, true, out var scope))
        {
            return Result<CalendarDto>.Failure(new Error("calendar.scope_invalid", "Choose Personal, Space or Organization.", ErrorKind.Validation));
        }

        try
        {
            WikiCalendar cal;
            if (scope == CalendarScope.Personal)
            {
                cal = WikiCalendar.CreatePersonal(current.UserId, request.Name, clock.UtcNow);
                if (!string.IsNullOrWhiteSpace(request.Description) || !string.IsNullOrWhiteSpace(request.Color) || !string.IsNullOrWhiteSpace(request.Icon))
                {
                    cal.UpdateMeta(cal.Name, request.Description, request.Color, request.Icon, current.UserId, clock.UtcNow);
                }
            }
            else if (scope == CalendarScope.Organization)
            {
                if (!(await authz.PrincipalAsync(ct).ConfigureAwait(false)).IsTenantAdministrator)
                {
                    return Result<CalendarDto>.Failure(WikiErrors.Forbidden);
                }

                cal = WikiCalendar.CreateOrganization(request.Name ?? "Organization", request.Description, request.Color, current.UserId, clock.UtcNow);
                if (!string.IsNullOrWhiteSpace(request.Icon))
                {
                    cal.UpdateMeta(cal.Name, cal.Description, cal.Color, request.Icon, current.UserId, clock.UtcNow);
                }
            }
            else
            {
                if (string.IsNullOrWhiteSpace(request.SpaceKey))
                {
                    return Result<CalendarDto>.Failure(WikiErrors.SpaceNotFound);
                }

                var space = await spaces.LoadAsync(request.SpaceKey, requireView: true, ct).ConfigureAwait(false);
                if (space is null)
                {
                    return Result<CalendarDto>.Failure(WikiErrors.SpaceNotFound);
                }

                if (!space.CanWrite(PermissionAction.Edit) || space.Space.Status == SpaceStatus.Archived)
                {
                    return Result<CalendarDto>.Failure(space.Space.Status == SpaceStatus.Archived ? WikiErrors.SpaceArchived : WikiErrors.Forbidden);
                }

                cal = WikiCalendar.CreateSpace(space.Space.Id, request.Name ?? space.Space.Name, request.Description, request.Color, current.UserId, clock.UtcNow);
                if (!string.IsNullOrWhiteSpace(request.Icon))
                {
                    cal.UpdateMeta(cal.Name, cal.Description, cal.Color, request.Icon, current.UserId, clock.UtcNow);
                }
            }

            db.Calendars.Add(cal);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<CalendarDto>.Success(ToCalendarDto(cal));
        }
        catch (DomainException ex)
        {
            return Result<CalendarDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    public async Task<Result<CalendarDto>> UpdateCalendarAsync(Guid id, CalendarRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cal = await db.Calendars.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);
        if (cal is null || !await CanManageCalendarAsync(cal, ct).ConfigureAwait(false))
        {
            return Result<CalendarDto>.Failure(CalendarNotFound);
        }

        try
        {
            cal.UpdateMeta(request.Name, request.Description, request.Color, request.Icon, current.UserId, clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<CalendarDto>.Success(ToCalendarDto(cal));
        }
        catch (DomainException ex)
        {
            return Result<CalendarDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    public async Task<Result<CalendarDto>> ArchiveCalendarAsync(Guid id, bool restore, CancellationToken ct)
    {
        var cal = await db.Calendars.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);
        if (cal is null || !await CanManageCalendarAsync(cal, ct).ConfigureAwait(false))
        {
            return Result<CalendarDto>.Failure(CalendarNotFound);
        }

        if (restore)
        {
            cal.Restore(current.UserId, clock.UtcNow);
        }
        else
        {
            cal.Archive(current.UserId, clock.UtcNow);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<CalendarDto>.Success(ToCalendarDto(cal));
    }

    /// <summary>
    /// Permanently deletes an archived Raavi calendar and its owned events only. Never touches external/Jira/Templar authority.
    /// Requires typing the calendar name.
    /// </summary>
    public async Task<Result> HardDeleteCalendarAsync(Guid id, CalendarHardDeleteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cal = await db.Calendars.FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false);
        if (cal is null || !await CanManageCalendarAsync(cal, ct).ConfigureAwait(false))
        {
            return Result.Failure(CalendarNotFound);
        }

        if (cal.ArchivedAt is null)
        {
            return Result.Failure(NotArchived);
        }

        if (!string.Equals(cal.Name, (request.ConfirmName ?? string.Empty).Trim(), StringComparison.Ordinal))
        {
            return Result.Failure(ConfirmMismatch);
        }

        var events = await db.CalendarEvents.Where(e => e.CalendarId == cal.Id).ToListAsync(ct).ConfigureAwait(false);
        var eventIds = events.Select(e => e.Id).ToList();
        var reminders = await db.CalendarEventReminders.Where(r => eventIds.Contains(r.EventId)).ToListAsync(ct).ConfigureAwait(false);
        db.CalendarEventReminders.RemoveRange(reminders);
        db.CalendarEvents.RemoveRange(events);
        var grants = await db.CalendarAccessGrants.Where(p => p.CalendarId == cal.Id).ToListAsync(ct).ConfigureAwait(false);
        db.CalendarAccessGrants.RemoveRange(grants);
        db.Calendars.Remove(cal);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<bool> CanManageCalendarAsync(WikiCalendar cal, CancellationToken ct)
    {
        if (cal.Scope == CalendarScope.Personal)
        {
            return cal.OwnerUserId == current.UserId;
        }

        if (cal.Scope == CalendarScope.Organization)
        {
            return (await authz.PrincipalAsync(ct).ConfigureAwait(false)).IsTenantAdministrator;
        }

        if (cal.SpaceId is not { } sid)
        {
            return false;
        }

        var space = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct).ConfigureAwait(false);
        if (space is null)
        {
            return false;
        }

        var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        return rights.CanWrite(PermissionAction.Edit);
    }

    private static CalendarDto ToCalendarDto(WikiCalendar c) =>
        new(c.Id, c.Scope.ToString(), c.SpaceId, c.OwnerUserId, c.Name, c.Description, c.Color, c.Icon, c.IsDefault, c.ArchivedAt, c.Revision);

    private sealed record CalendarAccess(WikiCalendar Calendar, CalendarGrant Grant, string? SpaceKey, string? SpaceName);

    private async Task<Result<(CalendarEvent Event, WikiCalendar Calendar, CalendarGrant Grant)>> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var ev = await (track ? db.CalendarEvents : db.CalendarEvents.AsNoTracking()).FirstOrDefaultAsync(e => e.Id == id, ct).ConfigureAwait(false);
        if (ev is null)
        {
            return Result<(CalendarEvent, WikiCalendar, CalendarGrant)>.Failure(NotFound);
        }

        var calendar = await db.Calendars.AsNoTracking().FirstOrDefaultAsync(c => c.Id == ev.CalendarId, ct).ConfigureAwait(false);
        if (calendar is null)
        {
            return Result<(CalendarEvent, WikiCalendar, CalendarGrant)>.Failure(NotFound);
        }

        var grant = await EffectiveGrantAsync(calendar, ct).ConfigureAwait(false);
        if (grant is null || grant < CalendarGrant.FreeBusy)
        {
            return Result<(CalendarEvent, WikiCalendar, CalendarGrant)>.Failure(NotFound);
        }

        if (ev.IsPrivate && grant < CalendarGrant.View && calendar.OwnerUserId != current.UserId)
        {
            return Result<(CalendarEvent, WikiCalendar, CalendarGrant)>.Failure(NotFound);
        }

        return Result<(CalendarEvent, WikiCalendar, CalendarGrant)>.Success((ev, calendar, grant.Value));
    }

    private static string Date(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string? Time(TimeOnly? t) => t?.ToString("HH:mm", CultureInfo.InvariantCulture);

    private async Task<CalendarEventDto> ToDtoAsync(CalendarEvent e, WikiCalendar calendar, CalendarGrant grant, CancellationToken ct)
    {
        var attendees = await db.CalendarEventAttendees.AsNoTracking().Where(a => a.EventId == e.Id).OrderBy(a => a.InvitedAt).ToListAsync(ct).ConfigureAwait(false);
        var nameIds = attendees.Select(a => a.UserId).Append(e.CreatedBy).Distinct().ToList();
        var names = await directory.GetUserNamesAsync(nameIds, ct).ConfigureAwait(false);
        var rule = e.Rule;
        var recurrence = rule.IsRecurring
            ? new RecurrenceDto(rule.Frequency.ToString(), rule.Interval, Enumerable.Range(0, 7).Where(i => (rule.WeekDays & (1 << i)) != 0).ToArray(), rule.Calendar.ToString(), rule.Until is { } u ? Date(u) : null, rule.Count, rule.MonthWeek)
            : null;
        string? spaceKey = null, spaceName = null;
        if (calendar.SpaceId is { } sid)
        {
            var space = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct).ConfigureAwait(false);
            spaceKey = space?.Key;
            spaceName = space?.Name;
        }

        var reminders = await db.CalendarEventReminders.AsNoTracking().Where(r => r.EventId == e.Id).OrderBy(r => r.MinutesBefore).ToListAsync(ct).ConfigureAwait(false);
        var freeBusy = grant == CalendarGrant.FreeBusy && calendar.OwnerUserId != current.UserId;
        var my = attendees.FirstOrDefault(a => a.UserId == current.UserId);
        return new CalendarEventDto(
            e.Id, e.CalendarId, spaceKey, spaceName,
            freeBusy ? string.Empty : e.Title,
            freeBusy ? null : e.Description,
            freeBusy ? null : e.Location,
            e.IsAllDay, Date(e.StartDate), Time(e.StartTime), Date(e.EndDate), Time(e.EndTime), e.TimeZone,
            recurrence, e.ExcludedDates.OrderBy(d => d).Select(Date).ToArray(),
            e.Type.ToString(), e.Status.ToString(), e.Priority.ToString(), freeBusy ? null : e.Color, freeBusy ? null : e.OnlineUrl, e.IsPrivate,
            names.GetValueOrDefault(e.CreatedBy), e.CreatedAt, e.Revision, CallerCanEdit(grant, e.CreatedBy),
            reminders.Select(r => new CalendarReminderDto(r.Id, r.MinutesBefore, r.InApp, r.Email)).ToList(),
            freeBusy ? [] : attendees.Select(a => new CalendarAttendeeDto(a.UserId, names.GetValueOrDefault(a.UserId), a.Response.ToString(), a.Optional, a.InvitedAt, a.RespondedAt)).ToList(),
            freeBusy ? null : e.LinkedPageId,
            freeBusy ? null : e.LinkedWhiteboardId,
            my?.Response.ToString());
    }

    public async Task<Result<CalendarEventDto>> RsvpAsync(Guid id, CalendarRsvpRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Enum.TryParse<CalendarAttendeeResponse>(request.Response, true, out var response) || response == CalendarAttendeeResponse.Pending)
        {
            return Result<CalendarEventDto>.Failure(new Error("calendar.rsvp_invalid", "Choose Accepted, Declined or Tentative.", ErrorKind.Validation));
        }

        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<CalendarEventDto>.Failure(loaded.Error!);
        }

        var (ev, calendar, grant) = loaded.Value;
        var row = await db.CalendarEventAttendees.FirstOrDefaultAsync(a => a.EventId == id && a.UserId == current.UserId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result<CalendarEventDto>.Failure(NotFound);
        }

        try
        {
            row.Respond(response, clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<CalendarEventDto>.Success(await ToDtoAsync(ev, calendar, grant, ct).ConfigureAwait(false));
        }
        catch (DomainException ex)
        {
            return Result<CalendarEventDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    private async Task<Result> ValidateLinksAsync(Guid? pageId, Guid? whiteboardId, CancellationToken ct)
    {
        if (pageId is { } pid && pid != Guid.Empty)
        {
            var page = await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == pid, ct).ConfigureAwait(false);
            if (page is null)
            {
                return Result.Failure(new Error("calendar.link_invalid", "The linked page was not found.", ErrorKind.Validation));
            }

            var space = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == page.SpaceId, ct).ConfigureAwait(false);
            if (space is null || !(await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false)).CanView)
            {
                return Result.Failure(new Error("calendar.link_invalid", "The linked page was not found.", ErrorKind.Validation));
            }
        }

        if (whiteboardId is { } wid && wid != Guid.Empty)
        {
            var board = await db.Whiteboards.AsNoTracking().FirstOrDefaultAsync(b => b.Id == wid, ct).ConfigureAwait(false);
            if (board is null)
            {
                return Result.Failure(new Error("calendar.link_invalid", "The linked whiteboard was not found.", ErrorKind.Validation));
            }

            var space = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == board.SpaceId, ct).ConfigureAwait(false);
            if (space is null || !(await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false)).CanView)
            {
                return Result.Failure(new Error("calendar.link_invalid", "The linked whiteboard was not found.", ErrorKind.Validation));
            }
        }

        return Result.Success();
    }

    private async Task ReplaceAttendeesAsync(CalendarEvent ev, IReadOnlyList<CalendarAttendeeInput>? inputs, CancellationToken ct)
    {
        if (inputs is null)
        {
            return;
        }

        var wanted = inputs
            .Where(i => i.UserId is { } id && id != Guid.Empty && id != current.UserId)
            .GroupBy(i => i.UserId!.Value)
            .Select(g => g.First())
            .Take(CalendarEventAttendee.MaxPerEvent)
            .ToList();
        var existing = await db.CalendarEventAttendees.Where(a => a.EventId == ev.Id).ToListAsync(ct).ConfigureAwait(false);
        var keep = wanted.Select(w => w.UserId!.Value).ToHashSet();
        db.CalendarEventAttendees.RemoveRange(existing.Where(a => !keep.Contains(a.UserId)));
        var now = clock.UtcNow;
        foreach (var input in wanted)
        {
            var userId = input.UserId!.Value;
            var row = existing.FirstOrDefault(a => a.UserId == userId);
            if (row is null)
            {
                // In-app Notification rows require a Content FK; calendar events are not content nodes.
                // Invite visibility is the attendee list + RSVP until notifications support non-content kinds.
                db.CalendarEventAttendees.Add(CalendarEventAttendee.Invite(ev.Id, userId, input.Optional == true, now));
            }
        }
    }

    private static OccurrenceDto ToOccurrence(CalendarEvent e, CalendarAccess a, DateOnly start, bool canEdit, bool freeBusyOnly)
    {
        var end = start.AddDays(e.SpanDays);
        DateTimeOffset? startUtc = null, endUtc = null;
        if (!e.IsAllDay && e.StartTime is { } st && e.EndTime is { } et)
        {
            startUtc = ToUtc(start, st, e.TimeZone);
            endUtc = ToUtc(end, et, e.TimeZone);
        }

        return new OccurrenceDto(
            e.Id, e.CalendarId, a.SpaceKey, a.SpaceName,
            freeBusyOnly ? string.Empty : e.Title,
            freeBusyOnly ? null : e.Location,
            e.IsAllDay, Date(start), Time(e.StartTime), Date(end), Time(e.EndTime), e.TimeZone, startUtc, endUtc,
            e.Rule.IsRecurring, Date(start), canEdit, "Raavi", null, freeBusyOnly, freeBusyOnly ? null : e.Type.ToString(), freeBusyOnly ? null : (e.Color ?? a.Calendar.Color));
    }

    private static OccurrenceDto FromOverlay(CalendarOverlayOccurrence o) =>
        new(null, null, null, null, o.FreeBusyOnly ? string.Empty : o.Title, o.FreeBusyOnly ? null : o.Location, o.AllDay,
            o.StartDate, o.StartTime, o.EndDate, o.EndTime, o.TimeZone ?? "UTC", null, null, false, o.StartDate, false, o.SourceKind, o.Url, o.FreeBusyOnly, null, null);

    private bool CallerCanEdit(CalendarGrant grant, Guid createdBy) =>
        grant >= CalendarGrant.EditAll || (grant == CalendarGrant.EditOwn && createdBy == current.UserId);

    private async Task<Result<IReadOnlyList<CalendarAccess>>> ResolveAccessibleCalendarsAsync(string? spaceKey, CancellationToken ct)
    {
        var principal = await authz.PrincipalAsync(ct).ConfigureAwait(false);
        var list = new List<CalendarAccess>();

        if (spaceKey is not null)
        {
            var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
            if (space is null)
            {
                return Result<IReadOnlyList<CalendarAccess>>.Failure(WikiErrors.SpaceNotFound);
            }

            var cals = await db.Calendars.AsNoTracking().Where(c => c.Scope == CalendarScope.Space && c.SpaceId == space.Space.Id && c.ArchivedAt == null).ToListAsync(ct).ConfigureAwait(false);
            var grant = space.CanWrite(PermissionAction.Edit) ? CalendarGrant.Manage : CalendarGrant.View;
            foreach (var c in cals)
            {
                list.Add(new CalendarAccess(c, grant, space.Space.Key, space.Space.Name));
            }

            return Result<IReadOnlyList<CalendarAccess>>.Success(list);
        }

        var viewable = (await authz.ViewableSpaceIdsAsync(ct).ConfigureAwait(false)).ToList();
        var spacesById = await db.Spaces.AsNoTracking().Where(s => viewable.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);
        var candidates = await db.Calendars.AsNoTracking().Where(c => c.ArchivedAt == null && (
            (c.Scope == CalendarScope.Personal && c.OwnerUserId == current.UserId)
            || c.Scope == CalendarScope.Organization
            || (c.Scope == CalendarScope.Space && c.SpaceId != null && viewable.Contains(c.SpaceId.Value)))).ToListAsync(ct).ConfigureAwait(false);

        foreach (var c in candidates)
        {
            var g = await EffectiveGrantAsync(c, ct).ConfigureAwait(false);
            if (g is null)
            {
                continue;
            }

            string? key = null, name = null;
            if (c.SpaceId is { } sid && spacesById.TryGetValue(sid, out var sp))
            {
                key = sp.Key;
                name = sp.Name;
            }

            list.Add(new CalendarAccess(c, g.Value, key, name));
        }

        // Shared personal calendars via grants
        var sharedIds = await db.CalendarAccessGrants.AsNoTracking()
            .Where(g => g.SubjectType == CalendarSubjectType.User && g.SubjectId == current.UserId
                || g.SubjectType == CalendarSubjectType.Group && principal.GroupIds.Contains(g.SubjectId))
            .Select(g => g.CalendarId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var already = list.Select(a => a.Calendar.Id).ToHashSet();
        foreach (var id in sharedIds.Where(id => !already.Contains(id)))
        {
            var c = await db.Calendars.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id && x.ArchivedAt == null, ct).ConfigureAwait(false);
            if (c is null)
            {
                continue;
            }

            var g = await EffectiveGrantAsync(c, ct).ConfigureAwait(false);
            if (g is null)
            {
                continue;
            }

            list.Add(new CalendarAccess(c, g.Value, null, null));
        }

        return Result<IReadOnlyList<CalendarAccess>>.Success(list);
    }

    private async Task<CalendarGrant?> EffectiveGrantAsync(WikiCalendar cal, CancellationToken ct)
    {
        if (cal.Scope == CalendarScope.Personal && cal.OwnerUserId == current.UserId)
        {
            return CalendarGrant.Manage;
        }

        if (cal.Scope == CalendarScope.Organization)
        {
            var p = await authz.PrincipalAsync(ct).ConfigureAwait(false);
            if (p.IsTenantAdministrator)
            {
                return CalendarGrant.Manage;
            }
        }

        if (cal.Scope == CalendarScope.Space && cal.SpaceId is { } sid)
        {
            var space = await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, ct).ConfigureAwait(false);
            if (space is null)
            {
                return null;
            }

            var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
            if (!rights.CanView)
            {
                return null;
            }

            return rights.CanWrite(PermissionAction.Edit) ? CalendarGrant.Manage : CalendarGrant.View;
        }

        var principal = await authz.PrincipalAsync(ct).ConfigureAwait(false);
        var grants = await db.CalendarAccessGrants.AsNoTracking()
            .Where(g => g.CalendarId == cal.Id && (
                (g.SubjectType == CalendarSubjectType.User && g.SubjectId == current.UserId)
                || (g.SubjectType == CalendarSubjectType.Group && principal.GroupIds.Contains(g.SubjectId))))
            .Select(g => g.Grant).ToListAsync(ct).ConfigureAwait(false);
        return grants.Count == 0 ? null : grants.Max();
    }

    private void ReplaceReminders(Guid eventId, IReadOnlyList<CalendarReminderInput>? inputs, bool isNew)
    {
        if (inputs is null && isNew)
        {
            return;
        }

        if (!isNew)
        {
            var existing = db.CalendarEventReminders.Where(r => r.EventId == eventId).ToList();
            db.CalendarEventReminders.RemoveRange(existing);
        }

        if (inputs is null || inputs.Count == 0)
        {
            return;
        }

        if (inputs.Count > CalendarEventReminder.MaxOffsetsPerEvent)
        {
            throw new DomainException("calendar.reminder_invalid", $"At most {CalendarEventReminder.MaxOffsetsPerEvent} reminders per event.");
        }

        foreach (var i in inputs)
        {
            db.CalendarEventReminders.Add(CalendarEventReminder.Create(eventId, i.MinutesBefore, i.InApp, i.Email));
        }
    }

    public async Task<Result<IReadOnlyList<CalendarGrantDto>>> ListGrantsAsync(Guid calendarId, CancellationToken ct)
    {
        var cal = await db.Calendars.AsNoTracking().FirstOrDefaultAsync(c => c.Id == calendarId, ct).ConfigureAwait(false);
        if (cal is null || !await CanManageCalendarAsync(cal, ct).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<CalendarGrantDto>>.Failure(CalendarNotFound);
        }

        var rows = await db.CalendarAccessGrants.AsNoTracking().Where(g => g.CalendarId == calendarId).OrderBy(g => g.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<CalendarGrantDto>>.Success(rows.Select(g => new CalendarGrantDto(g.Id, g.SubjectType.ToString(), g.SubjectId, g.Grant.ToString(), g.CreatedAt)).ToList());
    }

    public async Task<Result<CalendarGrantDto>> PutGrantAsync(Guid calendarId, CalendarGrantRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var cal = await db.Calendars.FirstOrDefaultAsync(c => c.Id == calendarId, ct).ConfigureAwait(false);
        if (cal is null || !await CanManageCalendarAsync(cal, ct).ConfigureAwait(false))
        {
            return Result<CalendarGrantDto>.Failure(CalendarNotFound);
        }

        if (!Enum.TryParse<CalendarSubjectType>(request.SubjectType, true, out var subjectType)
            || !Enum.TryParse<CalendarGrant>(request.Grant, true, out var grant)
            || request.SubjectId is not { } subjectId || subjectId == Guid.Empty)
        {
            return Result<CalendarGrantDto>.Failure(new Error("calendar.permission_invalid", "The calendar permission is not valid.", ErrorKind.Validation));
        }

        var existing = await db.CalendarAccessGrants.FirstOrDefaultAsync(
            g => g.CalendarId == calendarId && g.SubjectType == subjectType && g.SubjectId == subjectId, ct).ConfigureAwait(false);
        if (existing is null)
        {
            existing = CalendarAccessGrant.Create(calendarId, subjectType, subjectId, grant, current.UserId, clock.UtcNow);
            db.CalendarAccessGrants.Add(existing);
        }
        else
        {
            existing.ChangeGrant(grant);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<CalendarGrantDto>.Success(new CalendarGrantDto(existing.Id, existing.SubjectType.ToString(), existing.SubjectId, existing.Grant.ToString(), existing.CreatedAt));
    }

    public async Task<Result> DeleteGrantAsync(Guid calendarId, Guid grantId, CancellationToken ct)
    {
        var cal = await db.Calendars.AsNoTracking().FirstOrDefaultAsync(c => c.Id == calendarId, ct).ConfigureAwait(false);
        if (cal is null || !await CanManageCalendarAsync(cal, ct).ConfigureAwait(false))
        {
            return Result.Failure(CalendarNotFound);
        }

        var row = await db.CalendarAccessGrants.FirstOrDefaultAsync(g => g.Id == grantId && g.CalendarId == calendarId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(CalendarNotFound);
        }

        db.CalendarAccessGrants.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<CalendarUserPrefsDto>> GetPrefsAsync(CancellationToken ct)
    {
        var row = await db.CalendarUserPreferences.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == current.UserId, ct).ConfigureAwait(false)
            ?? CalendarUserPreference.Create(current.UserId, clock.UtcNow);
        return Result<CalendarUserPrefsDto>.Success(ToPrefsDto(row));
    }

    public async Task<Result<CalendarUserPrefsDto>> UpdatePrefsAsync(CalendarUserPrefsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var row = await db.CalendarUserPreferences.FirstOrDefaultAsync(p => p.UserId == current.UserId, ct).ConfigureAwait(false);
        if (row is null)
        {
            row = CalendarUserPreference.Create(current.UserId, clock.UtcNow);
            db.CalendarUserPreferences.Add(row);
        }

        try
        {
            row.Update(
                request.BrowserAlerts ?? row.BrowserAlerts,
                request.TtsEnabled ?? row.TtsEnabled,
                request.TtsLocale ?? row.TtsLocale,
                request.TtsRate ?? row.TtsRate,
                request.HidePrivateTitlesInTts ?? row.HidePrivateTitlesInTts,
                request.QuietStartMinutes ?? row.QuietStartMinutes,
                request.QuietEndMinutes ?? row.QuietEndMinutes,
                request.TimeZone ?? row.TimeZone,
                clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<CalendarUserPrefsDto>.Success(ToPrefsDto(row));
        }
        catch (DomainException ex)
        {
            return Result<CalendarUserPrefsDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    private static CalendarUserPrefsDto ToPrefsDto(CalendarUserPreference p) =>
        new(p.BrowserAlerts, p.TtsEnabled, p.TtsLocale, p.TtsRate, p.HidePrivateTitlesInTts, p.QuietStartMinutes, p.QuietEndMinutes, p.TimeZone);

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

/// <summary>Minimal RFC 5545 reader for import: unfolds lines, reads VEVENT SUMMARY/DTSTART/DTEND/DESCRIPTION/LOCATION/RRULE (FREQ/INTERVAL/COUNT/UNTIL/BYDAY).</summary>
public static class IcsReader
{
    public sealed record Draft(string Title, string? Description, string? Location, bool AllDay, DateOnly StartDate, TimeOnly? StartTime, DateOnly EndDate, TimeOnly? EndTime, string TimeZone, RecurrenceRule Recurrence)
    {
        public EventDetails ToDetails() => new(Title, Description, Location, AllDay, StartDate, StartTime, EndDate, EndTime, TimeZone, Recurrence);
    }

    public static IReadOnlyList<Draft> Parse(string ics)
    {
        var lines = Unfold(ics);
        var list = new List<Draft>();
        Dictionary<string, string>? cur = null;
        foreach (var line in lines)
        {
            if (line.Equals("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                cur = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            if (line.Equals("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (cur is not null && TryDraft(cur, out var draft))
                {
                    list.Add(draft);
                }

                cur = null;
                continue;
            }

            if (cur is null)
            {
                continue;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var name = line[..colon];
            var semi = name.IndexOf(';');
            var key = semi < 0 ? name : name[..semi];
            cur[key] = line[(colon + 1)..];
            if (key.Equals("DTSTART", StringComparison.OrdinalIgnoreCase) || key.Equals("DTEND", StringComparison.OrdinalIgnoreCase))
            {
                cur[key + "_RAW"] = line; // keep params
            }
        }

        return list;
    }

    private static IEnumerable<string> Unfold(string ics)
    {
        var raw = ics.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        var buf = new StringBuilder();
        foreach (var line in raw)
        {
            if (line.StartsWith(' ') || line.StartsWith('\t'))
            {
                buf.Append(line.AsSpan(1));
                continue;
            }

            if (buf.Length > 0)
            {
                yield return buf.ToString();
                buf.Clear();
            }

            buf.Append(line);
        }

        if (buf.Length > 0)
        {
            yield return buf.ToString();
        }
    }

    private static bool TryDraft(Dictionary<string, string> props, out Draft draft)
    {
        draft = null!;
        if (!props.TryGetValue("SUMMARY", out var summary) || string.IsNullOrWhiteSpace(summary))
        {
            return false;
        }

        summary = Unescape(summary);
        if (summary.Length > CalendarEvent.MaxTitleLength)
        {
            summary = summary[..CalendarEvent.MaxTitleLength];
        }

        props.TryGetValue("DESCRIPTION", out var description);
        description = string.IsNullOrWhiteSpace(description) ? null : Unescape(description);
        if (description is { Length: > CalendarEvent.MaxDescriptionLength })
        {
            description = description[..CalendarEvent.MaxDescriptionLength];
        }

        props.TryGetValue("LOCATION", out var location);
        location = string.IsNullOrWhiteSpace(location) ? null : Unescape(location);
        if (location is { Length: > CalendarEvent.MaxLocationLength })
        {
            location = location[..CalendarEvent.MaxLocationLength];
        }

        if (!props.TryGetValue("DTSTART_RAW", out var startRaw) && !props.TryGetValue("DTSTART", out startRaw))
        {
            return false;
        }

        if (!TryDateTime(startRaw.Contains(':') ? startRaw : "DTSTART:" + startRaw, out var startDate, out var startTime, out var allDay, out var zone))
        {
            return false;
        }

        DateOnly endDate = startDate;
        TimeOnly? endTime = startTime;
        if (props.TryGetValue("DTEND_RAW", out var endRaw) || props.TryGetValue("DTEND", out endRaw))
        {
            var raw = endRaw.Contains(':') ? endRaw : "DTEND:" + endRaw;
            if (TryDateTime(raw, out var ed, out var et, out var endAllDay, out _))
            {
                endDate = allDay || endAllDay ? ed.AddDays(-1) : ed; // iCal exclusive end for all-day
                if (endDate < startDate)
                {
                    endDate = startDate;
                }

                endTime = allDay ? null : et ?? startTime?.AddHours(1) ?? new TimeOnly(10, 0);
            }
        }
        else if (!allDay)
        {
            endTime = startTime?.AddHours(1) ?? new TimeOnly(10, 0);
        }

        var rule = RecurrenceRule.None;
        if (props.TryGetValue("RRULE", out var rrule))
        {
            rule = ParseRRule(rrule, startDate);
        }

        draft = new Draft(summary, description, location, allDay, startDate, allDay ? null : startTime, endDate, allDay ? null : endTime, zone ?? "UTC", rule);
        return true;
    }

    private static RecurrenceRule ParseRRule(string rrule, DateOnly start)
    {
        var parts = rrule.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var map = parts.Select(p => p.Split('=', 2)).Where(a => a.Length == 2).ToDictionary(a => a[0], a => a[1], StringComparer.OrdinalIgnoreCase);
        if (!map.TryGetValue("FREQ", out var freqText) || !Enum.TryParse<RecurrenceFrequency>(freqText, true, out var freq) || freq == RecurrenceFrequency.None)
        {
            return RecurrenceRule.None;
        }

        var interval = map.TryGetValue("INTERVAL", out var iv) && int.TryParse(iv, out var n) ? Math.Clamp(n, 1, CalendarEvent.MaxInterval) : 1;
        int? count = map.TryGetValue("COUNT", out var c) && int.TryParse(c, out var cn) ? Math.Clamp(cn, 1, CalendarEvent.MaxCount) : null;
        DateOnly? until = null;
        if (map.TryGetValue("UNTIL", out var u) && TryParseIcsDate(u, out var ud, out _))
        {
            until = ud;
            count = null;
        }

        var mask = 0;
        if (freq == RecurrenceFrequency.Weekly && map.TryGetValue("BYDAY", out var byday))
        {
            foreach (var d in byday.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var day = d.Length >= 2 ? d[^2..] : d;
                mask |= day.ToUpperInvariant() switch
                {
                    "SU" => 1 << 0,
                    "MO" => 1 << 1,
                    "TU" => 1 << 2,
                    "WE" => 1 << 3,
                    "TH" => 1 << 4,
                    "FR" => 1 << 5,
                    "SA" => 1 << 6,
                    _ => 0,
                };
            }
        }

        if (freq == RecurrenceFrequency.Weekly && mask == 0)
        {
            mask = 1 << (int)start.DayOfWeek;
        }

        return new RecurrenceRule(freq, interval, mask, RecurrenceCalendar.Gregorian, until, count);
    }

    private static bool TryDateTime(string line, out DateOnly date, out TimeOnly? time, out bool allDay, out string? zone)
    {
        date = default;
        time = null;
        allDay = false;
        zone = null;
        var colon = line.IndexOf(':');
        if (colon < 0)
        {
            return false;
        }

        var meta = line[..colon];
        var value = line[(colon + 1)..].Trim();
        allDay = meta.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) || value.Length == 8;
        var tz = meta.Split(';').FirstOrDefault(p => p.StartsWith("TZID=", StringComparison.OrdinalIgnoreCase));
        if (tz is not null)
        {
            zone = tz["TZID=".Length..].Trim('"');
        }

        return TryParseIcsDate(value, out date, out time);
    }

    private static bool TryParseIcsDate(string value, out DateOnly date, out TimeOnly? time)
    {
        date = default;
        time = null;
        value = value.Trim();
        if (value.Length >= 8 && DateOnly.TryParseExact(value[..8], "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date))
        {
            if (value.Length >= 15 && value[8] == 'T'
                && TimeOnly.TryParseExact(value.AsSpan(9, 6), "HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t))
            {
                time = t;
            }

            return true;
        }

        return false;
    }

    private static string Unescape(string text) =>
        text.Replace("\\n", "\n", StringComparison.Ordinal).Replace("\\,", ",", StringComparison.Ordinal).Replace("\\;", ";", StringComparison.Ordinal).Replace("\\\\", "\\", StringComparison.Ordinal);
}
