using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Tenancy;

namespace Raavi.Tenancy.Resolution;

public sealed class TenantResolver(ITenantDirectory directory, IOptions<HostingOptions> options) : ITenantResolver
{
    public async ValueTask<TenantResolutionResult> ResolveAsync(string normalizedHost, CancellationToken cancellationToken)
    {
        var hosting = options.Value;
        if (normalizedHost == hosting.NormalizedSystemHost)
        {
            return TenantResolutionResult.System();
        }

        // 1. Verified custom domain (takes precedence; a custom domain can never live under a platform base domain).
        if (!hosting.IsPlatformHost(normalizedHost))
        {
            var byDomain = await directory.FindByVerifiedDomainAsync(normalizedHost, cancellationToken).ConfigureAwait(false);
            return byDomain is null
                ? TenantResolutionResult.Rejected("unknown_host")
                : Accept(byDomain, TenantResolutionSource.CustomDomain, normalizedHost);
        }

        // 2. Exactly one label below a configured base domain.
        foreach (var baseDomain in hosting.NormalizedBaseDomains)
        {
            if (TenantHost.TryGetSubdomainSlug(normalizedHost, baseDomain, out var slug))
            {
                var bySlug = await directory.FindBySlugAsync(slug, cancellationToken).ConfigureAwait(false);
                return bySlug is null
                    ? TenantResolutionResult.Rejected("unknown_slug")
                    : Accept(bySlug, TenantResolutionSource.Subdomain, normalizedHost);
            }
        }

        return TenantResolutionResult.Rejected("not_a_tenant_host");
    }

    private static TenantResolutionResult Accept(TenantSnapshot tenant, TenantResolutionSource source, string host) =>
        tenant.IsActive
            ? TenantResolutionResult.For(new ResolvedTenant(tenant.Id, tenant.Code, tenant.Slug, source, host))
            : TenantResolutionResult.Rejected($"tenant_{tenant.Status.ToString().ToLowerInvariant()}");
}
