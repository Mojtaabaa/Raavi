using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Tenancy.Domain;
using Raavi.Tenancy.Persistence;

namespace Raavi.Tenancy.Resolution;

/// <summary>Primary verified custom domain, else <c>system-host/t/slug/</c> (path addressing) or <c>slug.{first base domain}</c>; scheme/port from <c>Raavi:Hosting</c>.</summary>
public sealed class TenantUrlResolver(TenancyDbContext db, ITenantDirectory directory, IOptions<HostingOptions> hosting) : ITenantUrlResolver
{
    public Uri SystemBaseUrl => hosting.Value.TenantUrl(hosting.Value.NormalizedSystemHost);

    public string SystemApiPrefix => hosting.Value.NormalizedDefaultTenantCode.Length > 0 ? "/system" : string.Empty;

    public async ValueTask<Uri?> GetCanonicalUrlAsync(Guid tenantId, CancellationToken cancellationToken = default)
    {
        var tenant = await directory.FindByIdAsync(tenantId, cancellationToken).ConfigureAwait(false);
        if (tenant is null)
        {
            return null;
        }

        var primary = await db.TenantDomains.AsNoTracking()
            .Where(d => d.TenantId == tenantId && d.IsPrimary && d.RemovedAt == null && d.Status == TenantDomainStatus.Verified)
            .Select(d => d.Host).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (primary is not null)
        {
            return hosting.Value.TenantUrl(primary);
        }

        // Path addressing only (no wildcard DNS/TLS needed), else a subdomain of the first base domain.
        return hosting.Value.ParsedAddressing == TenantAddressing.Path || hosting.Value.NormalizedBaseDomains.Count == 0
            ? hosting.Value.TenantPathUrl(tenant.Slug)
            : hosting.Value.TenantUrl($"{tenant.Slug}.{hosting.Value.NormalizedBaseDomains[0]}");
    }
}
