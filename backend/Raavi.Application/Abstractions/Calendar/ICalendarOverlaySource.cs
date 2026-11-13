namespace Raavi.Application.Abstractions.Calendar;

/// <summary>Ephemeral occurrence from an external source (Jira, Templar, …). Never authoritative Raavi storage.</summary>
public sealed record CalendarOverlayOccurrence(
    string SourceKind,
    string ExternalId,
    string Title,
    string? Location,
    bool AllDay,
    string StartDate,
    string? StartTime,
    string EndDate,
    string? EndTime,
    string? TimeZone,
    string? Url,
    bool FreeBusyOnly);

/// <summary>Read-only overlay contributions to the calendar occurrence feed (Integrations / Templar).</summary>
public interface ICalendarOverlaySource
{
    string Kind { get; }

    Task<IReadOnlyList<CalendarOverlayOccurrence>> GetOccurrencesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}

/// <summary>Templar calendar provider shell. Live calls stay Blocked until the API contract is wired.</summary>
public interface ITemplarCalendarProvider
{
    bool IsConfigured { get; }

    Task<IReadOnlyList<CalendarOverlayOccurrence>> GetOccurrencesAsync(DateOnly from, DateOnly to, CancellationToken cancellationToken);
}
