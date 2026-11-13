using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Application;

namespace Raavi.Wiki.Api;

/// <summary>Space tasks and "My tasks" (docs/api/WIKI_API.md §Tasks). Tenant host only.</summary>
public static class TaskEndpoints
{
    public static void MapTaskEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.AuthenticatedUser);

        g.MapGet("/spaces/{key}/tasks", async (string key, string? status, Guid? assignee, bool? archived, TaskService svc, CancellationToken ct) =>
            (await svc.ListAsync(key, status, assignee, ct, includeArchived: archived == true).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapPost("/spaces/{key}/tasks", async (string key, TaskRequest body, TaskService svc, HttpContext http, CancellationToken ct) =>
            (await svc.CreateAsync(key, body, ct).ConfigureAwait(false)).ToHttp(t =>
            {
                ETags.Set(http.Response, t.Revision);
                return Results.Created($"/api/v1/tasks/{t.Id}", t);
            }));

        g.MapGet("/spaces/{key}/tasks/export", async (string key, TaskService svc, CancellationToken ct) =>
            (await svc.ExportCsvAsync(key, ct).ConfigureAwait(false)).ToHttp(e => Results.File(e.Data, "text/csv; charset=utf-8", e.FileName)));

        g.MapGet("/tasks/mine", async (string? status, TaskService svc, CancellationToken ct) => Results.Ok(await svc.MineAsync(status, ct).ConfigureAwait(false)));

        g.MapGet("/tasks/{id:guid}", async (Guid id, TaskService svc, HttpContext http, CancellationToken ct) =>
            (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(t => ETags.OkWithETag(http, t, t.Revision)));

        g.MapGet("/tasks/{id:guid}/assignees", async (Guid id, bool? history, TaskService svc, CancellationToken ct) =>
            (await svc.ListAssigneesAsync(id, includeRemoved: history == true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/tasks/{id:guid}/children", async (Guid id, TaskService svc, CancellationToken ct) =>
            (await svc.ListChildrenAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/tasks/{id:guid}/promote", async (Guid id, TaskService svc, CancellationToken ct) =>
            (await svc.PromoteAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/tasks/{id:guid}/checklists", async (Guid id, TaskChecklistService svc, CancellationToken ct) =>
            (await svc.ListAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/tasks/{id:guid}/checklists", async (Guid id, TaskChecklistWrite body, TaskChecklistService svc, CancellationToken ct) =>
            (await svc.CreateChecklistAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPut("/task-checklists/{id:guid}", async (Guid id, TaskChecklistWrite body, TaskChecklistService svc, CancellationToken ct) =>
            (await svc.UpdateChecklistAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapDelete("/task-checklists/{id:guid}", async (Guid id, TaskChecklistService svc, CancellationToken ct) =>
            (await svc.DeleteChecklistAsync(id, ct).ConfigureAwait(false)).ToHttp());
        g.MapPost("/task-checklists/{id:guid}/items", async (Guid id, TaskChecklistItemWrite body, TaskChecklistService svc, CancellationToken ct) =>
            (await svc.AddItemAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPut("/task-checklist-items/{id:guid}", async (Guid id, TaskChecklistItemWrite body, TaskChecklistService svc, CancellationToken ct) =>
            (await svc.UpdateItemAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapDelete("/task-checklist-items/{id:guid}", async (Guid id, TaskChecklistService svc, CancellationToken ct) =>
            (await svc.DeleteItemAsync(id, ct).ConfigureAwait(false)).ToHttp());
        g.MapPut("/task-checklists/{id:guid}/reorder", async (Guid id, Guid[] orderedIds, TaskChecklistService svc, CancellationToken ct) =>
            (await svc.ReorderItemsAsync(id, orderedIds ?? [], ct).ConfigureAwait(false)).ToHttp());

        g.MapGet("/tasks/{id:guid}/comments", async (Guid id, int? skip, int? take, string? sort, bool? includeDeleted, TaskCollaborationService svc, CancellationToken ct) =>
            (await svc.ListCommentsAsync(id, skip, take, sort, includeDeleted == true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/tasks/{id:guid}/comments", async (Guid id, TaskCommentBodyRequest body, Guid? parentId, TaskCollaborationService svc, CancellationToken ct) =>
            (await svc.CreateCommentAsync(id, body, parentId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPut("/task-comments/{id:guid}", async (Guid id, TaskCommentBodyRequest body, TaskCollaborationService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.EditCommentAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(c => ETags.OkWithETag(http, c, c.Revision))
                : expected.Error!.ToProblem();
        });
        g.MapDelete("/task-comments/{id:guid}", async (Guid id, TaskCollaborationService svc, CancellationToken ct) =>
            (await svc.DeleteCommentAsync(id, ct).ConfigureAwait(false)).ToHttp());

        g.MapGet("/tasks/{id:guid}/attachments", async (Guid id, TaskCollaborationService svc, CancellationToken ct) =>
            (await svc.ListAttachmentsAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/tasks/{id:guid}/attachments", async (Guid id, Guid? commentId, IFormFile file, TaskCollaborationService svc, CancellationToken ct) =>
        {
            await using var stream = file.OpenReadStream();
            return (await svc.UploadAsync(id, commentId, file.FileName, stream, file.Length, ct).ConfigureAwait(false))
                .ToHttp(a => Results.Created($"/api/v1/task-attachments/{a.Id}", a));
        }).DisableAntiforgery();
        g.MapGet("/task-attachments/{id:guid}", async (Guid id, TaskCollaborationService svc, HttpContext http, CancellationToken ct) =>
        {
            var r = await svc.DownloadAsync(id, ct).ConfigureAwait(false);
            if (!r.IsSuccess)
            {
                return r.Error!.ToProblem();
            }

            var a = r.Value;
            http.Response.Headers.ETag = $"\"{a.Sha256}\"";
            return Results.File(a.Data, a.MediaType, a.FileName);
        });
        g.MapDelete("/task-attachments/{id:guid}", async (Guid id, TaskCollaborationService svc, CancellationToken ct) =>
            (await svc.DeleteAttachmentAsync(id, ct).ConfigureAwait(false)).ToHttp());

        g.MapGet("/tasks/{id:guid}/watchers", async (Guid id, TaskCollaborationService svc, CancellationToken ct) =>
            (await svc.ListWatchersAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPut("/tasks/{id:guid}/watch", async (Guid id, TaskWatchRequest body, TaskCollaborationService svc, CancellationToken ct) =>
            (await svc.SetWatchAsync(id, body.Watching, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/tasks/{id:guid}/watchers", async (Guid id, TaskAddWatcherRequest body, TaskCollaborationService svc, CancellationToken ct) =>
            (await svc.AddWatcherAsync(id, body.UserId, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/tasks/{id:guid}/activity", async (Guid id, int? limit, TaskCollaborationService svc, CancellationToken ct) =>
            (await svc.ListActivityAsync(id, limit, ct).ConfigureAwait(false)).ToHttp(Results.Ok));

        g.MapGet("/tasks/{id:guid}/time", async (Guid id, Guid? userId, DateOnly? from, DateOnly? to, TaskTimeService svc, CancellationToken ct) =>
            (await svc.ListAsync(id, userId, from, to, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/tasks/{id:guid}/time", async (Guid id, TaskTimeWrite body, TaskTimeService svc, CancellationToken ct) =>
            (await svc.CreateAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPut("/task-time/{id:guid}", async (Guid id, TaskTimeWrite body, TaskTimeService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.UpdateAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(e => ETags.OkWithETag(http, e, e.Revision))
                : expected.Error!.ToProblem();
        });
        g.MapDelete("/task-time/{id:guid}", async (Guid id, TaskTimeService svc, CancellationToken ct) =>
            (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());

        g.MapPut("/tasks/{id:guid}", async (Guid id, TaskRequest body, TaskService svc, HttpContext http, CancellationToken ct) =>
        {
            var expected = ETags.RequireIfMatch(http.Request);
            return expected.IsSuccess
                ? (await svc.UpdateAsync(id, expected.Value, body, ct).ConfigureAwait(false)).ToHttp(t => ETags.OkWithETag(http, t, t.Revision))
                : expected.Error!.ToProblem();
        });

        g.MapPost("/tasks/{id:guid}/complete", async (Guid id, TaskService svc, CancellationToken ct) => (await svc.SetDoneAsync(id, true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/tasks/{id:guid}/reopen", async (Guid id, TaskService svc, CancellationToken ct) => (await svc.SetDoneAsync(id, false, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/tasks/{id:guid}/archive", async (Guid id, TaskService svc, CancellationToken ct) =>
            (await svc.ArchiveAsync(id, restore: false, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/tasks/{id:guid}/restore", async (Guid id, TaskService svc, CancellationToken ct) =>
            (await svc.ArchiveAsync(id, restore: true, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapDelete("/tasks/{id:guid}", async (Guid id, TaskService svc, CancellationToken ct) => (await svc.DeleteAsync(id, ct).ConfigureAwait(false)).ToHttp());
    }
}
