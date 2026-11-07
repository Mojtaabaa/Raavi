using System.Text.Json;
using System.Text.Json.Nodes;
using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

public enum CommentKind
{
    /// <summary>Discussion about the page as a whole (bottom of the page).</summary>
    Page = 1,

    /// <summary>Anchored to a passage of the published text (ADR-024).</summary>
    Inline = 2,
}

public enum CommentStatus
{
    Open = 1,
    Resolved = 2,
}

/// <summary>
/// Where an inline thread is anchored: a W3C-style text-quote selector over the page's plain text (ADR-024). Stored
/// beside the comment instead of as a mark in the document, so commenting never changes page content and does not need
/// Edit. When the quoted text no longer occurs in the current version the thread is shown as "orphaned".
/// </summary>
public sealed record CommentAnchor(string Exact, string Prefix, string Suffix)
{
    public const int MaxExactLength = 500;
    public const int MaxContextLength = 64;

    public static CommentAnchor Create(string? exact, string? prefix, string? suffix)
    {
        var e = (exact ?? string.Empty).Trim();
        if (e.Length is 0 or > MaxExactLength)
        {
            throw new DomainException("comment.anchor_invalid", $"The quoted text must be 1–{MaxExactLength} characters.");
        }

        var p = prefix ?? string.Empty;
        var s = suffix ?? string.Empty;
        return new CommentAnchor(e, p.Length > MaxContextLength ? p[^MaxContextLength..] : p, s.Length > MaxContextLength ? s[..MaxContextLength] : s);
    }

    public string ToJson() => JsonSerializer.Serialize(new { exact = Exact, prefix = Prefix, suffix = Suffix });

    public static CommentAnchor? FromJson(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        var o = JsonNode.Parse(json)!.AsObject();
        return new CommentAnchor((string?)o["exact"] ?? string.Empty, (string?)o["prefix"] ?? string.Empty, (string?)o["suffix"] ?? string.Empty);
    }
}

/// <summary>
/// A comment on a page (ADR-024). The first comment of a thread is its root (<see cref="ThreadId"/> = own id); replies
/// are flat (one level), so <see cref="ParentId"/> is always the root. Status, kind and anchor live on the root. Bodies
/// are Raavi documents (mentions, links, basic formatting), validated by the editor schema. Deletion is soft: the row
/// stays so replies keep their thread, but the body is cleared.
/// </summary>
public sealed class Comment : Entity, ITenantOwned
{
    /// <summary>Canonical JSON limit of one comment body.</summary>
    public const int MaxBodyBytes = 32 * 1024;
    public const int MaxExcerptLength = 280;
    public const int MaxMentions = 20;

    private Comment() { }

    public Guid TenantId { get; private set; }

    public Guid ContentId { get; private set; }

    public Guid ThreadId { get; private set; }

    public Guid? ParentId { get; private set; }

    public CommentKind Kind { get; private set; }

    public string? AnchorJson { get; private set; }

    public string BodyJson { get; private set; } = string.Empty;

    /// <summary>Plain-text excerpt for notifications and e-mail.</summary>
    public string Excerpt { get; private set; } = string.Empty;

    public CommentStatus Status { get; private set; } = CommentStatus.Open;

    /// <summary>Published version the comment was written against.</summary>
    public int PageVersion { get; private set; }

    /// <summary>The author; null for an imported comment whose source author is not (yet) a workspace member — never an invented user.</summary>
    public Guid? AuthorId { get; private set; }

    /// <summary>Name of the source author of an imported comment (shown when there is no <see cref="AuthorId"/>).</summary>
    public string? SourceAuthorName { get; private set; }

    /// <summary>Import provenance (idempotency key with <see cref="SourceId"/>).</summary>
    public string? SourceSystem { get; private set; }

    public string? SourceId { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? EditedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public Guid? ResolvedBy { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public int Revision { get; private set; }

    public bool IsRoot => ParentId is null;

    public bool IsDeleted => DeletedAt is not null;

    public static Comment StartThread(Guid contentId, CommentKind kind, CommentAnchor? anchor, RaaviDocument body, int pageVersion, Guid authorId, DateTimeOffset now)
    {
        if (kind == CommentKind.Inline && anchor is null)
        {
            throw new DomainException("comment.anchor_required", "An inline comment needs the quoted text it refers to.");
        }

        var c = new Comment
        {
            ContentId = contentId,
            Kind = kind,
            AnchorJson = kind == CommentKind.Inline ? anchor!.ToJson() : null,
            PageVersion = pageVersion,
            AuthorId = authorId,
            CreatedAt = now,
            Revision = 1,
        };
        c.ThreadId = c.Id;
        c.SetBody(body);
        return c;
    }

    /// <summary>An imported comment keeps its original time and author name; nothing is notified. Replies pass the thread root.</summary>
    public static Comment Import(Guid contentId, Comment? root, CommentKind kind, CommentAnchor? anchor, RaaviDocument body, int pageVersion, Guid? authorId, string? authorName, DateTimeOffset createdAt, string sourceSystem, string sourceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceSystem);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceId);
        if (root is null && kind == CommentKind.Inline && anchor is null)
        {
            throw new DomainException("comment.anchor_required", "An inline comment needs the quoted text it refers to.");
        }

        var c = new Comment
        {
            ContentId = contentId,
            Kind = root?.Kind ?? kind,
            AnchorJson = root is null && kind == CommentKind.Inline ? anchor!.ToJson() : null,
            PageVersion = pageVersion,
            AuthorId = authorId,
            SourceAuthorName = authorName is { Length: > 200 } ? authorName[..200] : authorName,
            CreatedAt = createdAt,
            Revision = 1,
            SourceSystem = sourceSystem,
            SourceId = sourceId.Length > 128 ? sourceId[..128] : sourceId,
        };
        c.ThreadId = root?.Id ?? c.Id;
        c.ParentId = root?.Id;
        c.SetBody(body);
        return c;
    }

    /// <summary>An imported thread that was resolved in the source stays resolved (by nobody in particular).</summary>
    public void MarkImportedResolved(DateTimeOffset at)
    {
        EnsureRoot();
        Status = CommentStatus.Resolved;
        ResolvedAt = at;
    }

    public Comment Reply(RaaviDocument body, int pageVersion, Guid authorId, DateTimeOffset now)
    {
        if (!IsRoot)
        {
            throw new DomainException("comment.reply_to_reply", "Replies are added to the thread's first comment.");
        }

        var r = new Comment
        {
            ContentId = ContentId,
            ThreadId = Id,
            ParentId = Id,
            Kind = Kind,
            PageVersion = pageVersion,
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

    public void Delete(Guid by, DateTimeOffset now)
    {
        if (IsDeleted)
        {
            return;
        }

        DeletedAt = now;
        DeletedBy = by;
        BodyJson = string.Empty;
        Excerpt = string.Empty;
        Revision++;
    }

    public void Resolve(Guid by, DateTimeOffset now)
    {
        EnsureRoot();
        if (Status == CommentStatus.Resolved)
        {
            return;
        }

        Status = CommentStatus.Resolved;
        ResolvedBy = by;
        ResolvedAt = now;
        Revision++;
    }

    public void Reopen()
    {
        EnsureRoot();
        if (Status == CommentStatus.Open)
        {
            return;
        }

        Status = CommentStatus.Open;
        ResolvedBy = null;
        ResolvedAt = null;
        Revision++;
    }

    /// <summary>Distinct user ids of mention nodes in the body (at most <see cref="MaxMentions"/>).</summary>
    public static IReadOnlyList<Guid> MentionedUsers(RaaviDocument body)
    {
        ArgumentNullException.ThrowIfNull(body);
        var ids = new List<Guid>();
        Walk(body.Doc);
        return ids;

        void Walk(JsonNode? node)
        {
            if (node is not JsonObject o || ids.Count >= MaxMentions)
            {
                return;
            }

            if ((string?)o["type"] == "mention" && o["attrs"] is JsonObject attrs && Guid.TryParse((string?)attrs["id"], out var id) && !ids.Contains(id))
            {
                ids.Add(id);
            }

            if (o["content"] is JsonArray children)
            {
                foreach (var child in children)
                {
                    Walk(child);
                }
            }
        }
    }

    private void SetBody(RaaviDocument body)
    {
        ArgumentNullException.ThrowIfNull(body);
        if (System.Text.Encoding.UTF8.GetByteCount(body.Json) > MaxBodyBytes)
        {
            throw new DomainException("comment.too_large", "The comment is too long.");
        }

        var text = body.PlainText.Trim();
        if (text.Length == 0 && MentionedUsers(body).Count == 0)
        {
            throw new DomainException("comment.empty", "The comment is empty.");
        }

        BodyJson = body.Json;
        Excerpt = text.Length > MaxExcerptLength ? text[..(MaxExcerptLength - 1)] + "…" : text;
    }

    private void EnsureRoot()
    {
        if (!IsRoot)
        {
            throw new DomainException("comment.not_thread", "Only a thread can be resolved or reopened.");
        }
    }

    private void EnsureNotDeleted()
    {
        if (IsDeleted)
        {
            throw new DomainException("comment.deleted", "This comment was deleted.");
        }
    }
}

/// <summary>One user's reaction to a comment; the emoji comes from a fixed allow-list.</summary>
public sealed class CommentReaction : ITenantOwned
{
    public static readonly IReadOnlyList<string> Allowed = ["👍", "❤️", "🎉", "😄", "👀", "✅"];

    private CommentReaction() { }

    public Guid TenantId { get; private set; }

    public Guid CommentId { get; private set; }

    public Guid UserId { get; private set; }

    public string Emoji { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public static CommentReaction Create(Guid commentId, Guid userId, string emoji, DateTimeOffset now) =>
        Allowed.Contains(emoji)
            ? new CommentReaction { CommentId = commentId, UserId = userId, Emoji = emoji, CreatedAt = now }
            : throw new DomainException("comment.reaction_invalid", "This reaction is not available.");
}

public enum WatchState
{
    Watching = 1,

    /// <summary>Explicitly unwatched: automatic watching (on authoring or commenting) must not re-add the user.</summary>
    Muted = 2,
}

/// <summary>A user's subscription to a page's comments and publications.</summary>
public sealed class Watch : Entity, ITenantOwned
{
    private Watch() { }

    public Guid TenantId { get; private set; }

    public Guid ContentId { get; private set; }

    public Guid UserId { get; private set; }

    public WatchState State { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static Watch Create(Guid contentId, Guid userId, WatchState state, DateTimeOffset now) =>
        new() { ContentId = contentId, UserId = userId, State = state, UpdatedAt = now };

    public void Set(WatchState state, DateTimeOffset now)
    {
        State = state;
        UpdatedAt = now;
    }
}

public static class NotificationKinds
{
    public const string Mention = "mention";
    public const string Reply = "comment.reply";
    public const string Comment = "comment.created";
    public const string Resolved = "comment.resolved";
    public const string Published = "page.published";
}

public enum EmailDelivery
{
    None = 0,
    Pending = 1,
    Sent = 2,
    Skipped = 3,
    Failed = 4,
}

/// <summary>
/// An in-app notification for one recipient. Created by the outbox handler after the recipient's View right on the page
/// was re-checked; carries ids and a title/excerpt snapshot only. (EventId, RecipientId) is unique, which makes
/// at-least-once delivery idempotent.
/// </summary>
public sealed class Notification : Entity, ITenantOwned
{
    public const int MaxKindLength = 32;

    private Notification() { }

    public Guid TenantId { get; private set; }

    public Guid RecipientId { get; private set; }

    public Guid EventId { get; private set; }

    public string Kind { get; private set; } = string.Empty;

    public Guid SpaceId { get; private set; }

    public Guid ContentId { get; private set; }

    public Guid? CommentId { get; private set; }

    public Guid ActorId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string Excerpt { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ReadAt { get; private set; }

    public EmailDelivery Email { get; private set; }

    public DateTimeOffset? EmailedAt { get; private set; }

    public static Notification Create(Guid eventId, Guid recipientId, string kind, Guid spaceId, Guid contentId, Guid? commentId, Guid actorId, string title, string excerpt, bool email, DateTimeOffset now) => new()
    {
        EventId = eventId,
        RecipientId = recipientId,
        Kind = kind,
        SpaceId = spaceId,
        ContentId = contentId,
        CommentId = commentId,
        ActorId = actorId,
        Title = title,
        Excerpt = excerpt.Length > Comment.MaxExcerptLength ? excerpt[..Comment.MaxExcerptLength] : excerpt,
        CreatedAt = now,
        Email = email ? EmailDelivery.Pending : EmailDelivery.None,
    };

    public void MarkRead(DateTimeOffset now) => ReadAt ??= now;

    public void Emailed(EmailDelivery outcome, DateTimeOffset now)
    {
        Email = outcome;
        EmailedAt = now;
    }
}

/// <summary>Per-user delivery preferences inside one tenant (no row = defaults: e-mail on).</summary>
public sealed class NotificationPreference : Entity, ITenantOwned
{
    private NotificationPreference() { }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public bool EmailEnabled { get; private set; } = true;

    public DateTimeOffset UpdatedAt { get; private set; }

    public static NotificationPreference Create(Guid userId, DateTimeOffset now) => new() { UserId = userId, UpdatedAt = now };

    public void SetEmail(bool enabled, DateTimeOffset now)
    {
        EmailEnabled = enabled;
        UpdatedAt = now;
    }
}
