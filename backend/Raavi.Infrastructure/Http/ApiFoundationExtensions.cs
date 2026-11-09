using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace Raavi.Infrastructure.Http;

/// <summary>Cross-cutting HTTP foundation shared by every Raavi HTTP host: RFC 9457 problem details, health checks, headers.</summary>
public static class ApiFoundationExtensions
{
    public const string LiveTag = "live";
    public const string ReadyTag = "ready";

    public static IServiceCollection AddRaaviHttpFoundation(this IServiceCollection services)
    {
        // API_CONVENTIONS.md §1: camelCase JSON, enums as strings.
        services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = ctx =>
            {
                ctx.ProblemDetails.Extensions["correlationId"] = ctx.HttpContext.TraceIdentifier;
                ctx.ProblemDetails.Instance ??= ctx.HttpContext.Request.Path;
            };
        });

        services.AddExceptionHandler<DomainExceptionHandler>();
        services.AddRaaviErrorReporting();

        services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), tags: [LiveTag, ReadyTag]);

        return services;
    }

    public static WebApplication UseRaaviHttpFoundation(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();

        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = r => r.Tags.Contains(LiveTag) })
            .DisableHttpMetrics();
        app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = r => r.Tags.Contains(ReadyTag) })
            .DisableHttpMetrics();

        return app;
    }

    /// <summary>Unknown /api routes must produce a problem+json 404 rather than an empty body.</summary>
    public static RouteGroupBuilder MapRaaviApiV1(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/api/v1");
        app.MapFallback("/api/{**path}", () => Results.Problem(statusCode: StatusCodes.Status404NotFound, title: "Not Found"));
        return group;
    }
}
