using System.Security.Cryptography;
using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

/// <summary>
/// Task-scoped comment (B4). Reuses <see cref="RaaviDocument"/> and <see cref="Comment.MentionedUsers"/>;
/// not the page <see cref="Comment"/> table (that stays ContentId-bound).
/// </summary>
public sealed class TaskComment : Entity, ITenantOwned
{
    public const int MaxBodyBytes = Comment.MaxBodyBytes;
    public const int MaxExcerptLength = Comment.MaxExcerptLength;
    public const int MaxMentions = Comment.MaxMentions;

    private TaskComment() { }

    public Guid TenantId { get; private set; }

    public Guid TaskId { get; private set; }

    public Guid ThreadId { get; private set; }

    public Guid? ParentId { get; private set; }

    public string BodyJson { get; private set; } = string.Empty;

    public string Excerpt { get; private set; } = string.Empty;

    public Guid AuthorId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? EditedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public int Revision { get; private set; }

    public bool IsRoot => ParentId is null;

    public bool IsDeleted => DeletedAt is not null;

    public static TaskComment Start(Guid taskId, RaaviDocument body, Guid authorId, DateTimeOffset now)
    {
        var c = new TaskComment
        {
            TaskId = taskId,
            AuthorId = authorId,
            CreatedAt = now,
            Revision = 1,
        };
        c.ThreadId = c.Id;
        c.SetBody(body);
        return c;
    }

    public TaskComment Reply(RaaviDocument body, Guid authorId, DateTimeOffset now)
    {
        if (!IsRoot)
        {
            throw new DomainException("task.comment.reply_to_reply", "Replies are added to the thread's first comment.");
        }

        EnsureNotDeleted();
        var r = new TaskComment
        {
            TaskId = TaskId,
            ThreadId = Id,
            ParentId = Id,
            AuthorId = authorId,
            CreatedAt = now,
            Revision = 1,
        };
        r.SetBody(body);
        return r;
    }

    public void Edit(RaaviDocument body, DateTimeOffset now)
    {
        EnsureNotDeleted();
        SetBody(body);
        EditedAt = now;
        Revision++;
    }

    public void SoftDelete(Guid by, DateTimeOffset now)
    {
        if (IsDeleted)
        {
            return;
        }

        DeletedAt = now;
        DeletedBy = by;
        Revision++;
    }

    private void SetBody(RaaviDocument body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (System.Text.Encoding.UTF8.GetByteCount(body.Json) > MaxBodyBytes)
        {
            throw new DomainException("task.comment.too_large", "The comment is too long.");
        }

        var text = body.PlainText.Trim();
        if (text.Length == 0 && Comment.MentionedUsers(body).Count == 0)
        {
            throw new DomainException("task.comment.empty", "The comment is empty.");
        }

        BodyJson = body.Json;
        Excerpt = text.Length > MaxExcerptLength ? text[..(MaxExcerptLength - 1)] + "…" : text;
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainException("task.comment.deleted", "This comment was deleted.");
        }
    }
}

/// <summary>
/// Task-scoped file (B4). Bytes in SQL like page/whiteboard attachments until object storage (7b).
/// Soft-deleted with the owning comment or explicitly; hard-removed with the task (cascade).
/// </summary>
public sealed class TaskAttachment : Entity, ITenantOwned
{
    public const int MaxFileNameLength = Attachment.MaxFileNameLength;

    private TaskAttachment() { }

    public Guid TenantId { get; private set; }

    public Guid TaskId { get; private set; }

    /// <summary>When set, the file is attached to a task comment; otherwise to the task itself.</summary>
    public Guid? CommentId { get; private set; }

    public string FileName { get; private set; } = string.Empty;

    public string MediaType { get; private set; } = "application/octet-stream";

    public long Size { get; private set; }

    public string Sha256 { get; private set; } = string.Empty;

    public byte[] Data { get; private set; } = [];

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public bool IsImage => MediaType.StartsWith("image/", StringComparison.Ordinal);

    public bool IsDeleted => DeletedAt is not null;

    public static TaskAttachment Create(Guid taskId, Guid? commentId, string fileName, byte[] data, Guid userId, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(data);
        return new TaskAttachment
        {
            TaskId = taskId,
            CommentId = commentId,
            FileName = Attachment.CleanFileName(fileName),
            MediaType = Attachment.Sniff(data),
            Size = data.LongLength,
            Sha256 = Convert.ToHexStringLower(SHA256.HashData(data)),
            Data = data,
            CreatedBy = userId,
            CreatedAt = now,
        };
    }

    public void SoftDelete(Guid by, DateTimeOffset now)
    {
        if (IsDeleted)
        {
            return;
        }

        DeletedAt = now;
        DeletedBy = by;
        Data = [];
    }
}

/// <summary>Per-user watch subscription on a task (B4). Mirrors page <see cref="Watch"/> states.</summary>
public sealed class TaskWatcher : Entity, ITenantOwned
{
    private TaskWatcher() { }

    public Guid TenantId { get; private set; }

    public Guid TaskId { get; private set; }

    public Guid UserId { get; private set; }

    public WatchState State { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static TaskWatcher Create(Guid taskId, Guid userId, WatchState state, DateTimeOffset now) =>
        new() { TaskId = taskId, UserId = userId, State = state, UpdatedAt = now };

    public void Set(WatchState state, DateTimeOffset now)
    {
        State = state;
        UpdatedAt = now;
    }
}
