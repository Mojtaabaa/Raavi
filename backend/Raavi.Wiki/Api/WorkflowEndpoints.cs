using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Application;

namespace Raavi.Wiki.Api;

/// <summary>Space workflows (docs/api/WIKI_API.md §Workflows). Tenant host only; space administrators only.</summary>
public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        g.MapGet("/workflows/catalog", () => Results.Ok(WorkflowService.Catalog()));

        g.MapGet("/spaces/{key}/workflows", async (string key, WorkflowService svc, CancellationToken ct) => (await svc.ListAsync(key, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/spaces/{key}/workflows", async (string key, WorkflowRequest body, WorkflowService svc, HttpContext http, CancellationToken ct) =>
            (await svc.CreateAsync(key, body, ct).ConfigureAwait(false)).ToHttp(d =>
            {
                ETags.Set(http.Response, d.Revision);
                return Results.Created($"/api/v1/workflows/{d.Id}", d);
            }));

        g.MapGet("/workflows/{id:guid}", async (Guid id, WorkflowService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision)));

        g.MapPut("/workflows/{id:guid}", async (Guid id, WorkflowRequest body, WorkflowService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.UpdateAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision))
                : expected.Error!.ToProblem();
        });

        g.MapDelete("/workflows/{id:guid}", async (Guid id, WorkflowService svc, CancellationToken ct) => (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());

        g.MapGet("/workflows/{id:guid}/runs", async (Guid id, int? take, WorkflowService svc, CancellationToken ct) => (await svc.RunsAsync(id, take ?? 50, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
    }
}
