using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Application;

namespace Raavi.Wiki.Api;

/// <summary>Space tasks and "My tasks" (docs/api/WIKI_API.md §Tasks). Tenant host only.</summary>
public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        g.MapGet("/spaces/{key}/tasks", async (string key, string? status, Guid? assignee, TaskService svc, CancellationToken ct) =>
            (await svc.ListAsync(key, status, assignee, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/spaces/{key}/tasks", async (string key, TaskRequest body, TaskService svc, HttpContext http, CancellationToken ct) =>
            (await svc.CreateAsync(key, body, ct).ConfigureAwait(false)).ToHttp(t =>
            {
                ETags.Set(http.Response, t.Revision);
                return Results.Created($"/api/v1/tasks/{t.Id}", t);
            }));

        g.MapGet("/spaces/{key}/tasks/export", async (string key, TaskService svc, CancellationToken ct) =>
            (await svc.ExportCsvAsync(key, ct).ConfigureAwait(false)).ToHttp(e => Results.File(e.Data, "text/csv; charset=utf-8", e.FileName)));

        g.MapGet("/tasks/mine", async (string? status, TaskService svc, CancellationToken ct) => Results.Ok(await svc.MineAsync(status, ct).ConfigureAwait(false)));

        g.MapGet("/tasks/{id:guid}", async (Guid id, TaskService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(t => ETags.OkWithETag(http, t, t.Revision)));

        g.MapPut("/tasks/{id:guid}", async (Guid id, TaskRequest body, TaskService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.UpdateAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(t => ETags.OkWithETag(http, t, t.Revision))
                : expected.Error!.ToProblem();
        });

        g.MapPost("/tasks/{id:guid}/complete", async (Guid id, TaskService svc, CancellationToken ct) => (await svc.SetDoneAsync(id, true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/tasks/{id:guid}/reopen", async (Guid id, TaskService svc, CancellationToken ct) => (await svc.SetDoneAsync(id, false, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapDelete("/tasks/{id:guid}", async (Guid id, TaskService svc, CancellationToken ct) => (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());
    }
}
