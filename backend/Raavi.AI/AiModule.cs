using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Raavi.AI.Persistence;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Infrastructure.Persistence;

namespace Raavi.AI;

/// <summary>
/// AI module (Phase 11a, ADR-034): an OpenAI-compatible provider per tenant and assistance on single pages. Every AI context is assembled only
/// from content that passed the permission evaluator for the current user (<c>IPageTextReader</c>).
/// </summary>
public sealed record AskRequest(string? Question, string? Space);

public static class AiModule
{
    public const string Name = "ai";

    /// <summary>Requires the Wiki module (<c>IPageTextReader</c>), Identity (<c>IPrincipalDirectory</c>) and ASP.NET Data Protection.</summary>
    public static IServiceCollection AddAiModule(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddRaaviDbContext<AiDbContext>(configuration, AiDbContext.HistorySchema);
        services.AddOptions<AiOptions>().Bind(configuration.GetSection(AiOptions.Section));
        services.AddSingleton(sp => new AiHostPolicy(sp.GetRequiredService<IOptions<AiOptions>>().Value.AllowedHosts));
        services.AddHttpClient(AiHttp.ClientName).ConfigurePrimaryHttpMessageHandler(AiHostPolicy.CreateHandler).ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(120));
        services.AddScoped<IAiChat, OpenAiChat>();
        services.AddScoped<IAiEmbedder, OpenAiEmbedder>();
        services.AddScoped<RagIndexer>();
        services.AddScoped<RagService>();
        services.AddScoped<AiIndexJob>();
        services.AddScoped<AiService>();
        return services;
    }

    public static void MapAiEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("/ai").RequireHostKind(RaaviHostKind.Tenant);
        var members = g.MapGroup(string.Empty).RequireAuthorization(RaaviPolicies.AuthenticatedUser);
        var admin = g.MapGroup(string.Empty).RequireAuthorization(RaaviPolicies.TenantAdministrator);

        members.MapGet("/status", async (AiService svc, CancellationToken ct) => Results.Ok(await svc.StatusAsync(ct).ConfigureAwait(false)));
        members.MapPost("/pages/{id:guid}/summary", async (Guid id, AiService svc, CancellationToken ct) => (await svc.SummarizeAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        members.MapPost("/pages/{id:guid}/ask", async (Guid id, AiAskRequest body, AiService svc, CancellationToken ct) => (await svc.AskAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        // Smart search works with keywords alone (no AI needed); with an embedding model it becomes hybrid. Results are filtered by the caller's own View right.
        members.MapGet("/search", async (string? q, string? space, int? take, RagService svc, CancellationToken ct) => (await svc.SearchAsync(q, space, take ?? 10, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        members.MapPost("/ask", async (AskRequest body, RagService svc, CancellationToken ct) => (await svc.AskAsync(body.Question, body.Space, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        admin.MapGet("/index", async (RagService svc, CancellationToken ct) => Results.Ok(await svc.IndexStatusAsync(ct).ConfigureAwait(false)));
        admin.MapPost("/index/run", async (RagService svc, CancellationToken ct) => Results.Ok(await svc.StartIndexAsync(false, ct).ConfigureAwait(false)));
        admin.MapPost("/index/rebuild", async (RagService svc, CancellationToken ct) => Results.Ok(await svc.StartIndexAsync(true, ct).ConfigureAwait(false)));

        admin.MapGet("/settings", async (AiService svc, HttpContext http, CancellationToken ct) =>
        {
            var dto = await svc.GetSettingsAsync(ct).ConfigureAwait(false);
            return ETags.OkWithETag(http, dto, dto.Revision);
        });
        admin.MapPut("/settings", async (AiSettingsRequest body, AiService svc, HttpContext http, CancellationToken ct) =>
        {
            var current = await svc.GetSettingsAsync(ct).ConfigureAwait(false);
            if (current.Revision == 0)
            {
                // First save: no revision exists yet, so the client sends no precondition.
                return (await svc.PutSettingsAsync(null, body, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision));
            }

            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.PutSettingsAsync(expected.Value, body, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision))
                : expected.Error!.ToProblem();
        });
        admin.MapGet("/usage", async (AiService svc, CancellationToken ct) => Results.Ok(await svc.UsageAsync(ct).ConfigureAwait(false)));
    }
}
