using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Application;

namespace Raavi.Wiki.Api;

/// <summary>Space slide decks (docs/api/WIKI_API.md §Slides). Tenant host only.</summary>
public static class SlideEndpoints
{
    public static void MapSlideEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        g.MapGet("/spaces/{key}/slides", async (string key, SlideService svc, CancellationToken ct) => (await svc.ListAsync(key, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/spaces/{key}/slides", async (string key, SlideDeckRequest body, SlideService svc, HttpContext http, CancellationToken ct) =>
            (await svc.CreateAsync(key, body, ct).ConfigureAwait(false)).ToHttp(d =>
            {
                ETags.Set(http.Response, d.Revision);
                return Results.Created($"/api/v1/slides/{d.Id}", d);
            }));

        g.MapGet("/slides/{id:guid}", async (Guid id, SlideService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision)));

        g.MapPut("/slides/{id:guid}", async (Guid id, SlideDeckRequest body, SlideService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.UpdateAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision))
                : expected.Error!.ToProblem();
        });

        g.MapDelete("/slides/{id:guid}", async (Guid id, SlideService svc, CancellationToken ct) => (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());

        g.MapGet("/slides/{id:guid}/export", async (Guid id, SlideService svc, CancellationToken ct) =>
            (await svc.ExportPptxAsync(id, ct).ConfigureAwait(false)).ToHttp(e => Results.File(e.Data, SlideService.PptxContentType, e.FileName)));
    }
}
