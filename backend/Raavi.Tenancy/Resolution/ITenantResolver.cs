using Raavi.Application.Abstractions.Tenancy;

namespace Raavi.Tenancy.Resolution;

/// <summary>
/// Resolves a tenant from a normalized host using the precedence in ADR-004:
/// system host → verified custom domain → single-label subdomain of a configured base domain.
/// Explicit identification by tenant code is not a resolution source: it exists only on the system host's discovery
/// endpoint, which redirects to the tenant's canonical host. Implementations never consult request bodies or headers.
/// </summary>
public interface ITenantResolver
{
    ValueTask<TenantResolutionResult> ResolveAsync(string normalizedHost, CancellationToken cancellationToken);

    /// <summary>Workspace named by the path prefix of a platform-host request (<c>/t/{slug}</c>). The slug must already be validated.</summary>
    ValueTask<TenantResolutionResult> ResolveSlugAsync(string slug, string normalizedHost, CancellationToken cancellationToken);

    /// <summary>The installation's default workspace for platform-host requests that name none; rejected when none is configured or it is not active.</summary>
    ValueTask<TenantResolutionResult> ResolveDefaultAsync(string normalizedHost, CancellationToken cancellationToken);
}

public enum TenantResolutionOutcome
{
    /// <summary>A single active tenant was identified.</summary>
    Resolved = 1,

    /// <summary>The host is the configured system (administration) host; no tenant context is established.</summary>
    SystemHost = 2,

    /// <summary>Unknown host, inactive tenant or unverified domain.</summary>
    Rejected = 3,
}

public sealed record TenantResolutionResult(TenantResolutionOutcome Outcome, ResolvedTenant? Tenant, string ReasonCode)
{
    public static TenantResolutionResult Rejected(string reasonCode) => new(TenantResolutionOutcome.Rejected, null, reasonCode);

    public static TenantResolutionResult System() => new(TenantResolutionOutcome.SystemHost, null, "system_host");

    public static TenantResolutionResult For(ResolvedTenant tenant) => new(TenantResolutionOutcome.Resolved, tenant, "resolved");
}
