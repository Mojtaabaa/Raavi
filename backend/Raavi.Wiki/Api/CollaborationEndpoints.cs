using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Infrastructure.Http;
using Raavi.Infrastructure.Messaging;
using Raavi.Infrastructure.Tenancy;
using Raavi.Wiki.Application;
using Raavi.Wiki.Collaboration;

namespace Raavi.Wiki.Api;

/// <summary><see cref="Kind"/> is <c>page</c> (default; <see cref="ContentId"/> is the page) or <c>board</c> (the whiteboard id).</summary>
public sealed record TicketRequest(Guid ContentId, string? Kind = null);

public sealed record DraftTitleRequest(string Title);

public static class CollaborationEndpoints
{
    /// <summary>Browser-facing endpoints (tenant host, signed-in members).</summary>
    public static void MapCollaborationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        g.MapGet("/collaboration/config", (CollaborationService svc) => Results.Ok(new { enabled = svc.IsEnabled }));

        g.MapPost("/collaboration/tickets", async (TicketRequest body, CollaborationService svc, BoardCollaborationService boards, HttpContext http, CancellationToken ct) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            if (string.Equals(body.Kind, "board", StringComparison.OrdinalIgnoreCase))
            {
                return (await boards.IssueTicketAsync(body.ContentId, ct).ConfigureAwait(false)).ToHttp(Results.Ok);
            }

            return (await svc.IssueTicketAsync(body.ContentId, ct).ConfigureAwait(false)).ToHttp(Results.Ok);
        });

        g.MapPut("/content/{id:guid}/draft/title", async (Guid id, DraftTitleRequest body, PublishingService svc, CancellationToken ct) =>
            (await svc.SaveDraftTitleAsync(id, body.Title, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
    }

    /// <summary>
    /// Service endpoints for the collaboration server (<c>/internal/collaboration/*</c>): HMAC-signed requests only, no
    /// cookies, no host-based tenant. The tenant comes from the document address and must be an active tenant; every
    /// operation runs in a scope bound to it (RLS applies). Must not be exposed by the public reverse proxy.
    /// </summary>
    public static void MapCollaborationInternalEndpoints(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var g = app.MapGroup("/internal/collaboration").AllowAnonymous(); // signature verified by UseCollaborationSignatures (before binding)

        g.MapGet("/jwks", (TicketSigner signer) => signer.IsAvailable ? Results.Ok(signer.Jwks()) : Results.StatusCode(StatusCodes.Status503ServiceUnavailable));

        g.MapGet("/documents/{name}/state", (string name, HttpContext http, CancellationToken ct) =>
            InTenantAsync(http, name, async (sp, address) =>
            {
                var state = address.Kind == DocumentKind.Board
                    ? await sp.GetRequiredService<BoardCollaborationService>().LoadStateAsync(address.ContentId, ct).ConfigureAwait(false)
                    : await sp.GetRequiredService<CollaborationService>().LoadStateAsync(address.ContentId, ct).ConfigureAwait(false);
                return state is null ? Results.NotFound() : Results.Ok(state);
            }));

        g.MapPost("/documents/{name}/updates", (string name, StoreUpdateRequest body, HttpContext http, CancellationToken ct) =>
            InTenantAsync(http, name, async (sp, address) =>
                (address.Kind == DocumentKind.Board
                    ? await sp.GetRequiredService<BoardCollaborationService>().StoreStateAsync(address.ContentId, body, ct).ConfigureAwait(false)
                    : await sp.GetRequiredService<CollaborationService>().StoreUpdateAsync(address.ContentId, body, ct).ConfigureAwait(false)).ToHttp(seq => Results.Ok(new { seq }))));

        g.MapPost("/documents/{name}/snapshot", (string name, StoreSnapshotRequest body, HttpContext http, CancellationToken ct) =>
            InTenantAsync(http, name, async (sp, address) =>
                address.Kind == DocumentKind.Board
                    ? Results.NoContent() // boards persist their full state with every update; there is nothing to compact
                    : (await sp.GetRequiredService<CollaborationService>().StoreSnapshotAsync(address.ContentId, body, ct).ConfigureAwait(false)).ToHttp()));

        g.MapPost("/check", (CheckRequest body, HttpContext http, CancellationToken ct) =>
            InTenantAsync(http, body.DocumentName, async (sp, address) =>
                Results.Ok(new { permission = address.Kind == DocumentKind.Board
                    ? await sp.GetRequiredService<BoardCollaborationService>().CheckAsync(address.ContentId, body.UserId, body.SessionId, ct).ConfigureAwait(false)
                    : await sp.GetRequiredService<CollaborationService>().CheckAsync(address.ContentId, body.UserId, body.SessionId, ct).ConfigureAwait(false) }),
                missingTenant: Results.Ok(new { permission = "none" })));
    }

    /// <summary>
    /// Verifies the HMAC signature of every <c>/internal/collaboration</c> request before routing/model binding reads the
    /// body (the body is buffered and rewound). Unsigned or wrongly signed calls get 401; with collaboration disabled, 404.
    /// </summary>
    public static IApplicationBuilder UseCollaborationSignatures(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        return app.UseWhen(c => c.Request.Path.StartsWithSegments("/internal/collaboration", StringComparison.Ordinal), branch => branch.Use(async (http, next) =>
        {
            var options = CollaborationOptions.From(http.RequestServices.GetRequiredService<IConfiguration>());
            if (!options.Enabled)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            if (http.Request.ContentLength > 32 * 1024 * 1024)
            {
                http.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }

            http.Request.EnableBuffering();
            using var buffer = new MemoryStream();
            await http.Request.Body.CopyToAsync(buffer, http.RequestAborted).ConfigureAwait(false);
            http.Request.Body.Position = 0;
            var pathAndQuery = http.Request.Path.Value + http.Request.QueryString.Value;
            var now = http.RequestServices.GetRequiredService<IClock>().UtcNow;
            if (!ServiceSignature.Verify(options.ServiceKey, http.Request.Headers[ServiceSignature.Header].ToString(), http.Request.Method, pathAndQuery, buffer.ToArray(), now))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            await next(http).ConfigureAwait(false);
        }));
    }

    private static async Task<IResult> InTenantAsync(HttpContext http, string documentName, Func<IServiceProvider, DocumentAddress, Task<IResult>> action, IResult? missingTenant = null)
    {
        var address = DocumentAddress.Parse(documentName);
        if (address is null)
        {
            return Results.BadRequest();
        }

        var directory = http.RequestServices.GetRequiredService<ITenantDirectory>();
        var tenant = await directory.FindByIdAsync(address.TenantId, http.RequestAborted).ConfigureAwait(false);
        if (tenant is null || !tenant.IsActive)
        {
            return missingTenant ?? Results.NotFound();
        }

        var scope = http.RequestServices.GetRequiredService<ITenantScopeFactory>()
            .CreateScope(new ResolvedTenant(tenant.Id, tenant.Code, tenant.Slug, TenantResolutionSource.ServicePrincipal, null));
        await using (scope.ConfigureAwait(false))
        {
            return await action(scope.ServiceProvider, address).ConfigureAwait(false);
        }
    }
}

/// <summary>Permission and restriction changes trigger an immediate re-check of live editing connections in the tenant.</summary>
public sealed class CollaborationRevocationHandler(CollaborationClient client) : IOutboxHandler
{
    public bool CanHandle(string eventType) => eventType is WikiEventTypes.SpacePermissionsChanged or WikiEventTypes.ContentRestrictionsChanged or WikiEventTypes.ContentTrashed or WikiEventTypes.ContentArchived;

    public Task HandleAsync(OutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);
        return client.RecheckAsync(outboxEvent.TenantId, null, cancellationToken);
    }
}
