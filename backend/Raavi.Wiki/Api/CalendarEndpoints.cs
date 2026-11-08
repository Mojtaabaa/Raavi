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

        g.MapGet("/calendar/occurrences", async (DateOnly from, DateOnly to, CalendarService svc, CancellationToken ct) =>
            (await svc.OccurrencesAsync(null, from, to, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/spaces/{key}/calendar/occurrences", async (string key, DateOnly from, DateOnly to, CalendarService svc, CancellationToken ct) =>
            (await svc.OccurrencesAsync(key, from, to, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

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

        g.MapDelete("/calendar/events/{id:guid}", async (Guid id, CalendarService svc, CancellationToken ct) => (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());
    }
}
