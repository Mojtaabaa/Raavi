using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Application;

namespace Raavi.Wiki.Api;

/// <summary>Comments, reactions, watching and the notification inbox (docs/api/WIKI_API.md §Comments). Tenant host only.</summary>
public static class CommentEndpoints
{
    public static void MapCommentEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        // ── Comments ───────────────────────────────────────────────────────────────────────────
        g.MapGet("/content/{id:guid}/comments", async (Guid id, bool? includeResolved, CommentService svc, CancellationToken ct) =>
            (await svc.ListAsync(id, includeResolved == true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/content/{id:guid}/comments", async (Guid id, CreateCommentRequest body, CommentService svc, CancellationToken ct) =>
            (await svc.CreateAsync(id, body, ct).ConfigureAwait(false)).ToHttp(t => Results.Created($"/api/v1/comments/{t.Id}", t)));

        g.MapPost("/comments/{id:guid}/replies", async (Guid id, CommentBodyRequest body, CommentService svc, CancellationToken ct) =>
            (await svc.ReplyAsync(id, body, ct).ConfigureAwait(false)).ToHttp(c => Results.Created($"/api/v1/comments/{c.Id}", c)));

        g.MapPut("/comments/{id:guid}", async (Guid id, CommentBodyRequest body, CommentService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            if (!expected.IsSuccess)
            {
                return expected.Error!.ToProblem();
            }

            return (await svc.EditAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(c => ETags.OkWithETag(http, c, c.Revision));
        });

        g.MapDelete("/comments/{id:guid}", async (Guid id, CommentService svc, CancellationToken ct) =>
            (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());

        g.MapPost("/comments/{id:guid}/resolve", async (Guid id, CommentService svc, CancellationToken ct) =>
            (await svc.SetResolvedAsync(id, true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/comments/{id:guid}/reopen", async (Guid id, CommentService svc, CancellationToken ct) =>
            (await svc.SetResolvedAsync(id, false, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/comments/{id:guid}/reactions", async (Guid id, ReactionRequest body, CommentService svc, CancellationToken ct) =>
            (await svc.ReactAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        // ── Watching ───────────────────────────────────────────────────────────────────────────
        g.MapGet("/content/{id:guid}/watch", async (Guid id, CommentService svc, CancellationToken ct) =>
            (await svc.GetWatchAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPut("/content/{id:guid}/watch", async (Guid id, CommentService svc, CancellationToken ct) =>
            (await svc.SetWatchAsync(id, true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapDelete("/content/{id:guid}/watch", async (Guid id, CommentService svc, CancellationToken ct) =>
            (await svc.SetWatchAsync(id, false, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        // ── Notifications (the caller's own inbox) ─────────────────────────────────────────────
        g.MapGet("/notifications", async (bool? unreadOnly, DateTimeOffset? before, NotificationService svc, CancellationToken ct) =>
            Results.Ok(await svc.ListAsync(unreadOnly == true, before, ct).ConfigureAwait(false)));

        g.MapGet("/notifications/unread-count", async (NotificationService svc, CancellationToken ct) =>
            Results.Ok(new { count = await svc.UnreadCountAsync(ct).ConfigureAwait(false) }));

        g.MapPost("/notifications/{id:guid}/read", async (Guid id, NotificationService svc, CancellationToken ct) =>
            (await svc.MarkReadAsync(id, ct).ConfigureAwait(false)).ToHttp());

        g.MapPost("/notifications/read-all", async (NotificationService svc, CancellationToken ct) =>
        {
            await svc.MarkAllReadAsync(ct).ConfigureAwait(false);
            return Results.NoContent();
        });

        g.MapGet("/notifications/preferences", async (NotificationService svc, CancellationToken ct) =>
            Results.Ok(await svc.GetPreferencesAsync(ct).ConfigureAwait(false)));

        g.MapPut("/notifications/preferences", async (UpdateNotificationPreferencesRequest body, NotificationService svc, CancellationToken ct) =>
            Results.Ok(await svc.SetPreferencesAsync(body, ct).ConfigureAwait(false)));
    }
}
