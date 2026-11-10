using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Http;
using Raavi.Tenancy.Application;
using Raavi.Tenancy.Persistence;
using Raavi.Tenancy.Resolution;
using Microsoft.EntityFrameworkCore;

namespace Raavi.Tenancy.Api;

public static class TenancyEndpoints
{
    public const string DiscoveryRateLimitPolicy = "raavi.discovery";
    public const string ClientErrorRateLimitPolicy = "raavi.client-errors";

    public static void MapTenancyEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        // ── Any host: SPA bootstrap. Tenancy: anonymous-tenant / system. ─────────────────────────────
        api.MapGet("/host/context", async (HttpContext http, TenantConfigurationService config, CancellationToken ct) =>
            http.GetHostKind() == RaaviHostKind.Tenant
                ? Results.Ok(new HostContextDto("tenant", await config.GetPublicContextAsync(ct).ConfigureAwait(false)))
                : Results.Ok(new HostContextDto("system", null)))
            .WithName("GetHostContext")
            .AllowAnonymous();

        // ── A workspace host: its square logo (public like the branding; version in the address -> immutable cache). ──
        api.MapGet("/host/logo", async (HttpContext http, TenantConfigurationService config, CancellationToken ct) =>
            http.GetHostKind() == RaaviHostKind.Tenant ? LogoResult(http, await config.GetLogoAsync(ct).ConfigureAwait(false)) : Results.NotFound())
            .WithName("GetHostLogo")
            .AllowAnonymous();

        // ── Errors the browser reports (anonymous, rate limited, size limited): they join the error log as source "client". ──
        api.MapPost("/errors/client", (ClientErrorRequest body, HttpContext http, IErrorSink sink, Raavi.Application.Abstractions.Tenancy.ITenantContext tenant) =>
            {
                string? code = null;
                try
                {
                    code = tenant.IsResolved ? tenant.Tenant.Code : null;
                }
                catch (Exception)
                {
                    // system host
                }

                sink.Report(new ErrorReport(
                    DateTimeOffset.UtcNow,
                    "client",
                    code,
                    null,
                    "GET",
                    ErrorText.Clean(body.Url, 300),
                    null,
                    ErrorText.Clean(body.Type, 200),
                    ErrorText.Clean(body.Message, ErrorText.MaxMessage),
                    ErrorText.Clean(body.Stack, ErrorText.MaxStack),
                    http.TraceIdentifier,
                    ErrorText.Clean(http.Request.Headers.UserAgent.ToString(), 200)));
                return Results.NoContent();
            })
            .WithName("ReportClientError")
            .RequireRateLimiting(ClientErrorRateLimitPolicy)
            .AllowAnonymous();

        // ── System host, system administrators: the error log. ──
        var errors = api.MapGroup("/system/errors")
            .RequireHostKind(RaaviHostKind.System)
            .RequireAuthorization(RaaviPolicies.SystemAdministrator);

        errors.MapGet("/", async (string? q, string? source, DateTimeOffset? from, DateTimeOffset? to, string? fingerprint, int? skip, int? take, ErrorLogService svc, CancellationToken ct) =>
            Results.Ok(await svc.SearchAsync(q, source, from, to, fingerprint, skip ?? 0, take ?? 50, ct).ConfigureAwait(false)));

        errors.MapGet("/summary", async (int? days, ErrorLogService svc, CancellationToken ct) =>
            Results.Ok(await svc.SummaryAsync(days ?? 14, ct).ConfigureAwait(false)));

        errors.MapGet("/{id:guid}", async (Guid id, ErrorLogService svc, CancellationToken ct) =>
            (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        errors.MapPost("/purge", async (PurgeErrorsRequest body, ErrorLogService svc, CancellationToken ct) =>
            Results.Ok(new { deleted = await svc.PurgeAsync(body.OlderThanDays, ct).ConfigureAwait(false) }));

        // ── Tenant host, workspace administrators: time zone, calendar and default language. ──
        var own = api.MapGroup("/tenant/settings").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.TenantAdministrator);
        own.MapGet(string.Empty, async (TenantConfigurationService svc, CancellationToken ct) => (await svc.GetSettingsAsync(ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        own.MapPut(string.Empty, async (UpdateDisplayRequest body, TenantConfigurationService svc, ITenantDirectory directory, ITenantContext tenant, CancellationToken ct) =>
        {
            var result = await svc.UpdateDisplayAsync(body, ct).ConfigureAwait(false);
            if (result.IsSuccess)
            {
                await directory.InvalidateAsync(tenant.Tenant.TenantId, cancellationToken: ct).ConfigureAwait(false);
            }

            return result.ToHttp(Results.Ok);
        });

        // ── System host, anonymous, rate-limited: login discovery by tenant code. ───────────────────
        api.MapGet("/discovery/tenants/{code}", async (string code, ITenantDirectory directory, ITenantUrlResolver urls, CancellationToken ct) =>
            {
                var tenant = await directory.FindByCodeAsync(code, ct).ConfigureAwait(false);
                var url = tenant is { IsActive: true } ? await urls.GetCanonicalUrlAsync(tenant.Id, ct).ConfigureAwait(false) : null;
                return url is null
                    ? Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Workspace not found", extensions: new Dictionary<string, object?> { ["code"] = "tenant.unavailable" })
                    : Results.Ok(new DiscoveryResultDto(url));
            })
            .WithName("DiscoverTenant")
            .RequireHostKind(RaaviHostKind.System)
            .RequireRateLimiting(DiscoveryRateLimitPolicy)
            .AllowAnonymous();

        // ── System host, system administrators only. ─────────────────────────────────────────────
        var system = api.MapGroup("/system/tenants")
            .RequireHostKind(RaaviHostKind.System)
            .RequireAuthorization(RaaviPolicies.SystemAdministrator);

        system.MapPost("/", async (CreateTenantRequest body, TenantAdministrationService svc, CancellationToken ct) =>
            (await svc.CreateAsync(body, ct).ConfigureAwait(false)).ToHttp(t => Results.Created($"/api/v1/system/tenants/{t.Id}", t)));

        system.MapGet("/", async (TenantStatus? status, int? skip, int? take, TenantAdministrationService svc, ITenantUrlResolver urls, CancellationToken ct) =>
        {
            var page = await svc.ListAsync(status, skip ?? 0, take ?? 50, ct).ConfigureAwait(false);
            var items = new List<TenantDto>(page.Items.Count);
            foreach (var t in page.Items)
            {
                var url = await urls.GetCanonicalUrlAsync(t.Id, ct).ConfigureAwait(false);
                items.Add(t with { Url = url?.ToString() });
            }

            return Results.Ok(new PagedResult<TenantDto>(items, page.Total, page.Skip, page.Take));
        });

        system.MapGet("/{tenantId:guid}", async (Guid tenantId, TenantAdministrationService svc, CancellationToken ct) =>
            (await svc.GetAsync(tenantId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        system.MapPost("/{tenantId:guid}/provision", async (Guid tenantId, TenantAdministrationService svc, CancellationToken ct) =>
            (await svc.ProvisionAsync(tenantId, null, null, null, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        system.MapPost("/{tenantId:guid}/activate", async (Guid tenantId, TenantAdministrationService svc, CancellationToken ct) =>
            (await svc.ActivateAsync(tenantId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        system.MapPost("/{tenantId:guid}/suspend", async (Guid tenantId, ChangeStatusRequest body, TenantAdministrationService svc, CancellationToken ct) =>
            (await svc.SuspendAsync(tenantId, body.Reason, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        system.MapPost("/{tenantId:guid}/deactivate", async (Guid tenantId, ChangeStatusRequest body, TenantAdministrationService svc, CancellationToken ct) =>
            (await svc.DeactivateAsync(tenantId, body.Reason, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        system.MapPost("/{tenantId:guid}/delete-request", async (Guid tenantId, RequestDeletionRequest body, TenantDeletionService svc, CancellationToken ct) =>
            (await svc.RequestAsync(tenantId, body.Reason, body.ConfirmCode, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        system.MapPost("/{tenantId:guid}/cancel-deletion", async (Guid tenantId, TenantDeletionService svc, CancellationToken ct) =>
            (await svc.CancelAsync(tenantId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        system.MapPost("/{tenantId:guid}/purge", async (Guid tenantId, PurgeTenantRequest body, TenantDeletionService svc, CancellationToken ct) =>
            (await svc.PurgeNowAsync(tenantId, body.ConfirmCode, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        system.MapGet("/{tenantId:guid}/domains", async (Guid tenantId, TenantDomainService svc, CancellationToken ct) =>
            (await svc.ListAsync(tenantId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        system.MapPost("/{tenantId:guid}/domains", async (Guid tenantId, AddDomainRequest body, TenantDomainService svc, CancellationToken ct) =>
            (await svc.AddAsync(tenantId, body.Host, ct).ConfigureAwait(false)).ToHttp(d => Results.Created($"/api/v1/system/tenants/{tenantId}/domains/{d.Id}", d)));

        system.MapPost("/{tenantId:guid}/domains/{domainId:guid}/verify", async (Guid tenantId, Guid domainId, TenantDomainService svc, CancellationToken ct) =>
            (await svc.VerifyAsync(tenantId, domainId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        system.MapPost("/{tenantId:guid}/domains/{domainId:guid}/primary", async (Guid tenantId, Guid domainId, TenantDomainService svc, CancellationToken ct) =>
            (await svc.MakePrimaryAsync(tenantId, domainId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        system.MapDelete("/{tenantId:guid}/domains/{domainId:guid}", async (Guid tenantId, Guid domainId, TenantDomainService svc, CancellationToken ct) =>
            (await svc.RemoveAsync(tenantId, domainId, ct).ConfigureAwait(false)).ToHttp());

        system.MapGet("/{tenantId:guid}/settings", (Guid tenantId, IServiceProvider sp, CancellationToken ct) =>
            Run(sp, tenantId, s => s.GetSettingsAsync(ct), ct));

        system.MapPut("/{tenantId:guid}/settings", (Guid tenantId, UpdateSettingsRequest body, IServiceProvider sp, CancellationToken ct) =>
            Run(sp, tenantId, s => s.UpdateSettingsAsync(body, ct), ct));

        system.MapPut("/{tenantId:guid}/logo", async (Guid tenantId, IFormFile file, IServiceProvider sp, CancellationToken ct) =>
            {
                await using var stream = file.OpenReadStream();
                return await Run(sp, tenantId, s => s.SetLogoAsync(stream, file.Length, ct), ct).ConfigureAwait(false);
            })
            .DisableAntiforgery();

        system.MapDelete("/{tenantId:guid}/logo", (Guid tenantId, IServiceProvider sp, CancellationToken ct) =>
            Run(sp, tenantId, s => s.RemoveLogoAsync(ct), ct));

        system.MapPut("/{tenantId:guid}/branding", (Guid tenantId, UpdateBrandingRequest body, IServiceProvider sp, CancellationToken ct) =>
            Run(sp, tenantId, s => s.UpdateBrandingAsync(body, ct), ct));
    }

    private static IResult LogoResult(HttpContext http, LogoFile? logo)
    {
        if (logo is null)
        {
            return Results.NotFound();
        }

        var etag = "\"l" + logo.Version + "\"";
        http.Response.Headers.ETag = etag;
        http.Response.Headers.CacheControl = "public, max-age=3600";
        http.Response.Headers["X-Content-Type-Options"] = "nosniff";
        return http.Request.Headers.IfNoneMatch == etag ? Results.StatusCode(StatusCodes.Status304NotModified) : Results.File(logo.Data, logo.ContentType);
    }

    private static async Task<IResult> Run<T>(IServiceProvider sp, Guid tenantId, Func<TenantConfigurationService, Task<Raavi.Application.Common.Result<T>>> action, CancellationToken ct) =>
        (await TenantConfigurationService.ForTenantAsync(sp, tenantId, action, ct).ConfigureAwait(false)).ToHttp(v => Results.Ok(v));
}
