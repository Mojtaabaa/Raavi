using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Tenancy.Domain;
using Raavi.Tenancy.Persistence;

namespace Raavi.Tenancy.Resolution;

/// <summary>Primary verified custom domain, else <c>slug.{first base domain}</c>; scheme/port from <c>Raavi:Hosting</c>.</summary>
public sealed class TenantUrlResolver(TenancyDbContext db, ITenantDirectory directory, IOptions<HostingOptions> hosting) : ITenantUrlResolver
{
    public Uri SystemBaseUrl => hosting.Value.TenantUrl(hosting.Value.NormalizedSystemHost);

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
        return hosting.Value.TenantUrl(primary ?? $"{tenant.Slug}.{hosting.Value.NormalizedBaseDomains[0]}");
    }
}
