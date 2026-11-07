using Hangfire;
using Hangfire.SqlServer;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Jobs;
using Raavi.Infrastructure.Persistence;

namespace Raavi.Infrastructure.Jobs;

public static class HangfireExtensions
{
    public const string Schema = "hangfire";
    public static readonly string[] Queues = ["critical", "default", "indexing", "import", "ai", "maintenance"];

    /// <summary>
    /// Hangfire with SQL Server storage in schema <c>hangfire</c>. The schema is installed by the migrator
    /// (<see cref="InstallSchema"/>), never by application hosts, because the application login has no DDL rights.
    /// </summary>
    public static IServiceCollection AddRaaviJobs(this IServiceCollection services, IConfiguration configuration, bool runServer)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHangfire((sp, c) =>
        {
            var config = sp.GetRequiredService<IConfiguration>();
            var cs = config.GetConnectionString("Hangfire");
            if (string.IsNullOrWhiteSpace(cs))
            {
                cs = config.GetSystemConnectionString();
            }

            c.SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseSqlServerStorage(cs, new SqlServerStorageOptions
                {
                    SchemaName = Schema,
                    PrepareSchemaIfNecessary = false,
                    QueuePollInterval = TimeSpan.FromSeconds(5),
                });
        });

        services.AddScoped(typeof(TenantJobRunner<,>));
        services.AddScoped<ITenantJobScheduler, HangfireTenantJobScheduler>();

        if (runServer)
        {
            services.AddHangfireServer(o => o.Queues = Queues);
        }

        return services;
    }

    public static void InstallSchema(string connectionString)
    {
        using var connection = new SqlConnection(connectionString);
        connection.Open();
        SqlServerObjectsInstaller.Install(connection, Schema, enableHeavyMigrations: true);
    }
}
