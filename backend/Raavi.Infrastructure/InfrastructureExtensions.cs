using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Infrastructure.Caching;
using Raavi.Infrastructure.Email;
using Raavi.Infrastructure.Http;
using Raavi.Infrastructure.Tenancy;

namespace Raavi.Infrastructure;

public sealed class SystemClock : IClock
{
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

public static class InfrastructureExtensions
{
    /// <summary>Core services every host needs: tenant context, tenant scopes, caching, clock, audit actor, e-mail.</summary>
    public static IServiceCollection AddRaaviInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddScoped<TenantContextHolder>();
        services.AddScoped<ITenantContext>(sp => sp.GetRequiredService<TenantContextHolder>());
        services.AddSingleton<ITenantScopeFactory, TenantScopeFactory>();
        services.AddSingleton<IClock, SystemClock>();
        services.AddHttpContextAccessor();
        services.AddSingleton<IAuditActorAccessor, HttpAuditActorAccessor>();
        services.AddRaaviCaching(configuration);
        services.AddRaaviEmail(configuration);
        return services;
    }
}
