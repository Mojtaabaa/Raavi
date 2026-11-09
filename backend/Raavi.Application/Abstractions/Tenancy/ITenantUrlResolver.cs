namespace Raavi.Application.Abstractions.Tenancy;

/// <summary>Canonical public URL of a tenant (primary verified custom domain, else <c>slug.{first base domain}</c>). Implemented by Tenancy.</summary>
public interface ITenantUrlResolver
{
    ValueTask<Uri?> GetCanonicalUrlAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Public base URL of the system host (fixed OAuth callbacks for relay-mode providers).</summary>
    Uri SystemBaseUrl { get; }

    /// <summary>"/system" when the root of the system host belongs to a default workspace (the system API then lives under /system/api), else empty.</summary>
    string SystemApiPrefix { get; }
}
