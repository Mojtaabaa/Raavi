using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Tenancy;

namespace Raavi.Infrastructure.Persistence;

public sealed class DatabaseOptions
{
    public const string Section = "Raavi:Database";

    /// <summary>SQL Server compatibility level EF Core generates SQL for. 160 = SQL Server 2022 (product floor). Tests on older engines lower it.</summary>
    public int CompatibilityLevel { get; set; } = 160;

    public int CommandTimeoutSeconds { get; set; } = 30;
}

public static class PersistenceExtensions
{
    /// <summary>Connection string used by the application (DML only, RLS enforced). Never the migrator login in production.</summary>
    public static string GetSystemConnectionString(this IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var cs = configuration.GetConnectionString("System");
        return string.IsNullOrWhiteSpace(cs)
            ? throw new InvalidOperationException("ConnectionStrings:System is not configured (set RAAVI_ConnectionStrings__System).")
            : cs;
    }

    /// <summary>
    /// Registers a module DbContext with the tenant interceptors. Every module context must be registered through this
    /// method so it cannot opt out of tenant enforcement.
    /// </summary>
    public static IServiceCollection AddRaaviDbContext<TContext>(
        this IServiceCollection services,
        IConfiguration configuration,
        string migrationsHistorySchema,
        string? connectionString = null)
        where TContext : RaaviDbContextBase
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddDbContext<TContext>((sp, builder) =>
        {
            // Read from the built configuration so environment / host overrides always apply.
            var config = sp.GetRequiredService<IConfiguration>();
            var options = config.GetSection(DatabaseOptions.Section).Get<DatabaseOptions>() ?? new DatabaseOptions();
            var tenant = sp.GetRequiredService<ITenantContext>();
            builder.UseSqlServer(connectionString ?? config.GetSystemConnectionString(), sql =>
            {
                sql.UseCompatibilityLevel(options.CompatibilityLevel);
                sql.CommandTimeout(options.CommandTimeoutSeconds);
                sql.MigrationsHistoryTable(HistoryRepository.DefaultTableName, migrationsHistorySchema);
                sql.MigrationsAssembly(typeof(TContext).Assembly.GetName().Name);
            });
            builder.AddInterceptors(new TenantSessionContextInterceptor(tenant), new TenantStampingInterceptor(tenant));
        });
        return services;
    }
}
