using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Application;

namespace Raavi.Wiki.Api;

/// <summary>Page approvals (docs/api/WIKI_API.md §Approvals). Tenant host only.</summary>
public static class ApprovalEndpoints
{
    public static void MapApprovalEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        g.MapPost("/content/{id:guid}/approvals", async (Guid id, ApprovalRequestBody body, ApprovalService svc, CancellationToken ct) =>
            (await svc.RequestAsync(id, body, ct).ConfigureAwait(false)).ToHttp(d => Results.Created($"/api/v1/approvals/{d.Id}", d)));

        g.MapGet("/content/{id:guid}/approvals", async (Guid id, ApprovalService svc, CancellationToken ct) => (await svc.ForPageAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/approvals", async (string? scope, ApprovalService svc, CancellationToken ct) => (await svc.ListAsync(scope ?? "inbox", ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/approvals/{id:guid}", async (Guid id, ApprovalService svc, CancellationToken ct) => (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/approvals/{id:guid}/decision", async (Guid id, ApprovalDecisionBody body, ApprovalService svc, CancellationToken ct) => (await svc.DecideAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/approvals/{id:guid}/cancel", async (Guid id, ApprovalService svc, CancellationToken ct) => (await svc.CancelAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
    }
}
