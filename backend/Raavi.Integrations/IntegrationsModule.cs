using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Infrastructure.Messaging;
using Raavi.Infrastructure.Persistence;
using Raavi.Integrations.Connectors;
using Raavi.Integrations.Persistence;
using Raavi.Integrations.Webhooks;

namespace Raavi.Integrations;

/// <summary>
/// Integrations module (Phase 11, 13): outbound webhooks (ADR-036), issue connectors (ADR-043) and Jira Page–Issue linking.
/// Outbound calls go through an SSRF-guarded HTTP client (SECURITY_ARCHITECTURE.md §5).
/// </summary>
public static class IntegrationsModule
{
    public const string Name = "integrations";

    /// <summary>Requires ASP.NET Data Protection. <paramref name="runOutboxPoller"/> only in the worker host (integrations outbox for Jira relationship events).</summary>
    public static IServiceCollection AddIntegrationsModule(this IServiceCollection services, IConfiguration configuration, bool runOutboxPoller = false)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        services.AddRaaviDbContext<IntegrationsDbContext>(configuration, IntegrationsDbContext.HistorySchema);
        services.AddRaaviOutbox<IntegrationsDbContext>(runOutboxPoller);
        services.AddOptions<IntegrationsOptions>().Bind(configuration.GetSection(IntegrationsOptions.Section));
        services.AddSingleton(sp => new WebhookHostPolicy(sp.GetRequiredService<IOptions<IntegrationsOptions>>().Value.AllowedWebhookHosts));
        services.AddHttpClient(WebhookHttp.ClientName).ConfigurePrimaryHttpMessageHandler(WebhookHostPolicy.CreateHandler).ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(90));
        services.AddMemoryCache();
        services.AddSingleton(sp => new ConnectorHostPolicy(sp.GetRequiredService<IOptions<IntegrationsOptions>>().Value.AllowedConnectorHosts));
        services.AddHttpClient(ConnectorHttp.ClientName).ConfigurePrimaryHttpMessageHandler(WebhookHostPolicy.CreateHandler).ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(90));
        services.AddScoped<IssueConnectorService>();
        services.AddScoped<IssueLookupService>();
        services.AddScoped<JiraIssueClient>();
        services.AddScoped<Raavi.Application.Abstractions.Calendar.ICalendarOverlaySource, JiraCalendarOverlay>();
        services.AddScoped<PageIssueLinkService>();
        services.AddScoped<PageIssueRemoteLinkJob>();
        services.AddScoped<Raavi.Application.Abstractions.Ai.IIssueContextReader, IssueContextReader>();
        services.AddScoped<WebhookSender>();
        services.AddScoped<WebhookService>();
        services.AddScoped<IOutboxHandler, WebhookDispatcher>();
        services.AddScoped<IOutboxHandler, PageIssueLinkOutboxHandler>();
        return services;
    }

    public static void MapIntegrationsEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("/integrations/webhooks").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapGet("/catalog", (WebhookService svc) => Results.Ok(svc.Catalog()));
        g.MapGet(string.Empty, async (WebhookService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct).ConfigureAwait(false)));
        g.MapPost(string.Empty, async (WebhookRequest body, WebhookService svc, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store"; // the response carries the signing secret
            return (await svc.CreateAsync(body, ct).ConfigureAwait(false)).ToHttp(d => Results.Created($"/api/v1/integrations/webhooks/{d.Webhook.Id}", d));
        });
        g.MapGet("/{id:guid}", async (Guid id, WebhookService svc, HttpContext http, CancellationToken ct) => (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision)));
        g.MapPut("/{id:guid}", async (Guid id, WebhookRequest body, WebhookService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.UpdateAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision))
                : expected.Error!.ToProblem();
        });
        g.MapPost("/{id:guid}/rotate-secret", async (Guid id, WebhookService svc, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            return (await svc.RotateSecretAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok);
        });
        g.MapPost("/{id:guid}/test", async (Guid id, WebhookService svc, CancellationToken ct) => (await svc.TestAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapGet("/{id:guid}/deliveries", async (Guid id, int? take, WebhookService svc, CancellationToken ct) => (await svc.DeliveriesAsync(id, take ?? 50, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapDelete("/{id:guid}", async (Guid id, WebhookService svc, CancellationToken ct) => (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());

        var connectors = api.MapGroup("/integrations/connectors").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.TenantAdministrator);
        connectors.MapGet("/catalog", (IssueConnectorService svc) => Results.Ok(svc.Catalog()));
        connectors.MapGet(string.Empty, async (IssueConnectorService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct).ConfigureAwait(false)));
        connectors.MapPost(string.Empty, async (ConnectorRequest body, IssueConnectorService svc, CancellationToken ct) =>
            (await svc.CreateAsync(body, ct).ConfigureAwait(false)).ToHttp(d => Results.Created($"/api/v1/integrations/connectors/{d.Id}", d)));
        connectors.MapGet("/{id:guid}", async (Guid id, IssueConnectorService svc, HttpContext http, CancellationToken ct) => (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision)));
        connectors.MapPut("/{id:guid}", async (Guid id, ConnectorRequest body, IssueConnectorService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.UpdateAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision))
                : expected.Error!.ToProblem();
        });
        connectors.MapDelete("/{id:guid}", async (Guid id, IssueConnectorService svc, CancellationToken ct) => (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());

        // Members: resolve an issue reference to title/status/link through an enabled connector.
        var lookup = api.MapGroup("/integrations/issues").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);
        lookup.MapGet("/connectors", async (IssueConnectorService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListAsync(ct).ConfigureAwait(false)).Where(c => c.Enabled).Select(c => new { c.Id, c.Name, c.Kind })));
        lookup.MapGet("/lookup", async (Guid connector, string reference, IssueLookupService svc, CancellationToken ct) =>
            (await svc.LookupAsync(connector, reference, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        var jira = api.MapGroup("/integrations/jira").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);
        jira.MapGet("/connectors", async (IssueConnectorService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListAsync(ct).ConfigureAwait(false)).Where(c => c.Enabled && c.Kind == "Jira").Select(c => new { c.Id, c.Name })));
        jira.MapGet("/connectors/{id:guid}/projects", async (Guid id, IssueConnectorService connectors, JiraIssueClient client, IntegrationsDbContext db, CancellationToken ct) =>
        {
            var c = await db.IssueConnectors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            return c is null
                ? IssueConnectorService.NotFound.ToProblem()
                : (await client.ProjectsAsync(c, ct).ConfigureAwait(false)).ToHttp(Results.Ok);
        });
        jira.MapGet("/connectors/{id:guid}/issuetypes", async (Guid id, string project, JiraIssueClient client, IntegrationsDbContext db, CancellationToken ct) =>
        {
            var c = await db.IssueConnectors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            return c is null
                ? IssueConnectorService.NotFound.ToProblem()
                : (await client.IssueTypesAsync(c, project, ct).ConfigureAwait(false)).ToHttp(Results.Ok);
        });
        jira.MapGet("/connectors/{id:guid}/createmeta", async (Guid id, string project, string issuetype, JiraIssueClient client, IntegrationsDbContext db, CancellationToken ct) =>
        {
            var c = await db.IssueConnectors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            return c is null
                ? IssueConnectorService.NotFound.ToProblem()
                : (await client.CreateMetaAsync(c, project, issuetype, ct).ConfigureAwait(false)).ToHttp(Results.Ok);
        });
        jira.MapGet("/connectors/{id:guid}/search", async (Guid id, string q, JiraIssueClient client, IntegrationsDbContext db, CancellationToken ct) =>
        {
            var c = await db.IssueConnectors.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
            return c is null
                ? IssueConnectorService.NotFound.ToProblem()
                : (await client.SearchAsync(c, q, ct).ConfigureAwait(false)).ToHttp(Results.Ok);
        });
        jira.MapGet("/issues/{connectorId:guid}/{issueId}/pages", async (Guid connectorId, string issueId, PageIssueLinkService svc, CancellationToken ct) =>
            (await svc.PagesForIssueAsync(connectorId, issueId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        var pageLinks = api.MapGroup("/pages/{contentId:guid}/jira-links").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);
        pageLinks.MapGet(string.Empty, async (Guid contentId, PageIssueLinkService svc, CancellationToken ct) =>
            (await svc.ListForPageAsync(contentId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        pageLinks.MapPost("/create", async (Guid contentId, CreateJiraIssueRequest body, PageIssueLinkService svc, CancellationToken ct) =>
            (await svc.CreateIssueAsync(contentId, body, ct).ConfigureAwait(false)).ToHttp(d => Results.Created($"/api/v1/pages/{contentId:D}/jira-links/{d.Id:D}", d)));
        pageLinks.MapPost(string.Empty, async (Guid contentId, LinkJiraIssueRequest body, PageIssueLinkService svc, CancellationToken ct) =>
            (await svc.LinkExistingAsync(contentId, body, ct).ConfigureAwait(false)).ToHttp(d => Results.Created($"/api/v1/pages/{contentId:D}/jira-links/{d.Id:D}", d)));
        pageLinks.MapPost("/{linkId:guid}/refresh", async (Guid contentId, Guid linkId, PageIssueLinkService svc, CancellationToken ct) =>
            (await svc.RefreshAsync(contentId, linkId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        pageLinks.MapDelete("/{linkId:guid}", async (Guid contentId, Guid linkId, PageIssueLinkService svc, CancellationToken ct) =>
            (await svc.UnlinkAsync(contentId, linkId, ct).ConfigureAwait(false)).ToHttp());
    }
}
