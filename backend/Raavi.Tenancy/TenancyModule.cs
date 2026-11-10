using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Http;
using Raavi.Infrastructure.Persistence;
using Raavi.Tenancy.Application;
using Raavi.Tenancy.Persistence;
using Raavi.Tenancy.Resolution;

namespace Raavi.Tenancy;

public static class TenancyModule
{
    public static IServiceCollection AddTenancyModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<HostingOptions>().Bind(configuration.GetSection(HostingOptions.Section)).ValidateOnStart();
        services.AddSingleton<IPostConfigureOptions<HostingOptions>, HostingOptionsSetup>();
        services.AddOptions<DomainVerificationOptions>().Bind(configuration.GetSection(DomainVerificationOptions.Section));

        services.AddRaaviDbContext<TenancyDbContext>(configuration, TenancyDbContext.Schema);

        services.AddScoped<ITenantDirectory, TenantDirectory>();
        services.AddScoped<ITenantResolver, TenantResolver>();
        services.AddScoped<ITenantUrlResolver, TenantUrlResolver>();
        services.AddScoped<TenancyAudit>();
        services.AddScoped<TenantAdministrationService>();
        services.AddScoped<TenantDomainService>();
        services.AddScoped<TenantDeletionService>();
        services.AddScoped<TenantPurgeJob>();
        services.AddScoped<TenantConfigurationService>();
        services.AddScoped<DomainVerificationJob>();
        services.AddSingleton<IDnsTxtResolver, DnsClientTxtResolver>();

        services.AddSingleton<ErrorLogWriter>();
        services.AddSingleton<IErrorSink>(sp => sp.GetRequiredService<ErrorLogWriter>());
        services.AddHostedService(sp => sp.GetRequiredService<ErrorLogWriter>());
        services.AddScoped<ErrorLogService>();

        services.AddHealthChecks().AddCheck<TenancyDatabaseHealthCheck>("sql", tags: [ApiFoundationExtensions.ReadyTag]);
        return services;
    }

    /// <summary>Must run after forwarded headers and before authentication.</summary>
    public static IApplicationBuilder UseTenantResolution(this IApplicationBuilder app) =>
        app.UseMiddleware<TenantResolutionMiddleware>();
}

internal sealed class TenancyDatabaseHealthCheck(TenancyDbContext db) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            return await db.Database.CanConnectAsync(cancellationToken).ConfigureAwait(false)
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy("SQL Server not reachable");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("SQL Server not reachable");
        }
    }
}
