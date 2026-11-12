using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Http;
using Raavi.Infrastructure.Messaging;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public sealed record TaskCommentDto(
    Guid Id, Guid ThreadId, Guid? ParentId, JsonElement Body, bool IsDeleted,
    Guid AuthorId, string? AuthorName, DateTimeOffset CreatedAt, DateTimeOffset? EditedAt,
    int Revision, bool CanEdit, bool CanDelete);

public sealed record TaskCommentsPageDto(
    IReadOnlyList<TaskCommentDto> Items, int Total, bool CanComment, bool Watching, string Sort);

public sealed record TaskCommentBodyRequest(JsonElement Body);

public sealed record TaskAttachmentDto(
    Guid Id, Guid TaskId, Guid? CommentId, string FileName, string MediaType, long Size,
    bool IsImage, DateTimeOffset CreatedAt, Guid CreatedBy, string? CreatedByName);

public sealed record TaskAttachmentDownload(string FileName, string MediaType, bool IsImage, byte[] Data, string Sha256);

public sealed record TaskWatcherDto(Guid UserId, string? Name, string State, DateTimeOffset UpdatedAt);

public sealed record TaskWatchersDto(IReadOnlyList<TaskWatcherDto> Items, bool Watching, bool CanManage);

public sealed record TaskWatchRequest(bool Watching);

public sealed record TaskAddWatcherRequest(Guid UserId);

public sealed record TaskActivityDto(Guid Id, string Kind, Guid ActorId, string? ActorName, string Title, DateTimeOffset OccurredAt);

/// <summary>Outbox payload for task comment fan-out (mentions / watchers). In-app notify only when ContentId is set (ADR-056).</summary>
public sealed record TaskCommentEvent(
    Guid SpaceId, Guid TaskId, Guid? ContentId, Guid CommentId, Guid ThreadId, Guid ActorId,
    string Title, string Excerpt, bool IsReply, IReadOnlyList<Guid> Mentions);

public static class TaskCommentEventTypes
{
    // Must not use the "task." prefix — ActivityProjection handles all WikiEventTypes.IsWiki ("task.*").
    public const string Created = "notify.task.comment";
    public const string MentionsAdded = "notify.task.mentions";
    public const string AttachmentAdded = "notify.task.attachment";
}

/// <summary>Comments, attachments, watchers and activity for WorkTask (B4). Reuses document/mention/sniff patterns.</summary>
public sealed class TaskCollaborationService(
    WikiDbContext db,
    TaskService tasks,
    WikiAuthorizer authz,
    IPrincipalDirectory directory,
    ICurrentUser current,
    IClock clock,
    IConfiguration configuration)
{
    public const int MaxCommentsPage = 50;
    public static readonly Error CommentNotFound = new("task.comment_not_found", "Comment not found.", ErrorKind.NotFound);
    public static readonly Error AttachmentNotFound = new("task.attachment_not_found", "Attachment not found.", ErrorKind.NotFound);
    public static readonly Error TooLarge = new("attachment.too_large", "The file is too large.", ErrorKind.Validation);
    public static readonly Error Empty = new("attachment.empty", "The file is empty.", ErrorKind.Validation);
    public static readonly Error WatcherInvalid = new("task.watcher_invalid", "The watcher is not a member of this workspace.", ErrorKind.Validation);

    public long MaxBytes => configuration.GetValue<long?>("Raavi:Attachments:MaxBytes") ?? 10 * 1024 * 1024;

    public async Task<Result<TaskCommentsPageDto>> ListCommentsAsync(Guid taskId, int? skip, int? take, string? sort, bool includeDeleted, CancellationToken ct)
    {
        var loaded = await tasks.LoadForCollaborationAsync(taskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskCommentsPageDto>.Failure(loaded.Error!);
        }

        var (_, space) = loaded.Value;
        // Soft-deleted rows reveal author/timestamps; require Edit (same bar as assignee history).
        if (includeDeleted && !space.CanWrite(PermissionAction.Edit))
        {
            return Result<TaskCommentsPageDto>.Failure(WikiErrors.Forbidden);
        }

        var descending = !string.Equals(sort, "asc", StringComparison.OrdinalIgnoreCase);
        var pageSize = Math.Clamp(take ?? 20, 1, MaxCommentsPage);
        var offset = Math.Max(skip ?? 0, 0);
        var q = db.TaskComments.AsNoTracking().Where(c => c.TaskId == taskId);
        if (!includeDeleted)
        {
            q = q.Where(c => c.DeletedAt == null);
        }

        var total = await q.CountAsync(ct).ConfigureAwait(false);
        q = descending ? q.OrderByDescending(c => c.CreatedAt) : q.OrderBy(c => c.CreatedAt);
        var rows = await q.Skip(offset).Take(pageSize).ToListAsync(ct).ConfigureAwait(false);
        var watching = await IsWatchingAsync(taskId, ct).ConfigureAwait(false);
        return Result<TaskCommentsPageDto>.Success(new TaskCommentsPageDto(
            await ToCommentDtosAsync(space, rows, ct).ConfigureAwait(false),
            total,
            CanComment(space),
            watching,
            descending ? "desc" : "asc"));
    }

    public async Task<Result<TaskCommentDto>> CreateCommentAsync(Guid taskId, TaskCommentBodyRequest request, Guid? parentId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await tasks.LoadForCollaborationAsync(taskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskCommentDto>.Failure(loaded.Error!);
        }

        var (task, space) = loaded.Value;
        if (!CanComment(space))
        {
            return Result<TaskCommentDto>.Failure(WikiErrors.Forbidden);
        }

        var body = RaaviDocument.Parse(request.Body);
        var now = clock.UtcNow;
        TaskComment comment;
        try
        {
            if (parentId is { } pid)
            {
                var root = await db.TaskComments.FirstOrDefaultAsync(c => c.Id == pid && c.TaskId == taskId, ct).ConfigureAwait(false);
                if (root is null || root.IsDeleted)
                {
                    return Result<TaskCommentDto>.Failure(CommentNotFound);
                }

                if (!root.IsRoot)
                {
                    root = await db.TaskComments.FirstAsync(c => c.Id == root.ThreadId, ct).ConfigureAwait(false);
                }

                comment = root.Reply(body, current.UserId, now);
            }
            else
            {
                comment = TaskComment.Start(taskId, body, current.UserId, now);
            }
        }
        catch (DomainException ex)
        {
            return Result<TaskCommentDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }

        db.TaskComments.Add(comment);
        await AutoWatchAsync(taskId, current.UserId, now, ct).ConfigureAwait(false);
        var mentions = await FilterMentionsAsync(body, ct).ConfigureAwait(false);
        PublishNotify(TaskCommentEventTypes.Created, task, comment, mentions, now);
        db.Publish(WikiEventTypes.TaskCommentCreated, new WikiEvent(task.SpaceId, task.ContentId, current.UserId, $"{task.Title}: {comment.Excerpt}", null, task.Id), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TaskCommentDto>.Success((await ToCommentDtosAsync(space, [comment], ct).ConfigureAwait(false))[0]);
    }

    public async Task<Result<TaskCommentDto>> EditCommentAsync(Guid commentId, int expectedRevision, TaskCommentBodyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = await LoadCommentAsync(commentId, track: true, ct).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return Result<TaskCommentDto>.Failure(target.Error!);
        }

        var (task, space, comment) = target.Value;
        if (comment.AuthorId != current.UserId || !CanComment(space))
        {
            return Result<TaskCommentDto>.Failure(WikiErrors.Forbidden);
        }

        if (comment.Revision != expectedRevision)
        {
            return Result<TaskCommentDto>.Failure(ETags.PreconditionFailed);
        }

        var previous = Comment.MentionedUsers(RaaviDocument.Parse(comment.BodyJson));
        var body = RaaviDocument.Parse(request.Body);
        try
        {
            comment.Edit(body, clock.UtcNow);
        }
        catch (DomainException ex)
        {
            return Result<TaskCommentDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }

        var now = clock.UtcNow;
        var next = await FilterMentionsAsync(body, ct).ConfigureAwait(false);
        var added = next.Where(id => !previous.Contains(id)).ToList();
        if (added.Count > 0)
        {
            PublishNotify(TaskCommentEventTypes.MentionsAdded, task, comment, added, now);
        }

        db.Publish(WikiEventTypes.TaskCommentUpdated, new WikiEvent(task.SpaceId, task.ContentId, current.UserId, $"{task.Title}: {comment.Excerpt}", null, task.Id), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TaskCommentDto>.Success((await ToCommentDtosAsync(space, [comment], ct).ConfigureAwait(false))[0]);
    }

    public async Task<Result> DeleteCommentAsync(Guid commentId, CancellationToken ct)
    {
        var target = await LoadCommentAsync(commentId, track: true, ct).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return Result.Failure(target.Error!);
        }

        var (task, space, comment) = target.Value;
        var authorOk = comment.AuthorId == current.UserId && CanComment(space);
        var moderateOk = space.CanWrite(PermissionAction.Edit);
        if (!authorOk && !moderateOk)
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        var now = clock.UtcNow;
        comment.SoftDelete(current.UserId, now);
        foreach (var a in await db.TaskAttachments.Where(x => x.CommentId == commentId && x.DeletedAt == null).ToListAsync(ct).ConfigureAwait(false))
        {
            a.SoftDelete(current.UserId, now);
        }

        db.Publish(WikiEventTypes.TaskCommentDeleted, new WikiEvent(task.SpaceId, task.ContentId, current.UserId, task.Title, null, task.Id), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<TaskAttachmentDto>>> ListAttachmentsAsync(Guid taskId, CancellationToken ct)
    {
        var loaded = await tasks.LoadForCollaborationAsync(taskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<TaskAttachmentDto>>.Failure(loaded.Error!);
        }

        var rows = await db.TaskAttachments.AsNoTracking()
            .Where(a => a.TaskId == taskId && a.DeletedAt == null)
            .OrderBy(a => a.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<TaskAttachmentDto>>.Success(await ToAttachmentDtosAsync(rows, ct).ConfigureAwait(false));
    }

    public async Task<Result<TaskAttachmentDto>> UploadAsync(Guid taskId, Guid? commentId, string fileName, Stream content, long? declaredLength, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        var loaded = await tasks.LoadForCollaborationAsync(taskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskAttachmentDto>.Failure(loaded.Error!);
        }

        var (task, space) = loaded.Value;
        if (!CanComment(space) && !space.CanWrite(PermissionAction.Edit))
        {
            return Result<TaskAttachmentDto>.Failure(WikiErrors.Forbidden);
        }

        if (commentId is { } cid)
        {
            var comment = await db.TaskComments.AsNoTracking().FirstOrDefaultAsync(c => c.Id == cid && c.TaskId == taskId, ct).ConfigureAwait(false);
            if (comment is null || comment.IsDeleted)
            {
                return Result<TaskAttachmentDto>.Failure(CommentNotFound);
            }
        }

        if (declaredLength > MaxBytes)
        {
            return Result<TaskAttachmentDto>.Failure(TooLarge);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await content.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxBytes)
            {
                return Result<TaskAttachmentDto>.Failure(TooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        if (buffer.Length == 0)
        {
            return Result<TaskAttachmentDto>.Failure(Empty);
        }

        var data = buffer.ToArray();
        var now = clock.UtcNow;
        var attachment = TaskAttachment.Create(taskId, commentId, fileName, data, current.UserId, now);
        db.TaskAttachments.Add(attachment);
        await AutoWatchAsync(taskId, current.UserId, now, ct).ConfigureAwait(false);
        db.Publish(WikiEventTypes.TaskAttachmentAdded, new WikiEvent(task.SpaceId, task.ContentId, current.UserId, $"{task.Title}: {attachment.FileName}", null, task.Id), now);
        if (task.ContentId is not null)
        {
            db.Outbox.Add(OutboxMessage.Create(
                db.CurrentTenantId,
                TaskCommentEventTypes.AttachmentAdded,
                new TaskCommentEvent(task.SpaceId, task.Id, task.ContentId, Guid.Empty, Guid.Empty, current.UserId, task.Title, attachment.FileName, false, []),
                now));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TaskAttachmentDto>.Success((await ToAttachmentDtosAsync([attachment], ct).ConfigureAwait(false))[0]);
    }

    public async Task<Result<TaskAttachmentDownload>> DownloadAsync(Guid id, CancellationToken ct)
    {
        var meta = await db.TaskAttachments.AsNoTracking().Where(a => a.Id == id && a.DeletedAt == null)
            .Select(a => new { a.TaskId }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (meta is null)
        {
            return Result<TaskAttachmentDownload>.Failure(AttachmentNotFound);
        }

        var loaded = await tasks.LoadForCollaborationAsync(meta.TaskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskAttachmentDownload>.Failure(AttachmentNotFound);
        }

        var a = await db.TaskAttachments.AsNoTracking().FirstAsync(x => x.Id == id, ct).ConfigureAwait(false);
        return Result<TaskAttachmentDownload>.Success(new TaskAttachmentDownload(a.FileName, a.MediaType, a.IsImage, a.Data, a.Sha256));
    }

    public async Task<Result> DeleteAttachmentAsync(Guid id, CancellationToken ct)
    {
        var row = await db.TaskAttachments.FirstOrDefaultAsync(a => a.Id == id && a.DeletedAt == null, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(AttachmentNotFound);
        }

        var loaded = await tasks.LoadForCollaborationAsync(row.TaskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(AttachmentNotFound);
        }

        var (task, space) = loaded.Value;
        if (!(row.CreatedBy == current.UserId || space.CanWrite(PermissionAction.Edit)))
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        var now = clock.UtcNow;
        row.SoftDelete(current.UserId, now);
        db.Publish(WikiEventTypes.TaskAttachmentRemoved, new WikiEvent(task.SpaceId, task.ContentId, current.UserId, $"{task.Title}: {row.FileName}", null, task.Id), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<TaskWatchersDto>> ListWatchersAsync(Guid taskId, CancellationToken ct)
    {
        var loaded = await tasks.LoadForCollaborationAsync(taskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskWatchersDto>.Failure(loaded.Error!);
        }

        var rows = await db.TaskWatchers.AsNoTracking()
            .Where(w => w.TaskId == taskId && w.State == WatchState.Watching)
            .OrderBy(w => w.UpdatedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        var names = rows.Count == 0 ? new Dictionary<Guid, string>() : await directory.GetUserNamesAsync(rows.Select(r => r.UserId).ToList(), ct).ConfigureAwait(false);
        var items = rows.Where(r => names.ContainsKey(r.UserId))
            .Select(r => new TaskWatcherDto(r.UserId, names.GetValueOrDefault(r.UserId), r.State.ToString(), r.UpdatedAt))
            .ToList();
        return Result<TaskWatchersDto>.Success(new TaskWatchersDto(
            items,
            items.Any(i => i.UserId == current.UserId),
            loaded.Value.Space.CanWrite(PermissionAction.Edit)));
    }

    public async Task<Result<TaskWatchersDto>> SetWatchAsync(Guid taskId, bool watching, CancellationToken ct)
    {
        var loaded = await tasks.LoadForCollaborationAsync(taskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskWatchersDto>.Failure(loaded.Error!);
        }

        var (task, _) = loaded.Value;
        var now = clock.UtcNow;
        var row = await db.TaskWatchers.FirstOrDefaultAsync(w => w.TaskId == taskId && w.UserId == current.UserId, ct).ConfigureAwait(false);
        var state = watching ? WatchState.Watching : WatchState.Muted;
        if (row is null)
        {
            db.TaskWatchers.Add(TaskWatcher.Create(taskId, current.UserId, state, now));
        }
        else if (row.State != state)
        {
            row.Set(state, now);
        }
        else
        {
            return await ListWatchersAsync(taskId, ct).ConfigureAwait(false);
        }

        db.Publish(watching ? WikiEventTypes.TaskWatched : WikiEventTypes.TaskUnwatched,
            new WikiEvent(task.SpaceId, task.ContentId, current.UserId, task.Title, null, task.Id), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await ListWatchersAsync(taskId, ct).ConfigureAwait(false);
    }

    public async Task<Result<TaskWatchersDto>> AddWatcherAsync(Guid taskId, Guid userId, CancellationToken ct)
    {
        var loaded = await tasks.LoadForCollaborationAsync(taskId, ct, requireEdit: true).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskWatchersDto>.Failure(loaded.Error!);
        }

        var (task, space) = loaded.Value;
        var names = await directory.GetUserNamesAsync([userId], ct).ConfigureAwait(false);
        if (!names.ContainsKey(userId))
        {
            return Result<TaskWatchersDto>.Failure(WatcherInvalid);
        }

        var principal = await directory.GetPrincipalAsync(userId, fresh: true, ct).ConfigureAwait(false);
        if (!(await authz.GetSpaceRightsAsync(space.Space, principal, ct).ConfigureAwait(false)).CanView)
        {
            return Result<TaskWatchersDto>.Failure(WatcherInvalid);
        }

        var now = clock.UtcNow;
        var row = await db.TaskWatchers.FirstOrDefaultAsync(w => w.TaskId == taskId && w.UserId == userId, ct).ConfigureAwait(false);
        if (row is null)
        {
            db.TaskWatchers.Add(TaskWatcher.Create(taskId, userId, WatchState.Watching, now));
            db.Publish(WikiEventTypes.TaskWatched, new WikiEvent(task.SpaceId, task.ContentId, current.UserId, task.Title, null, task.Id), now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else if (row.State != WatchState.Watching)
        {
            row.Set(WatchState.Watching, now);
            db.Publish(WikiEventTypes.TaskWatched, new WikiEvent(task.SpaceId, task.ContentId, current.UserId, task.Title, null, task.Id), now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return await ListWatchersAsync(taskId, ct).ConfigureAwait(false);
    }

    public async Task<Result<IReadOnlyList<TaskActivityDto>>> ListActivityAsync(Guid taskId, int? limit, CancellationToken ct)
    {
        var loaded = await tasks.LoadForCollaborationAsync(taskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<TaskActivityDto>>.Failure(loaded.Error!);
        }

        var take = Math.Clamp(limit ?? 40, 1, 100);
        var rows = await db.Activity.AsNoTracking()
            .Where(a => a.TaskId == taskId)
            .OrderByDescending(a => a.OccurredAt)
            .Take(take)
            .ToListAsync(ct).ConfigureAwait(false);
        var names = rows.Count == 0
            ? new Dictionary<Guid, string>()
            : await directory.GetUserNamesAsync(rows.Select(r => r.ActorId).Distinct().ToList(), ct).ConfigureAwait(false);
        return Result<IReadOnlyList<TaskActivityDto>>.Success(rows
            .Select(a => new TaskActivityDto(a.Id, a.Kind, a.ActorId, names.GetValueOrDefault(a.ActorId), a.Title, a.OccurredAt)).ToList());
    }

    public static async Task AutoWatchAsync(WikiDbContext db, Guid taskId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        if (db.TaskWatchers.Local.Any(w => w.TaskId == taskId && w.UserId == userId)
            || await db.TaskWatchers.AnyAsync(w => w.TaskId == taskId && w.UserId == userId, ct).ConfigureAwait(false))
        {
            return;
        }

        db.TaskWatchers.Add(TaskWatcher.Create(taskId, userId, WatchState.Watching, now));
    }

    private Task AutoWatchAsync(Guid taskId, Guid userId, DateTimeOffset now, CancellationToken ct) =>
        AutoWatchAsync(db, taskId, userId, now, ct);

    private async Task<bool> IsWatchingAsync(Guid taskId, CancellationToken ct) =>
        await db.TaskWatchers.AsNoTracking().AnyAsync(w => w.TaskId == taskId && w.UserId == current.UserId && w.State == WatchState.Watching, ct).ConfigureAwait(false);

    private static bool CanComment(SpaceRights space) =>
        space.CanWrite(PermissionAction.Comment) || space.CanWrite(PermissionAction.Edit);

    private void PublishNotify(string type, WorkTask task, TaskComment comment, IReadOnlyList<Guid> mentions, DateTimeOffset now) =>
        db.Outbox.Add(OutboxMessage.Create(
            db.CurrentTenantId,
            type,
            new TaskCommentEvent(task.SpaceId, task.Id, task.ContentId, comment.Id, comment.ThreadId, comment.AuthorId, task.Title, comment.Excerpt, !comment.IsRoot, mentions),
            now));

    private async Task<IReadOnlyList<Guid>> FilterMentionsAsync(RaaviDocument body, CancellationToken ct)
    {
        var mentioned = Comment.MentionedUsers(body);
        if (mentioned.Count == 0)
        {
            return [];
        }

        var members = await directory.GetUserNamesAsync(mentioned, ct).ConfigureAwait(false);
        return mentioned.Where(members.ContainsKey).ToList();
    }

    private async Task<Result<(WorkTask Task, SpaceRights Space, TaskComment Comment)>> LoadCommentAsync(Guid commentId, bool track, CancellationToken ct)
    {
        var query = track ? db.TaskComments : db.TaskComments.AsNoTracking();
        var comment = await query.FirstOrDefaultAsync(c => c.Id == commentId, ct).ConfigureAwait(false);
        if (comment is null || comment.IsDeleted)
        {
            return Result<(WorkTask, SpaceRights, TaskComment)>.Failure(CommentNotFound);
        }

        var loaded = await tasks.LoadForCollaborationAsync(comment.TaskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<(WorkTask, SpaceRights, TaskComment)>.Failure(CommentNotFound);
        }

        return Result<(WorkTask, SpaceRights, TaskComment)>.Success((loaded.Value.Task, loaded.Value.Space, comment));
    }

    private async Task<IReadOnlyList<TaskCommentDto>> ToCommentDtosAsync(SpaceRights space, IReadOnlyList<TaskComment> rows, CancellationToken ct)
    {
        var authors = rows.Select(r => r.AuthorId).Distinct().ToList();
        var names = authors.Count == 0 ? new Dictionary<Guid, string>() : await directory.GetUserNamesAsync(authors, ct).ConfigureAwait(false);
        var canModerate = space.CanWrite(PermissionAction.Edit);
        var canComment = CanComment(space);
        return rows.Select(c =>
        {
            var body = c.IsDeleted
                ? JsonDocument.Parse("""{"schema":"raavi.doc","schemaVersion":1,"doc":{"type":"doc","content":[{"type":"paragraph"}]}}""").RootElement.Clone()
                : JsonDocument.Parse(c.BodyJson).RootElement.Clone();
            var mine = c.AuthorId == current.UserId;
            return new TaskCommentDto(
                c.Id, c.ThreadId, c.ParentId, body, c.IsDeleted, c.AuthorId, names.GetValueOrDefault(c.AuthorId),
                c.CreatedAt, c.EditedAt, c.Revision,
                mine && canComment && !c.IsDeleted,
                ((mine && canComment) || canModerate) && !c.IsDeleted);
        }).ToList();
    }

    private async Task<IReadOnlyList<TaskAttachmentDto>> ToAttachmentDtosAsync(IReadOnlyList<TaskAttachment> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.CreatedBy).Distinct().ToList();
        var names = ids.Count == 0 ? new Dictionary<Guid, string>() : await directory.GetUserNamesAsync(ids, ct).ConfigureAwait(false);
        return rows.Select(a => new TaskAttachmentDto(
            a.Id, a.TaskId, a.CommentId, a.FileName, a.MediaType, a.Size, a.IsImage, a.CreatedAt, a.CreatedBy, names.GetValueOrDefault(a.CreatedBy))).ToList();
    }
}
