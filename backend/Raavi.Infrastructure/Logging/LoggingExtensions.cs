using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Raavi.Infrastructure.Logging;

/// <summary>
/// Structured JSON console logging with scopes (correlation id, tenant id once resolved). Collected by the platform log
/// pipeline (ADR-018). Never log passwords, tokens, secrets or page content (SECURITY_ARCHITECTURE.md §8).
/// OpenTelemetry exporters are wired in Phase 1 once collector endpoints are defined in sandbox/config.
/// </summary>
public static class LoggingExtensions
{
    public static IHostApplicationBuilder AddRaaviLogging(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ ";
        });
        return builder;
    }
}
