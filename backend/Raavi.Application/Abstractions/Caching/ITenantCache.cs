namespace Raavi.Application.Abstractions.Caching;

/// <summary>
/// The only cache feature code may use for tenant data. Keys are automatically prefixed with the resolved tenant
/// (<c>t:{tenantId}:</c>); calling it without a resolved tenant throws (TENANT_ISOLATION.md §2, layer 8).
/// </summary>
public interface ITenantCache
{
    ValueTask<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, TimeSpan? expiration = null, CancellationToken cancellationToken = default);

    ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default);
}
