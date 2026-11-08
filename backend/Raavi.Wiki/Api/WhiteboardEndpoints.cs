using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Application;

namespace Raavi.Wiki.Api;

/// <summary>Space whiteboards (docs/api/WIKI_API.md §Whiteboards). Tenant host only.</summary>
public static class WhiteboardEndpoints
{
    public static void MapWhiteboardEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        g.MapGet("/spaces/{key}/whiteboards", async (string key, WhiteboardService svc, CancellationToken ct) => (await svc.ListAsync(key, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/spaces/{key}/whiteboards", async (string key, WhiteboardRequest body, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
            (await svc.CreateAsync(key, body, ct).ConfigureAwait(false)).ToHttp(d =>
            {
                ETags.Set(http.Response, d.Revision);
                return Results.Created($"/api/v1/whiteboards/{d.Id}", d);
            }));

        g.MapGet("/whiteboards/{id:guid}", async (Guid id, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision)));

        g.MapPut("/whiteboards/{id:guid}", async (Guid id, WhiteboardRequest body, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.UpdateAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision))
                : expected.Error!.ToProblem();
        });

        g.MapPut("/whiteboards/{id:guid}/title", async (Guid id, BoardTitleRequest body, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
            (await svc.RenameAsync(id, body, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision)));

        g.MapDelete("/whiteboards/{id:guid}", async (Guid id, WhiteboardService svc, CancellationToken ct) => (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());

        g.MapGet("/whiteboards/{id:guid}/export", async (Guid id, WhiteboardService svc, CancellationToken ct) =>
            (await svc.ExportSvgAsync(id, ct).ConfigureAwait(false)).ToHttp(e => Results.File(e.Data, "image/svg+xml", e.FileName)));
    }
}
