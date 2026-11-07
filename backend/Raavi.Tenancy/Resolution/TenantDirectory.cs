using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Caching;
using Raavi.Tenancy.Domain;
using Raavi.Tenancy.Persistence;

namespace Raavi.Tenancy.Resolution;

/// <summary>
/// Cached tenant lookups (memory L1 + optional Redis L2). Negative results are cached briefly to blunt host
/// enumeration. Every lifecycle or domain change calls <see cref="InvalidateAsync"/>, so the node performing the change
/// sees it immediately and other nodes within the L1 lifetime (≤ 15 s).
/// </summary>
public sealed class TenantDirectory(TenancyDbContext db, HybridCache cache) : ITenantDirectory
{
    private static readonly HybridCacheEntryOptions Positive = new() { Expiration = TimeSpan.FromSeconds(30), LocalCacheExpiration = TimeSpan.FromSeconds(15) };

    public ValueTask<TenantSnapshot?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken = default) =>
        Lookup(IdKey(tenantId), ct => db.Tenants.AsNoTracking().Where(t => t.Id == tenantId), cancellationToken);

    public ValueTask<TenantSnapshot?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default) =>
        Lookup(SlugKey(slug), ct => db.Tenants.AsNoTracking().Where(t => t.Slug == slug), cancellationToken);

    public ValueTask<TenantSnapshot?> FindByCodeAsync(string code, CancellationToken cancellationToken = default)
    {
        var normalized = (code ?? string.Empty).Trim().ToUpperInvariant();
        return Lookup(CodeKey(normalized), ct => db.Tenants.AsNoTracking().Where(t => t.Code == normalized), cancellationToken);
    }

    public ValueTask<TenantSnapshot?> FindByVerifiedDomainAsync(string normalizedHost, CancellationToken cancellationToken = default) =>
        Lookup(
            HostKey(normalizedHost),
            ct => db.TenantDomains.AsNoTracking()
                .Where(d => d.Host == normalizedHost && d.RemovedAt == null && d.Status == TenantDomainStatus.Verified)
                .Join(db.Tenants.AsNoTracking(), d => d.TenantId, t => t.Id, (d, t) => t),
            cancellationToken);

    public async ValueTask InvalidateAsync(Guid tenantId, IEnumerable<string>? extraHosts = null, CancellationToken cancellationToken = default)
    {
        var keys = new List<string> { IdKey(tenantId) };
        var tenant = await db.Tenants.AsNoTracking().Where(t => t.Id == tenantId).Select(t => new { t.Slug, t.Code }).FirstOrDefaultAsync(cancellationToken).ConfigureAwait(false);
        if (tenant is not null)
        {
            keys.Add(SlugKey(tenant.Slug));
            keys.Add(CodeKey(tenant.Code));
        }

        var hosts = await db.TenantDomains.AsNoTracking().Where(d => d.TenantId == tenantId).Select(d => d.Host).ToListAsync(cancellationToken).ConfigureAwait(false);
        keys.AddRange(hosts.Concat(extraHosts ?? []).Distinct(StringComparer.Ordinal).Select(HostKey));
        await cache.RemoveAsync(keys, cancellationToken).ConfigureAwait(false);
    }

    private async ValueTask<TenantSnapshot?> Lookup(string key, Func<CancellationToken, IQueryable<Tenant>> query, CancellationToken cancellationToken)
    {
        var entry = await cache.GetOrCreateAsync(
            key,
            async ct =>
            {
                var snapshot = await query(ct)
                    .Select(t => new TenantSnapshot(t.Id, t.Code, t.Slug, t.Status, t.DisplayNameFa, t.DisplayNameEn))
                    .FirstOrDefaultAsync(ct).ConfigureAwait(false);
                return new CachedTenant(snapshot);
            },
            Positive,
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return entry.Tenant;
    }

    private static string IdKey(Guid id) => CacheKeys.System($"tenant:id:{id:N}");

    private static string SlugKey(string slug) => CacheKeys.System($"tenant:slug:{slug}");

    private static string CodeKey(string code) => CacheKeys.System($"tenant:code:{code}");

    private static string HostKey(string host) => CacheKeys.System($"tenant:host:{host}");

    /// <summary>Wrapper so that "not found" is cacheable too.</summary>
    public sealed record CachedTenant(TenantSnapshot? Tenant);
}
