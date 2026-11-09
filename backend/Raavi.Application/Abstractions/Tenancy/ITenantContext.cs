namespace Raavi.Application.Abstractions.Tenancy;

/// <summary>
/// The tenant resolved for the current unit of work (HTTP request, background job, collaboration callback).
/// It is set exactly once by trusted infrastructure (tenant resolution middleware or the job filter) and is immutable afterwards.
/// Application code must never derive the tenant from request bodies, query strings or arbitrary headers.
/// </summary>
public interface ITenantContext
{
    /// <summary>True when a tenant has been resolved. False for system-host requests and unauthenticated discovery endpoints.</summary>
    bool IsResolved { get; }

    /// <summary>The resolved tenant. Throws <see cref="TenantNotResolvedException"/> when no tenant is resolved.</summary>
    ResolvedTenant Tenant { get; }
}

/// <summary>Immutable snapshot of the resolved tenant.</summary>
/// <param name="TenantId">Internal immutable identifier used for every relationship and authorization decision.</param>
/// <param name="Code">Immutable human-facing tenant code (e.g. T001). Never used as a security boundary.</param>
/// <param name="Slug">URL identifier used for subdomains.</param>
/// <param name="ResolvedBy">How the tenant was identified.</param>
/// <param name="Host">Normalized host that produced the resolution, if any.</param>
public sealed record ResolvedTenant(Guid TenantId, string Code, string Slug, TenantResolutionSource ResolvedBy, string? Host);

public enum TenantResolutionSource
{
    CustomDomain = 1,
    Subdomain = 2,
    ExplicitDiscovery = 3,
    BackgroundJob = 4,
    ServicePrincipal = 5,

    /// <summary>Address path prefix on the platform host (<c>/t/{slug}/...</c>, ADR-045).</summary>
    PathPrefix = 6,

    /// <summary>The installation's default workspace (<c>Raavi:Hosting:DefaultTenantCode</c>) for requests that name none.</summary>
    DefaultTenant = 7,
}

public sealed class TenantNotResolvedException : InvalidOperationException
{
    public TenantNotResolvedException() : base("No tenant has been resolved for the current operation.") { }

    public TenantNotResolvedException(string message) : base(message) { }

    public TenantNotResolvedException(string message, Exception innerException) : base(message, innerException) { }
}
