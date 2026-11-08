using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Application;

namespace Raavi.Wiki.Api;

/// <summary>Space databases (docs/api/WIKI_API.md §Databases). Tenant host only.</summary>
public static class DatabaseEndpoints
{
    public static void MapDatabaseEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        g.MapGet("/spaces/{key}/databases", async (string key, DatabaseService svc, CancellationToken ct) => (await svc.ListAsync(key, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/spaces/{key}/databases", async (string key, DatabaseRequest body, DatabaseService svc, HttpContext http, CancellationToken ct) =>
            (await svc.CreateAsync(key, body, ct).ConfigureAwait(false)).ToHttp(d =>
            {
                ETags.Set(http.Response, d.Revision);
                return Results.Created($"/api/v1/databases/{d.Id}", d);
            }));

        g.MapGet("/databases/{id:guid}", async (Guid id, DatabaseService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision)));

        g.MapPut("/databases/{id:guid}", async (Guid id, DatabaseRequest body, DatabaseService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.UpdateAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision))
                : expected.Error!.ToProblem();
        });

        g.MapDelete("/databases/{id:guid}", async (Guid id, DatabaseService svc, CancellationToken ct) => (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());

        g.MapGet("/databases/{id:guid}/rows", async (Guid id, string? q, string? sort, string? dir, int? skip, int? take, DatabaseService svc, CancellationToken ct) =>
            (await svc.RowsAsync(id, q, sort, string.Equals(dir, "desc", StringComparison.OrdinalIgnoreCase), skip ?? 0, take ?? 100, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/databases/{id:guid}/rows", async (Guid id, DatabaseRowRequest body, DatabaseService svc, HttpContext http, CancellationToken ct) =>
            (await svc.AddRowAsync(id, body, ct).ConfigureAwait(false)).ToHttp(r =>
            {
                ETags.Set(http.Response, r.Revision);
                return Results.Created($"/api/v1/database-rows/{r.Id}", r);
            }));

        g.MapGet("/databases/{id:guid}/export", async (Guid id, DatabaseService svc, CancellationToken ct) =>
            (await svc.ExportCsvAsync(id, ct).ConfigureAwait(false)).ToHttp(e => Results.File(e.Data, "text/csv; charset=utf-8", e.FileName)));

        g.MapGet("/database-rows/{id:guid}", async (Guid id, DatabaseService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetRowAsync(id, ct).ConfigureAwait(false)).ToHttp(r => ETags.OkWithETag(http, r, r.Revision)));

        g.MapPut("/database-rows/{id:guid}", async (Guid id, DatabaseRowRequest body, DatabaseService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.UpdateRowAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(r => ETags.OkWithETag(http, r, r.Revision))
                : expected.Error!.ToProblem();
        });

        g.MapDelete("/database-rows/{id:guid}", async (Guid id, DatabaseService svc, CancellationToken ct) => (await svc.DeleteRowAsync(id, ct).ConfigureAwait(false)).ToHttp());
    }
}
