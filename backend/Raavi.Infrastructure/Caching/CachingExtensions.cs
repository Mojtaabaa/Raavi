using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Caching.StackExchangeRedis;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Caching;
using Raavi.Application.Abstractions.Tenancy;

namespace Raavi.Infrastructure.Caching;

/// <summary>Key namespaces. Tenant data: <c>t:{tenantId:N}:…</c> (only via <see cref="ITenantCache"/>). System data: <c>sys:…</c>.</summary>
public static class CacheKeys
{
    public static string Tenant(Guid tenantId, string key) => $"t:{tenantId:N}:{key}";

    public static string System(string key) => $"sys:{key}";
}

public sealed class TenantCache(HybridCache cache, ITenantContext tenantContext) : ITenantCache
{
    public ValueTask<T> GetOrCreateAsync<T>(string key, Func<CancellationToken, ValueTask<T>> factory, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        ArgumentNullException.ThrowIfNull(factory);
        var options = expiration is null ? null : new HybridCacheEntryOptions { Expiration = expiration, LocalCacheExpiration = expiration };
        return cache.GetOrCreateAsync(Scoped(key), factory, static (f, ct) => f(ct), options, cancellationToken: cancellationToken);
    }

    public ValueTask RemoveAsync(string key, CancellationToken cancellationToken = default) =>
        cache.RemoveAsync(Scoped(key), cancellationToken);

    private string Scoped(string key) => CacheKeys.Tenant(tenantContext.Tenant.TenantId, key);
}

public static class CachingExtensions
{
    /// <summary>
    /// HybridCache with an in-process L1 and, when <c>ConnectionStrings:Redis</c> is set, a Redis-protocol (Valkey/Redis) L2
    /// shared by all nodes. L1 lifetime is kept short so cross-node invalidation converges quickly. Configuration is read
    /// when the cache is first resolved (not at registration), so environment and host overrides always apply.
    /// </summary>
    public static IServiceCollection AddRaaviCaching(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IDistributedCache>(sp =>
        {
            var redis = sp.GetRequiredService<IConfiguration>().GetConnectionString("Redis");
            // HybridCache treats MemoryDistributedCache as "no L2".
            return string.IsNullOrWhiteSpace(redis)
                ? new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()))
                : new RedisCache(Options.Create(new RedisCacheOptions { Configuration = redis, InstanceName = "raavi:" }));
        });

        services.AddHybridCache(o =>
        {
            o.DefaultEntryOptions = new HybridCacheEntryOptions
            {
                Expiration = TimeSpan.FromSeconds(30),
                LocalCacheExpiration = TimeSpan.FromSeconds(15),
            };
        });
        services.AddScoped<ITenantCache, TenantCache>();
        return services;
    }
}
