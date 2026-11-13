using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Application;

namespace Raavi.Wiki.Api;

public sealed record ExcludeOccurrenceRequest(DateOnly Date);

/// <summary>Space calendars (docs/api/WIKI_API.md §Calendar). Tenant host only.</summary>
public static class CalendarEndpoints
{
    public static void MapCalendarEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        g.MapGet("/calendars", async (string? spaceKey, bool? includeArchived, CalendarService svc, CancellationToken ct) =>
            (await svc.ListCalendarsAsync(spaceKey, includeArchived == true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/spaces/{key}/calendars", async (string key, bool? includeArchived, CalendarService svc, CancellationToken ct) =>
            (await svc.ListCalendarsAsync(key, includeArchived == true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/calendars", async (CalendarRequest body, CalendarService svc, CancellationToken ct) =>
            (await svc.CreateCalendarAsync(body, ct).ConfigureAwait(false)).ToHttp(c => Results.Created($"/api/v1/calendars/{c.Id}", c)));

        g.MapPut("/calendars/{id:guid}", async (Guid id, CalendarRequest body, CalendarService svc, CancellationToken ct) =>
            (await svc.UpdateCalendarAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/calendars/{id:guid}/archive", async (Guid id, CalendarService svc, CancellationToken ct) =>
            (await svc.ArchiveCalendarAsync(id, restore: false, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/calendars/{id:guid}/restore", async (Guid id, CalendarService svc, CancellationToken ct) =>
            (await svc.ArchiveCalendarAsync(id, restore: true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/calendars/{id:guid}/purge", async (Guid id, CalendarHardDeleteRequest body, CalendarService svc, CancellationToken ct) =>
            (await svc.HardDeleteCalendarAsync(id, body, ct).ConfigureAwait(false)).ToHttp());

        g.MapGet("/calendars/{id:guid}/grants", async (Guid id, CalendarService svc, CancellationToken ct) =>
            (await svc.ListGrantsAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPut("/calendars/{id:guid}/grants", async (Guid id, CalendarGrantRequest body, CalendarService svc, CancellationToken ct) =>
            (await svc.PutGrantAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapDelete("/calendars/{id:guid}/grants/{grantId:guid}", async (Guid id, Guid grantId, CalendarService svc, CancellationToken ct) =>
            (await svc.DeleteGrantAsync(id, grantId, ct).ConfigureAwait(false)).ToHttp());

        g.MapGet("/calendar/preferences", async (CalendarService svc, CancellationToken ct) =>
            (await svc.GetPrefsAsync(ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPut("/calendar/preferences", async (CalendarUserPrefsRequest body, CalendarService svc, CancellationToken ct) =>
            (await svc.UpdatePrefsAsync(body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/calendar/occurrences", async (DateOnly from, DateOnly to, string? calendarIds, string? q, string? type, string? sourceKind, CalendarService svc, CancellationToken ct) =>
            (await svc.OccurrencesAsync(null, from, to, ct, ParseIds(calendarIds), q, type, sourceKind).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/spaces/{key}/calendar/occurrences", async (string key, DateOnly from, DateOnly to, string? calendarIds, string? q, string? type, string? sourceKind, CalendarService svc, CancellationToken ct) =>
            (await svc.OccurrencesAsync(key, from, to, ct, ParseIds(calendarIds), q, type, sourceKind).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/calendars/{id:guid}/import", async (Guid id, CalendarImportRequest body, CalendarService svc, CancellationToken ct) =>
            (await svc.ImportAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/calendars/{id:guid}/events", async (Guid id, CalendarEventRequest body, CalendarService svc, HttpContext http, CancellationToken ct) =>
            (await svc.CreateOnCalendarAsync(id, body, ct).ConfigureAwait(false)).ToHttp(e =>
            {
                ETags.Set(http.Response, e.Revision);
                return Results.Created($"/api/v1/calendar/events/{e.Id}", e);
            }));

        g.MapPost("/spaces/{key}/calendar/events", async (string key, CalendarEventRequest body, CalendarService svc, HttpContext http, CancellationToken ct) =>
            (await svc.CreateAsync(key, body, ct).ConfigureAwait(false)).ToHttp(e =>
            {
                ETags.Set(http.Response, e.Revision);
                return Results.Created($"/api/v1/calendar/events/{e.Id}", e);
            }));

        g.MapGet("/spaces/{key}/calendar/export", async (string key, CalendarService svc, CancellationToken ct) =>
            (await svc.ExportAsync(key, ct).ConfigureAwait(false)).ToHttp(e => Results.File(e.Data, "text/calendar; charset=utf-8", e.FileName)));

        g.MapGet("/calendar/events/{id:guid}", async (Guid id, CalendarService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(e => ETags.OkWithETag(http, e, e.Revision)));

        g.MapPut("/calendar/events/{id:guid}", async (Guid id, CalendarEventRequest body, CalendarService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.UpdateAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(e => ETags.OkWithETag(http, e, e.Revision))
                : expected.Error!.ToProblem();
        });

        g.MapPost("/calendar/events/{id:guid}/exclusions", async (Guid id, ExcludeOccurrenceRequest body, CalendarService svc, HttpContext http, CancellationToken ct) =>
            (await svc.ExcludeAsync(id, body.Date, ct).ConfigureAwait(false)).ToHttp(e => ETags.OkWithETag(http, e, e.Revision)));

        g.MapPost("/calendar/events/{id:guid}/move", async (Guid id, CalendarMoveRequest body, CalendarService svc, HttpContext http, CancellationToken ct) =>
            (await svc.MoveAsync(id, body, ct).ConfigureAwait(false)).ToHttp(e => ETags.OkWithETag(http, e, e.Revision)));

        g.MapPost("/calendar/events/{id:guid}/rsvp", async (Guid id, CalendarRsvpRequest body, CalendarService svc, HttpContext http, CancellationToken ct) =>
            (await svc.RsvpAsync(id, body, ct).ConfigureAwait(false)).ToHttp(e => ETags.OkWithETag(http, e, e.Revision)));

        g.MapDelete("/calendar/events/{id:guid}", async (Guid id, CalendarService svc, CancellationToken ct) => (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());
    }

    private static List<Guid>? ParseIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            return null;
        }

        var list = new List<Guid>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (Guid.TryParse(part, out var id))
            {
                list.Add(id);
            }
        }

        return list.Count == 0 ? null : list;
    }
}
