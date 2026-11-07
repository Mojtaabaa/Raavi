using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public sealed record CommentAnchorDto(string Exact, string Prefix, string Suffix);

public sealed record ReactionDto(string Emoji, int Count, bool Mine);

public sealed record CommentRightsDto(bool CanEdit, bool CanDelete);

public sealed record CommentDto(
    Guid Id, Guid ThreadId, Guid? ParentId, JsonElement? Body, bool IsDeleted, Guid? AuthorId, string? AuthorName,
    DateTimeOffset CreatedAt, DateTimeOffset? EditedAt, int PageVersion, IReadOnlyList<ReactionDto> Reactions, int Revision, CommentRightsDto Rights);

public sealed record ThreadDto(
    Guid Id, CommentKind Kind, CommentAnchorDto? Anchor, CommentStatus Status, string? ResolvedByName, DateTimeOffset? ResolvedAt,
    bool CanResolve, bool CanReply, IReadOnlyList<CommentDto> Comments);

public sealed record CommentsDto(IReadOnlyList<ThreadDto> Threads, bool CanComment, bool Watching, IReadOnlyList<string> Reactions);

public sealed record AnchorRequest(string? Exact, string? Prefix, string? Suffix);

public sealed record CreateCommentRequest(JsonElement Body, CommentKind? Kind, AnchorRequest? Anchor);

public sealed record CommentBodyRequest(JsonElement Body);

public sealed record ReactionRequest(string Emoji, bool On);

public sealed record WatchDto(bool Watching);

/// <summary>Outbox payload of comment events: ids, a title snapshot and the excerpt (already visible to recipients that pass View).</summary>
public sealed record CommentEvent(Guid SpaceId, Guid ContentId, Guid CommentId, Guid ThreadId, Guid ActorId, string Title, string Excerpt, bool IsReply, IReadOnlyList<Guid> Mentions);

public static class CommentEventTypes
{
    public const string Created = "comment.created";
    public const string Resolved = "comment.resolved";
}

public static class CommentErrors
{
    public static readonly Error NotFound = new("comment.not_found", "Comment not found.", ErrorKind.NotFound);
}

/// <summary>
/// Page and inline comments, reactions and watching (ADR-024). Reading needs View on the page; writing needs the space's
/// Comment right on a current page. Authors edit their own comments; authors and space administrators delete; the thread
/// author, page editors and space administrators resolve. Every change that should notify someone writes an outbox
/// event in the same transaction — recipients are computed (and their access re-checked) by <see cref="NotificationFanOut"/>.
/// </summary>
public sealed class CommentService(WikiDbContext db, PageService pages, IPrincipalDirectory directory, ICurrentUser current, IClock clock)
{
    public const int MaxCommentsPerPage = 2000;

    public async Task<Result<CommentsDto>> ListAsync(Guid contentId, bool includeResolved, CancellationToken ct)
    {
        var loaded = await pages.LoadAsync(contentId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<CommentsDto>.Failure(loaded.Error!);
        }

        var all = await db.Comments.AsNoTracking().Where(c => c.ContentId == contentId).OrderBy(c => c.CreatedAt).Take(MaxCommentsPerPage).ToListAsync(ct).ConfigureAwait(false);
        var roots = all.Where(c => c.IsRoot && (includeResolved || c.Status == CommentStatus.Open)).ToList();
        var rootIds = roots.Select(r => r.Id).ToHashSet();
        var visible = all.Where(c => rootIds.Contains(c.ThreadId)).ToList();
        var threads = await ToThreadsAsync(loaded.Value, roots, visible, ct).ConfigureAwait(false);
        var watching = await IsWatchingAsync(contentId, ct).ConfigureAwait(false);
        return Result<CommentsDto>.Success(new CommentsDto(threads, loaded.Value.Rights.CanComment, watching, CommentReaction.Allowed));
    }

    public async Task<Result<ThreadDto>> CreateAsync(Guid contentId, CreateCommentRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadForCommentAsync(contentId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<ThreadDto>.Failure(loaded.Error!);
        }

        var node = loaded.Value.Node;
        var kind = request.Kind ?? CommentKind.Page;
        var anchor = kind == CommentKind.Inline ? CommentAnchor.Create(request.Anchor?.Exact, request.Anchor?.Prefix, request.Anchor?.Suffix) : null;
        var body = RaaviDocument.Parse(request.Body);
        var now = clock.UtcNow;
        var comment = Comment.StartThread(node.Id, kind, anchor, body, node.CurrentVersionNumber, current.UserId, now);
        db.Comments.Add(comment);
        await AutoWatchAsync(node.Id, current.UserId, now, ct).ConfigureAwait(false);
        await PublishAsync(CommentEventTypes.Created, loaded.Value, comment, body, now, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var thread = await ToThreadsAsync(loaded.Value, [comment], [comment], ct).ConfigureAwait(false);
        return Result<ThreadDto>.Success(thread[0]);
    }

    public async Task<Result<CommentDto>> ReplyAsync(Guid commentId, CommentBodyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = await LoadCommentAsync(commentId, track: true, forWrite: true, ct).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return Result<CommentDto>.Failure(target.Error!);
        }

        var (loaded, root) = target.Value;
        if (!root.IsRoot)
        {
            root = await db.Comments.FirstAsync(c => c.Id == root.ThreadId, ct).ConfigureAwait(false);
        }

        var body = RaaviDocument.Parse(request.Body);
        var now = clock.UtcNow;
        var reply = root.Reply(body, loaded.Node.CurrentVersionNumber, current.UserId, now);
        db.Comments.Add(reply);
        await AutoWatchAsync(loaded.Node.Id, current.UserId, now, ct).ConfigureAwait(false);
        await PublishAsync(CommentEventTypes.Created, loaded, reply, body, now, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<CommentDto>.Success((await ToDtosAsync(loaded, [reply], ct).ConfigureAwait(false))[0]);
    }

    public async Task<Result<CommentDto>> EditAsync(Guid commentId, int expectedRevision, CommentBodyRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = await LoadCommentAsync(commentId, track: true, forWrite: true, ct).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return Result<CommentDto>.Failure(target.Error!);
        }

        var (loaded, comment) = target.Value;
        if (comment.AuthorId != current.UserId)
        {
            return Result<CommentDto>.Failure(WikiErrors.Forbidden);
        }

        if (comment.Revision != expectedRevision)
        {
            return Result<CommentDto>.Failure(ETags.PreconditionFailed);
        }

        comment.Edit(RaaviDocument.Parse(request.Body), clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<CommentDto>.Success((await ToDtosAsync(loaded, [comment], ct).ConfigureAwait(false))[0]);
    }

    public async Task<Result> DeleteAsync(Guid commentId, CancellationToken ct)
    {
        var target = await LoadCommentAsync(commentId, track: true, forWrite: false, ct).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return Result.Failure(target.Error!);
        }

        var (loaded, comment) = target.Value;
        var mayDelete = (comment.AuthorId == current.UserId && loaded.Rights.CanComment) || loaded.Rights.CanModerateComments;
        if (!mayDelete)
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        comment.Delete(current.UserId, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<ThreadDto>> SetResolvedAsync(Guid threadId, bool resolved, CancellationToken ct)
    {
        var target = await LoadCommentAsync(threadId, track: true, forWrite: true, ct).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return Result<ThreadDto>.Failure(target.Error!);
        }

        var (loaded, root) = target.Value;
        if (!root.IsRoot)
        {
            return Result<ThreadDto>.Failure(CommentErrors.NotFound);
        }

        if (!CanResolve(loaded, root))
        {
            return Result<ThreadDto>.Failure(WikiErrors.Forbidden);
        }

        var now = clock.UtcNow;
        var wasOpen = root.Status == CommentStatus.Open;
        if (resolved)
        {
            root.Resolve(current.UserId, now);
            if (wasOpen)
            {
                db.Outbox.Add(Raavi.Infrastructure.Messaging.OutboxMessage.Create(db.CurrentTenantId, CommentEventTypes.Resolved,
                    new CommentEvent(loaded.Node.SpaceId, loaded.Node.Id, root.Id, root.Id, current.UserId, loaded.Node.Title, root.Excerpt, false, []), now));
            }
        }
        else
        {
            root.Reopen();
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        var comments = await db.Comments.AsNoTracking().Where(c => c.ThreadId == root.Id).OrderBy(c => c.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        return Result<ThreadDto>.Success((await ToThreadsAsync(loaded, [root], comments, ct).ConfigureAwait(false))[0]);
    }

    public async Task<Result<IReadOnlyList<ReactionDto>>> ReactAsync(Guid commentId, ReactionRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var target = await LoadCommentAsync(commentId, track: false, forWrite: true, ct).ConfigureAwait(false);
        if (!target.IsSuccess)
        {
            return Result<IReadOnlyList<ReactionDto>>.Failure(target.Error!);
        }

        var comment = target.Value.Comment;
        if (comment.IsDeleted)
        {
            return Result<IReadOnlyList<ReactionDto>>.Failure(CommentErrors.NotFound);
        }

        var me = current.UserId;
        var existing = await db.CommentReactions.FirstOrDefaultAsync(r => r.CommentId == comment.Id && r.UserId == me && r.Emoji == request.Emoji, ct).ConfigureAwait(false);
        if (request.On && existing is null)
        {
            db.CommentReactions.Add(CommentReaction.Create(comment.Id, me, request.Emoji, clock.UtcNow));
        }
        else if (!request.On && existing is not null)
        {
            db.CommentReactions.Remove(existing);
        }

        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException) when (request.On)
        {
            // A concurrent request added the same reaction: the desired state is reached.
            db.ChangeTracker.Clear();
        }

        var reactions = await db.CommentReactions.AsNoTracking().Where(r => r.CommentId == comment.Id).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<ReactionDto>>.Success(Summarize(reactions, me));
    }

    // ── Watching ───────────────────────────────────────────────────────────────────────────────

    public async Task<Result<WatchDto>> GetWatchAsync(Guid contentId, CancellationToken ct)
    {
        var loaded = await pages.LoadAsync(contentId, track: false, ct).ConfigureAwait(false);
        return loaded.IsSuccess
            ? Result<WatchDto>.Success(new WatchDto(await IsWatchingAsync(contentId, ct).ConfigureAwait(false)))
            : Result<WatchDto>.Failure(loaded.Error!);
    }

    public async Task<Result<WatchDto>> SetWatchAsync(Guid contentId, bool watching, CancellationToken ct)
    {
        var loaded = await pages.LoadAsync(contentId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WatchDto>.Failure(loaded.Error!);
        }

        var me = current.UserId;
        var state = watching ? WatchState.Watching : WatchState.Muted;
        var row = await db.Watches.FirstOrDefaultAsync(w => w.ContentId == contentId && w.UserId == me, ct).ConfigureAwait(false);
        if (row is null)
        {
            db.Watches.Add(Watch.Create(contentId, me, state, clock.UtcNow));
        }
        else
        {
            row.Set(state, clock.UtcNow);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<WatchDto>.Success(new WatchDto(watching));
    }

    /// <summary>Starts watching unless the user explicitly unwatched (called on commenting and publishing).</summary>
    public static async Task AutoWatchAsync(WikiDbContext db, Guid contentId, Guid userId, DateTimeOffset now, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tracked = db.Watches.Local.FirstOrDefault(w => w.ContentId == contentId && w.UserId == userId);
        if (tracked is not null || await db.Watches.AnyAsync(w => w.ContentId == contentId && w.UserId == userId, ct).ConfigureAwait(false))
        {
            return;
        }

        db.Watches.Add(Watch.Create(contentId, userId, WatchState.Watching, now));
    }

    private Task AutoWatchAsync(Guid contentId, Guid userId, DateTimeOffset now, CancellationToken ct) => AutoWatchAsync(db, contentId, userId, now, ct);

    private async Task<bool> IsWatchingAsync(Guid contentId, CancellationToken ct)
    {
        var me = current.UserId;
        return await db.Watches.AsNoTracking().AnyAsync(w => w.ContentId == contentId && w.UserId == me && w.State == WatchState.Watching, ct).ConfigureAwait(false);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private async Task PublishAsync(string type, LoadedContent loaded, Comment comment, RaaviDocument body, DateTimeOffset now, CancellationToken ct)
    {
        // Only real members of this tenant can be mentioned; others are dropped silently.
        var mentioned = Comment.MentionedUsers(body);
        var members = mentioned.Count == 0 ? new Dictionary<Guid, string>() : await directory.GetUserNamesAsync(mentioned, ct).ConfigureAwait(false);
        var mentions = mentioned.Where(members.ContainsKey).ToList();
        db.Outbox.Add(Raavi.Infrastructure.Messaging.OutboxMessage.Create(db.CurrentTenantId, type,
            new CommentEvent(loaded.Node.SpaceId, loaded.Node.Id, comment.Id, comment.ThreadId, comment.AuthorId ?? Guid.Empty, loaded.Node.Title, comment.Excerpt, !comment.IsRoot, mentions), now));
    }

    private async Task<Result<LoadedContent>> LoadForCommentAsync(Guid contentId, CancellationToken ct)
    {
        var loaded = await pages.LoadAsync(contentId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        return loaded.Value.Rights.CanComment ? loaded : Result<LoadedContent>.Failure(WikiErrors.Forbidden);
    }

    private async Task<Result<(LoadedContent Loaded, Comment Comment)>> LoadCommentAsync(Guid commentId, bool track, bool forWrite, CancellationToken ct)
    {
        var query = track ? db.Comments : db.Comments.AsNoTracking();
        var comment = await query.FirstOrDefaultAsync(c => c.Id == commentId, ct).ConfigureAwait(false);
        if (comment is null)
        {
            return Result<(LoadedContent, Comment)>.Failure(CommentErrors.NotFound);
        }

        // Access to a comment is access to its page: callers without View get "not found" for both.
        var loaded = await pages.LoadAsync(comment.ContentId, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<(LoadedContent, Comment)>.Failure(CommentErrors.NotFound);
        }

        if (forWrite && !loaded.Value.Rights.CanComment)
        {
            return Result<(LoadedContent, Comment)>.Failure(WikiErrors.Forbidden);
        }

        return Result<(LoadedContent, Comment)>.Success((loaded.Value, comment));
    }

    private bool CanResolve(LoadedContent loaded, Comment root) =>
        loaded.Rights.CanComment && (root.AuthorId == current.UserId || loaded.Rights.CanEdit || loaded.Rights.CanModerateComments);

    private async Task<IReadOnlyList<ThreadDto>> ToThreadsAsync(LoadedContent loaded, IReadOnlyList<Comment> roots, IReadOnlyList<Comment> comments, CancellationToken ct)
    {
        var dtos = await ToDtosAsync(loaded, comments, ct, roots.Where(r => r.ResolvedBy is not null).Select(r => r.ResolvedBy!.Value)).ConfigureAwait(false);
        var byId = dtos.ToDictionary(d => d.Id);
        var names = _names;
        return roots.Select(r =>
        {
            var anchor = CommentAnchor.FromJson(r.AnchorJson);
            return new ThreadDto(
                r.Id, r.Kind, anchor is null ? null : new CommentAnchorDto(anchor.Exact, anchor.Prefix, anchor.Suffix), r.Status,
                r.ResolvedBy is { } by ? names.GetValueOrDefault(by) : null, r.ResolvedAt,
                CanResolve(loaded, r), loaded.Rights.CanComment,
                comments.Where(c => c.ThreadId == r.Id).Select(c => byId[c.Id]).ToList());
        }).ToList();
    }

    private IReadOnlyDictionary<Guid, string> _names = new Dictionary<Guid, string>();

    private async Task<IReadOnlyList<CommentDto>> ToDtosAsync(LoadedContent loaded, IReadOnlyList<Comment> comments, CancellationToken ct, IEnumerable<Guid>? extraUsers = null)
    {
        var ids = comments.Select(c => c.Id).ToList();
        var reactions = ids.Count == 0 ? [] : await db.CommentReactions.AsNoTracking().Where(r => ids.Contains(r.CommentId)).ToListAsync(ct).ConfigureAwait(false);
        _names = await directory.GetUserNamesAsync(comments.Where(c => c.AuthorId is not null).Select(c => c.AuthorId!.Value).Concat(extraUsers ?? []).Distinct().ToList(), ct).ConfigureAwait(false);
        var me = current.UserId;
        return comments.Select(c =>
        {
            var mine = c.AuthorId == me;
            var rights = new CommentRightsDto(
                CanEdit: !c.IsDeleted && mine && loaded.Rights.CanComment,
                CanDelete: !c.IsDeleted && ((mine && loaded.Rights.CanComment) || loaded.Rights.CanModerateComments));
            JsonElement? body = c.IsDeleted ? null : JsonDocument.Parse(c.BodyJson).RootElement.Clone();
            return new CommentDto(c.Id, c.ThreadId, c.ParentId, body, c.IsDeleted, c.AuthorId, c.AuthorId is { } author ? _names.GetValueOrDefault(author) : c.SourceAuthorName, c.CreatedAt, c.EditedAt, c.PageVersion,
                Summarize(reactions.Where(r => r.CommentId == c.Id), me), c.Revision, rights);
        }).ToList();
    }

    private static List<ReactionDto> Summarize(IEnumerable<CommentReaction> reactions, Guid me) =>
        reactions.GroupBy(r => r.Emoji)
            .OrderBy(g => CommentReaction.Allowed.ToList().IndexOf(g.Key))
            .Select(g => new ReactionDto(g.Key, g.Count(), g.Any(r => r.UserId == me)))
            .ToList();
}
