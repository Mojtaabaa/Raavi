using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Raavi.Infrastructure.Http;
using Raavi.Infrastructure.Tenancy;

namespace Raavi.Tenancy.Resolution;

/// <summary>
/// Establishes the immutable tenant context for every request except health probes (MULTI_TENANT_ARCHITECTURE.md §4).
/// Runs after forwarded-header processing (trusted proxies only). Unknown and inactive tenants receive the same
/// generic 404 so the response does not reveal whether a tenant exists; the precise reason is logged.
/// </summary>
public sealed partial class TenantResolutionMiddleware(RequestDelegate next, ILogger<TenantResolutionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context, ITenantResolver resolver, TenantContextHolder holder, IOptions<HostingOptions> options)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(holder);
        ArgumentNullException.ThrowIfNull(options);

        // Health probes and HMAC-authenticated service endpoints are not tenant-host requests.
        if (context.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal)
            || context.Request.Path.StartsWithSegments("/internal", StringComparison.Ordinal))
        {
            await next(context).ConfigureAwait(false);
            return;
        }

        if (!TenantHost.TryNormalize(context.Request.Host.Value, out var host))
        {
            LogRejected(logger, "invalid_host", "-");
            await Reject(context).ConfigureAwait(false);
            return;
        }

        var result = await resolver.ResolveAsync(host, context.RequestAborted).ConfigureAwait(false);
        if (result.Outcome == TenantResolutionOutcome.SystemHost)
        {
            result = await ResolvePlatformPathAsync(context, resolver, options.Value, host).ConfigureAwait(false);
        }

        switch (result.Outcome)
        {
            case TenantResolutionOutcome.SystemHost:
                if (HasTenantHint(context, options.Value))
                {
                    await Conflict(context, host, "hint_on_system_host").ConfigureAwait(false);
                    return;
                }

                context.Features.Set(new RaaviHostFeature(RaaviHostKind.System));
                await next(context).ConfigureAwait(false);
                return;

            case TenantResolutionOutcome.Resolved:
                var tenant = result.Tenant!;
                if (!HintsMatch(context, options.Value, tenant.TenantId, tenant.Code))
                {
                    await Conflict(context, host, "hint_mismatch").ConfigureAwait(false);
                    return;
                }

                holder.Set(tenant);
                context.Features.Set(new RaaviHostFeature(RaaviHostKind.Tenant));
                Activity.Current?.SetTag("raavi.tenant_id", tenant.TenantId);
                using (logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = tenant.TenantId }))
                {
                    await next(context).ConfigureAwait(false);
                }

                return;

            default:
                LogRejected(logger, result.ReasonCode, host);
                await Reject(context).ConfigureAwait(false);
                return;
        }
    }

    /// <summary>
    /// Requests on the system (platform) host (ADR-045). `/system/api/...` is the system area's API (prefix moved to PathBase so the routes stay
    /// `/api/v1/...`); `/{prefix}/{slug}/...` selects a workspace when path addressing is on; any other request goes to the default workspace when one
    /// is configured, else to the system area. The path is rewritten here, before routing; nothing outside the request path and the configuration decides.
    /// </summary>
    private static async ValueTask<TenantResolutionResult> ResolvePlatformPathAsync(HttpContext context, ITenantResolver resolver, HostingOptions hosting, string host)
    {
        var request = context.Request;
        if (request.Path.StartsWithSegments("/system/api", StringComparison.Ordinal, out var systemRest))
        {
            request.PathBase = request.PathBase.Add("/system");
            request.Path = "/api" + systemRest.Value;
            return TenantResolutionResult.System();
        }

        if (hosting.AcceptsPaths && TryMatchPathTenant(request.Path, hosting.NormalizedPathPrefix, out var slug, out var rest))
        {
            request.PathBase = request.PathBase.Add($"/{hosting.NormalizedPathPrefix}/{slug}");
            request.Path = rest;
            return await resolver.ResolveSlugAsync(slug, host, context.RequestAborted).ConfigureAwait(false);
        }

        if (hosting.NormalizedDefaultTenantCode.Length > 0)
        {
            return await resolver.ResolveDefaultAsync(host, context.RequestAborted).ConfigureAwait(false);
        }

        return TenantResolutionResult.System();
    }

    /// <summary>Matches <c>/{prefix}/{slug}</c> or <c>/{prefix}/{slug}/rest</c>; the slug is lower-case DNS-label syntax (it is compared with stored slugs, never used as a path).</summary>
    public static bool TryMatchPathTenant(PathString path, string prefix, out string slug, out PathString rest)
    {
        slug = string.Empty;
        rest = PathString.Empty;
        var value = path.Value;
        if (value is null || !value.StartsWith('/'))
        {
            return false;
        }

        var segments = value.Split('/', 4);
        // "" , prefix , slug [, rest]
        if (segments.Length < 3 || !string.Equals(segments[1], prefix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var candidate = segments[2].ToLowerInvariant();
        if (candidate.Length is 0 or > 63 || candidate[0] == '-' || candidate[^1] == '-' || !candidate.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
        {
            return false;
        }

        slug = candidate;
        rest = segments.Length == 4 ? new PathString("/" + segments[3]) : new PathString("/");
        return true;
    }

    private static bool HasTenantHint(HttpContext context, HostingOptions options) =>
        options.TenantHintHeaders.Any(h => context.Request.Headers.ContainsKey(h));

    /// <summary>Hint headers never select a tenant. If present they must name the resolved tenant (id or code); otherwise the request is rejected.</summary>
    private static bool HintsMatch(HttpContext context, HostingOptions options, Guid tenantId, string code)
    {
        foreach (var header in options.TenantHintHeaders)
        {
            if (!context.Request.Headers.TryGetValue(header, out var values))
            {
                continue;
            }

            foreach (var value in values)
            {
                var v = value?.Trim() ?? string.Empty;
                var matches = (Guid.TryParse(v, out var id) && id == tenantId) || string.Equals(v, code, StringComparison.OrdinalIgnoreCase);
                if (!matches)
                {
                    return false;
                }
            }
        }

        return true;
    }

    private async Task Conflict(HttpContext context, string host, string reason)
    {
        LogConflict(logger, reason, host);
        await Results.Problem(
                statusCode: StatusCodes.Status400BadRequest,
                title: "Conflicting tenant identification",
                extensions: new Dictionary<string, object?> { ["code"] = "tenant.conflict", ["correlationId"] = context.TraceIdentifier })
            .ExecuteAsync(context).ConfigureAwait(false);
    }

    private static Task Reject(HttpContext context) =>
        Results.Problem(
                statusCode: StatusCodes.Status404NotFound,
                title: "Workspace not found",
                extensions: new Dictionary<string, object?> { ["code"] = "tenant.unavailable", ["correlationId"] = context.TraceIdentifier })
            .ExecuteAsync(context);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant resolution rejected ({Reason}) for host {Host}")]
    private static partial void LogRejected(ILogger logger, string reason, string host);

    [LoggerMessage(EventId = 4001, Level = LogLevel.Warning, Message = "SECURITY tenant identification conflict ({Reason}) on host {Host}")]
    private static partial void LogConflict(ILogger logger, string reason, string host);
}
