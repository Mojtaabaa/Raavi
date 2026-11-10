using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Mvc;
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

        // Space export / import (ADR-051): any signed-in member; the services check space administration / permission to create spaces.
        var spaces = api.MapGroup("/spaces").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        spaces.MapGet("/{key}/export", async (string key, ExportService svc, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return (await svc.BuildSpaceAsync(key, ct).ConfigureAwait(false)).ToHttp(a => Results.File(a.Stream, "application/zip", a.FileName));
        });

        spaces.MapPost("/import", async (IFormFile file, [FromForm] string? key, [FromForm] string? name, SpaceImportService svc, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return (await svc.ImportAsync(stream, key, name, ct).ConfigureAwait(false)).ToHttp(r => Results.Ok(r));
        })
            .DisableAntiforgery()
            .WithMetadata(new RequestSizeLimitAttribute(SpaceImportService.MaxTotalBytes), new RequestFormLimitsAttribute { MultipartBodyLengthLimit = SpaceImportService.MaxTotalBytes });
    }
}
