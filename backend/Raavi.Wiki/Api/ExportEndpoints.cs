using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Application;

namespace Raavi.Wiki.Api;

/// <summary>Workspace data export (docs/api/WIKI_API.md §Export). Tenant administrators only.</summary>
public static class ExportEndpoints
{
    public static void MapExportEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("/tenant/export").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapGet(string.Empty, async (ExportService svc, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return (await svc.BuildAsync(ct).ConfigureAwait(false)).ToHttp(a => Results.File(a.Stream, "application/zip", a.FileName));
        });
    }
}
