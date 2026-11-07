using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Raavi.Infrastructure.Persistence;
using Raavi.Migration.Confluence;
using Raavi.Migration.Persistence;

namespace Raavi.Migration;

/// <summary>Global migration settings (<c>sandbox/config/migration.json</c>, section <c>Raavi:Migration</c>).</summary>
public sealed class MigrationOptions
{
    public const string Section = "Raavi:Migration";

    /// <summary>Hosts that may be used as migration sources (exact name or <c>*.suffix</c>). Empty = migrations from remote sources are disabled.</summary>
    public IList<string> AllowedSourceHosts { get; } = [];

    public int RequestsPerSecond { get; set; } = 10;

    /// <summary>Per job; further warnings are only counted.</summary>
    public int MaxEventsPerJob { get; set; } = 2000;
}

public static class MigrationModule
{
    /// <summary>Confluence import: job state, source adapters, runner and administration API. Requires the Wiki module (<c>IContentImporter</c>) and Identity (<c>IPrincipalDirectory</c>).</summary>
    public static IServiceCollection AddMigrationModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddRaaviDbContext<MigrationDbContext>(configuration, MigrationDbContext.HistorySchema);
        services.AddOptions<MigrationOptions>().Bind(configuration.GetSection(MigrationOptions.Section));
        services.AddSingleton(sp => new SourceHostPolicy(sp.GetRequiredService<IOptions<MigrationOptions>>().Value.AllowedSourceHosts));
        services.AddSingleton<IConfluenceSourceFactory, ConfluenceSourceFactory>();
        services.AddScoped<MigrationService>();
        services.AddScoped<ConfluenceMigrationJob>();
        return services;
    }
}
