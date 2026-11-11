using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Application;

namespace Raavi.Wiki.Api;

/// <summary>Space whiteboards (docs/api/WIKI_API.md §Whiteboards). Tenant host only.</summary>
public static class WhiteboardEndpoints
{
    public static void MapWhiteboardEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        g.MapGet("/spaces/{key}/whiteboards", async (string key, bool? archived, WhiteboardService svc, CancellationToken ct) =>
            (await svc.ListAsync(key, archived == true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/spaces/{key}/whiteboards", async (string key, WhiteboardRequest body, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
            (await svc.CreateAsync(key, body, ct).ConfigureAwait(false)).ToHttp(d =>
            {
                ETags.Set(http.Response, d.Revision);
                return Results.Created($"/api/v1/whiteboards/{d.Id}", d);
            }));

        g.MapPost("/spaces/{key}/whiteboards/from-template/{templateId:guid}", async (string key, Guid templateId, BoardTitleRequest? body, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
            (await svc.CreateFromTemplateAsync(key, templateId, body?.Title, ct).ConfigureAwait(false)).ToHttp(d =>
            {
                ETags.Set(http.Response, d.Revision);
                return Results.Created($"/api/v1/whiteboards/{d.Id}", d);
            }));

        g.MapGet("/whiteboard-templates", async (string? spaceKey, WhiteboardService svc, CancellationToken ct) =>
            (await svc.ListTemplatesAsync(spaceKey, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/whiteboards/{id:guid}", async (Guid id, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision)));

        g.MapPut("/whiteboards/{id:guid}", async (Guid id, WhiteboardRequest body, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.UpdateAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision))
                : expected.Error!.ToProblem();
        });

        g.MapPut("/whiteboards/{id:guid}/title", async (Guid id, BoardTitleRequest body, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
            (await svc.RenameAsync(id, body, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision)));

        g.MapPut("/whiteboards/{id:guid}/meta", async (Guid id, BoardMetaRequest body, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
            (await svc.UpdateMetaAsync(id, body, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision)));

        g.MapPost("/whiteboards/{id:guid}/duplicate", async (Guid id, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
            (await svc.DuplicateAsync(id, ct).ConfigureAwait(false)).ToHttp(d =>
            {
                ETags.Set(http.Response, d.Revision);
                return Results.Created($"/api/v1/whiteboards/{d.Id}", d);
            }));

        g.MapPost("/whiteboards/{id:guid}/archive", async (Guid id, WhiteboardService svc, CancellationToken ct) =>
            (await svc.ArchiveAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/whiteboards/{id:guid}/restore", async (Guid id, WhiteboardService svc, CancellationToken ct) =>
            (await svc.RestoreAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPut("/whiteboards/{id:guid}/favorite", async (Guid id, bool? on, WhiteboardService svc, CancellationToken ct) =>
            (await svc.FavoriteAsync(id, on != false, ct).ConfigureAwait(false)).ToHttp());

        g.MapDelete("/whiteboards/{id:guid}/favorite", async (Guid id, WhiteboardService svc, CancellationToken ct) =>
            (await svc.FavoriteAsync(id, false, ct).ConfigureAwait(false)).ToHttp());

        g.MapDelete("/whiteboards/{id:guid}", async (Guid id, WhiteboardService svc, CancellationToken ct) =>
            (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());

        g.MapGet("/whiteboards/{id:guid}/export", async (Guid id, WhiteboardService svc, CancellationToken ct) =>
            (await svc.ExportSvgAsync(id, ct).ConfigureAwait(false)).ToHttp(e => Results.File(e.Data, "image/svg+xml", e.FileName)));

        g.MapGet("/whiteboards/{id:guid}/export.json", async (Guid id, WhiteboardService svc, CancellationToken ct) =>
            (await svc.ExportJsonAsync(id, ct).ConfigureAwait(false)).ToHttp(e => Results.File(e.Data, "application/json", e.FileName)));

        g.MapGet("/whiteboards/{id:guid}/attachments", async (Guid id, WhiteboardService svc, CancellationToken ct) =>
            (await svc.ListAttachmentsAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/whiteboards/{id:guid}/attachments", async (Guid id, IFormFile file, WhiteboardService svc, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return (await svc.UploadAttachmentAsync(id, file.FileName, stream, file.Length, ct).ConfigureAwait(false))
                .ToHttp(a => Results.Created($"/api/v1/whiteboard-attachments/{a.Id}", a));
        }).DisableAntiforgery();

        g.MapGet("/whiteboard-attachments/{id:guid}", async (Guid id, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
            (await svc.DownloadAttachmentAsync(id, ct).ConfigureAwait(false)).ToHttp(a =>
            {
                http.Response.Headers.CacheControl = "private, max-age=3600";
                http.Response.Headers["X-Content-Type-Options"] = "nosniff";
                return Results.File(a.Data, a.MediaType, a.FileName);
            }));

        g.MapPost("/whiteboards/{id:guid}/template", async (Guid id, BoardTitleRequest? body, WhiteboardService svc, CancellationToken ct) =>
            (await svc.SaveAsTemplateAsync(id, body?.Title, ct).ConfigureAwait(false)).ToHttp(t => Results.Created($"/api/v1/whiteboard-templates/{t.Id}", t)));

        g.MapGet("/whiteboards/{id:guid}/revisions", async (Guid id, WhiteboardService svc, CancellationToken ct) =>
            (await svc.ListRevisionsAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/whiteboards/{id:guid}/revisions/{revisionId:guid}", async (Guid id, Guid revisionId, WhiteboardService svc, CancellationToken ct) =>
            (await svc.GetRevisionAsync(id, revisionId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/whiteboards/{id:guid}/revisions/{revisionId:guid}/restore", async (Guid id, Guid revisionId, WhiteboardService svc, HttpContext http, CancellationToken ct) =>
            (await svc.RestoreRevisionAsync(id, revisionId, ct).ConfigureAwait(false)).ToHttp(d => ETags.OkWithETag(http, d, d.Revision)));

        g.MapGet("/whiteboards/{id:guid}/comments", async (Guid id, WhiteboardService svc, CancellationToken ct) =>
            (await svc.ListCommentsAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/whiteboards/{id:guid}/comments", async (Guid id, WhiteboardCommentRequest body, WhiteboardService svc, CancellationToken ct) =>
            (await svc.AddCommentAsync(id, body, ct).ConfigureAwait(false)).ToHttp(c => Results.Created($"/api/v1/whiteboards/{id}/comments/{c.Id}", c)));

        g.MapPost("/whiteboards/{id:guid}/comments/{commentId:guid}/resolve", async (Guid id, Guid commentId, WhiteboardService svc, CancellationToken ct) =>
            (await svc.ResolveCommentAsync(id, commentId, resolve: true, ct).ConfigureAwait(false)).ToHttp());

        g.MapPost("/whiteboards/{id:guid}/comments/{commentId:guid}/reopen", async (Guid id, Guid commentId, WhiteboardService svc, CancellationToken ct) =>
            (await svc.ResolveCommentAsync(id, commentId, resolve: false, ct).ConfigureAwait(false)).ToHttp());
    }
}
