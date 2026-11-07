using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Exporter;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace Raavi.Infrastructure.Observability;

/// <summary>
/// OpenTelemetry traces and metrics (ADR-018). Exported over OTLP only when <c>Raavi:Observability:OtlpEndpoint</c>
/// is configured, so air-gapped installs without a collector run without exporter errors.
/// </summary>
public static class ObservabilityExtensions
{
    public const string ActivitySourceName = "Raavi";

    public static IHostApplicationBuilder AddRaaviObservability(this IHostApplicationBuilder builder, string serviceName)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var endpoint = builder.Configuration["Raavi:Observability:OtlpEndpoint"];

        var otel = builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName, serviceVersion: typeof(ObservabilityExtensions).Assembly.GetName().Version?.ToString()))
            .WithTracing(t => t
                .AddSource(ActivitySourceName)
                .AddSource("raavi.tenant_job")
                .AddAspNetCoreInstrumentation(o => o.Filter = ctx => !ctx.Request.Path.StartsWithSegments("/health", StringComparison.Ordinal))
                .AddHttpClientInstrumentation())
            .WithMetrics(m => m
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(ActivitySourceName));

        if (!string.IsNullOrWhiteSpace(endpoint))
        {
            otel.UseOtlpExporter(OtlpExportProtocol.Grpc, new Uri(endpoint));
        }

        return builder;
    }
}
