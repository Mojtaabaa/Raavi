using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Content;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Jobs;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Editor;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

/// <summary>
/// Draft / publish model (ADR-019): one shared autosaved draft per page, publishing creates an immutable version
/// (with all contributors since the previous publish), scheduled publishing via a tenant job, approval requests in
/// spaces that require them, history with structural diff, restore as a new version, trash with restore and purge.
/// </summary>
public sealed class PublishingService(
    WikiDbContext db,
    PageService pages,
    WikiAuthorizer authz,
    WikiActor actor,
    IPrincipalDirectory directory,
    ITenantJobScheduler jobs,
    Raavi.Wiki.Collaboration.CollaborationClient collaboration,
    IClock clock)
{
    // ── Drafts ─────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<DraftDto>> GetDraftAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadVersionedAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<DraftDto>.Failure(loaded.Error!);
        }

        if (!loaded.Value.Rights.CanEdit)
        {
            return Result<DraftDto>.Failure(WikiErrors.Forbidden);
        }

        return Result<DraftDto>.Success(await DraftDtoAsync(loaded.Value.Node, ct).ConfigureAwait(false));
    }

    /// <summary>Autosave. <paramref name="expectedRevision"/> 0 = "no draft yet" (the draft is created).</summary>
    public async Task<Result<DraftDto>> SaveDraftAsync(Guid id, int expectedRevision, SaveDraftRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadVersionedAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<DraftDto>.Failure(loaded.Error!);
        }

        var (node, _, _, rights) = loaded.Value;
        if (!rights.CanEdit)
        {
            return Result<DraftDto>.Failure(WikiErrors.Forbidden);
        }

        var body = request.Body is { } b ? RaaviDocument.Parse(b) : RaaviDocument.Empty;
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.ContentId == id, ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        if (draft is null)
        {
            if (expectedRevision != 0)
            {
                return Result<DraftDto>.Failure(ETags.PreconditionFailed);
            }

            db.Drafts.Add(PageDraft.Start(id, request.Title, body, node.CurrentVersionNumber, actor.UserId, now));
        }
        else
        {
            if (draft.Revision != expectedRevision)
            {
                return Result<DraftDto>.Failure(ETags.PreconditionFailed);
            }

            draft.Save(request.Title, body, actor.UserId, now);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await ResetCollaborationAsync(id, ct).ConfigureAwait(false); // a REST body save replaces any live document state
        return Result<DraftDto>.Success(await DraftDtoAsync(node, ct).ConfigureAwait(false));
    }

    /// <summary>Title of the shared draft (real-time editors edit the body through the collaboration server).</summary>
    public async Task<Result<DraftDto>> SaveDraftTitleAsync(Guid id, string title, CancellationToken ct)
    {
        var loaded = await LoadVersionedAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<DraftDto>.Failure(loaded.Error!);
        }

        if (!loaded.Value.Rights.CanEdit)
        {
            return Result<DraftDto>.Failure(WikiErrors.Forbidden);
        }

        var node = loaded.Value.Node;
        var now = clock.UtcNow;
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.ContentId == id, ct).ConfigureAwait(false);
        if (draft is null)
        {
            var json = node.CurrentVersionId is { } vid ? await db.PageVersions.AsNoTracking().Where(v => v.Id == vid).Select(v => v.ContentJson).FirstAsync(ct).ConfigureAwait(false) : RaaviDocument.Empty.Json;
            db.Drafts.Add(PageDraft.Start(id, title, RaaviDocument.Parse(json), node.CurrentVersionNumber, actor.UserId, now));
        }
        else
        {
            draft.SaveTitle(title, actor.UserId, now);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<DraftDto>.Success(await DraftDtoAsync(node, ct).ConfigureAwait(false));
    }

    /// <summary>
    /// Called for every update stored by the collaboration server: the shared draft follows the live document. A store
    /// that equals the published version (e.g. right after publishing) does not create a draft.
    /// </summary>
    internal async Task ApplyCollaborationBodyAsync(Guid contentId, RaaviDocument body, IReadOnlyList<Guid> contributors, CancellationToken ct)
    {
        var node = await db.Nodes.AsNoTracking().FirstAsync(n => n.Id == contentId, ct).ConfigureAwait(false);
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.ContentId == contentId, ct).ConfigureAwait(false);
        var editor = contributors.FirstOrDefault(c => c != Guid.Empty);
        if (editor == Guid.Empty)
        {
            editor = draft?.UpdatedBy ?? node.UpdatedBy;
        }

        var now = clock.UtcNow;
        if (draft is null)
        {
            var published = node.CurrentVersionId is { } vid
                ? RaaviDocument.Parse(await db.PageVersions.AsNoTracking().Where(v => v.Id == vid).Select(v => v.ContentJson).FirstAsync(ct).ConfigureAwait(false)).Hash
                : null;
            if (published == body.Hash)
            {
                return;
            }

            draft = PageDraft.Start(contentId, node.Title, body, node.CurrentVersionNumber, editor, now);
            db.Drafts.Add(draft);
        }
        else if (draft.ContentHash != body.Hash)
        {
            draft.SaveBody(body, editor, now);
        }

        foreach (var c in contributors.Where(c => c != Guid.Empty))
        {
            draft.AddContributor(c);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private async Task FlushCollaborationAsync(Guid contentId, CancellationToken ct)
    {
        if (await db.CollaborationDocuments.AnyAsync(d => d.ContentId == contentId, ct).ConfigureAwait(false))
        {
            await collaboration.FlushAsync(new Raavi.Wiki.Collaboration.DocumentAddress(db.CurrentTenantId, contentId).Name, ct).ConfigureAwait(false);
            db.ChangeTracker.Clear(); // the flush wrote the draft in another request
        }
    }

    /// <summary>Drops the stored live document and makes the collaboration server unload it; editors reload from the draft.</summary>
    private async Task ResetCollaborationAsync(Guid contentId, CancellationToken ct)
    {
        var docIds = await db.CollaborationDocuments.Where(d => d.ContentId == contentId).Select(d => d.Id).ToListAsync(ct).ConfigureAwait(false);
        if (docIds.Count == 0)
        {
            return;
        }

        await db.CollaborationUpdates.Where(u => docIds.Contains(u.DocumentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.CollaborationSnapshots.Where(s => docIds.Contains(s.DocumentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.CollaborationDocuments.Where(d => docIds.Contains(d.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await collaboration.ResetAsync(new Raavi.Wiki.Collaboration.DocumentAddress(db.CurrentTenantId, contentId).Name, ct).ConfigureAwait(false);
    }

    /// <summary>Discards the draft. A never-published page has nothing else, so it is removed entirely.</summary>
    public async Task<Result> DiscardDraftAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadVersionedAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        if (!loaded.Value.Rights.CanEdit)
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        var node = loaded.Value.Node;
        await ResetCollaborationAsync(id, ct).ConfigureAwait(false);
        await WithdrawPendingAsync(id, ct).ConfigureAwait(false);
        db.Drafts.RemoveRange(await db.Drafts.Where(d => d.ContentId == id).ToListAsync(ct).ConfigureAwait(false));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        if (node.Status == ContentStatus.Draft)
        {
            if (await db.Nodes.AnyAsync(n => n.ParentId == id, ct).ConfigureAwait(false))
            {
                return Result.Failure(new Error("content.has_children", "Move or delete the child pages first.", ErrorKind.Conflict));
            }

            await PurgeSubtreeAsync(id, ct).ConfigureAwait(false);
        }

        return Result.Success();
    }

    // ── Publish ────────────────────────────────────────────────────────────────────────────────

    /// <param name="expectedDraftRevision">Draft revision from If-Match; -1 (<c>If-Match: *</c>) publishes the latest draft — used by
    /// real-time editors, whose draft is written by the collaboration server.</param>
    public async Task<Result<PublishResultDto>> PublishAsync(Guid id, int expectedDraftRevision, PublishRequestBody request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        await FlushCollaborationAsync(id, ct).ConfigureAwait(false);
        var loaded = await LoadVersionedAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<PublishResultDto>.Failure(loaded.Error!);
        }

        var (node, space, _, rights) = loaded.Value;
        if (!rights.CanPublish)
        {
            return Result<PublishResultDto>.Failure(WikiErrors.Forbidden);
        }

        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.ContentId == id, ct).ConfigureAwait(false);
        if (draft is null)
        {
            return Result<PublishResultDto>.Failure(PublishingErrors.NothingToPublish);
        }

        if (expectedDraftRevision >= 0 && draft.Revision != expectedDraftRevision)
        {
            return Result<PublishResultDto>.Failure(ETags.PreconditionFailed);
        }

        var now = clock.UtcNow;
        if (request.PublishAt is { } at && at > now.AddSeconds(30))
        {
            draft.Schedule(at, request.ChangeSummary, actor.UserId);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            jobs.Schedule<ScheduledPublishJob, ScheduledPublishArgs>(new ScheduledPublishArgs(id, at, actor.UserId), at);
            return Result<PublishResultDto>.Success(new PublishResultDto(PublishOutcome.Scheduled, (await pages.GetAsync(id, ct).ConfigureAwait(false)).Value));
        }

        if (request.PublishAt is { } past && past < now.AddMinutes(-1))
        {
            return Result<PublishResultDto>.Failure(PublishingErrors.ScheduleInPast);
        }

        var outcome = await PublishOrRequestAsync(node, space.Space, rights, draft, request.ChangeSummary, now, ct).ConfigureAwait(false);
        return Result<PublishResultDto>.Success(new PublishResultDto(outcome, (await pages.GetAsync(id, ct).ConfigureAwait(false)).Value));
    }

    public async Task<Result<DraftDto>> CancelScheduleAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadVersionedAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<DraftDto>.Failure(loaded.Error!);
        }

        if (!loaded.Value.Rights.CanPublish)
        {
            return Result<DraftDto>.Failure(WikiErrors.Forbidden);
        }

        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.ContentId == id, ct).ConfigureAwait(false);
        if (draft?.ScheduledPublishAt is not null)
        {
            draft.CancelSchedule(); // the queued job finds no matching schedule and does nothing
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return Result<DraftDto>.Success(await DraftDtoAsync(loaded.Value.Node, ct).ConfigureAwait(false));
    }

    /// <summary>Runs inside <see cref="ScheduledPublishJob"/> as the user who scheduled it; rights are re-checked now.</summary>
    internal async Task RunScheduledAsync(ScheduledPublishArgs args, CancellationToken ct)
    {
        actor.ActAs(args.UserId);
        var draft = await db.Drafts.FirstOrDefaultAsync(d => d.ContentId == args.ContentId, ct).ConfigureAwait(false);
        if (draft?.ScheduledPublishAt is not { } at || at != args.PublishAt || draft.ScheduledBy != args.UserId)
        {
            return; // cancelled, rescheduled or already published
        }

        var loaded = await LoadVersionedAsync(args.ContentId, track: true, ct).ConfigureAwait(false);
        var summary = draft.ScheduledChangeSummary;
        draft.CancelSchedule();
        if (!loaded.IsSuccess || !loaded.Value.Rights.CanPublish)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false); // lost access in the meantime: schedule dropped, draft kept
            return;
        }

        await PublishOrRequestAsync(loaded.Value.Node, loaded.Value.Space.Space, loaded.Value.Rights, draft, summary, clock.UtcNow, ct).ConfigureAwait(false);
    }

    private async Task<PublishOutcome> PublishOrRequestAsync(ContentNode node, Space space, ContentRights rights, PageDraft draft, string? summary, DateTimeOffset now, CancellationToken ct)
    {
        if (space.RequirePublishApproval && !rights.CanApprove)
        {
            foreach (var pending in await db.PublishRequests.Where(r => r.ContentId == node.Id && r.Status == PublishRequestStatus.Pending).ToListAsync(ct).ConfigureAwait(false))
            {
                pending.Decide(PublishRequestStatus.Superseded, actor.UserId, null, now);
            }

            db.PublishRequests.Add(PublishRequest.From(draft, summary, actor.UserId, now));
            db.Publish(WikiEventTypes.ContentPublishRequested, new WikiEvent(node.SpaceId, node.Id, actor.UserId, draft.Title, null), now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return PublishOutcome.PendingApproval;
        }

        await PublishSnapshotAsync(node, draft.Title, RaaviDocument.Parse(draft.ContentJson), summary, draft.ContributorIds, now, ct).ConfigureAwait(false);
        db.Drafts.Remove(draft);
        await WithdrawPendingAsync(node.Id, ct).ConfigureAwait(false);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return PublishOutcome.Published;
    }

    /// <summary>Creates the next immutable version, moves the published pointer and records contributors.</summary>
    private PageVersion AddVersion(ContentNode node, Func<int, PageVersion> create, IEnumerable<Guid> contributors, DateTimeOffset now)
    {
        var version = create(node.CurrentVersionNumber + 1);
        db.PageVersions.Add(version);
        foreach (var user in contributors.Append(actor.UserId).Distinct())
        {
            db.VersionContributors.Add(PageVersionContributor.Create(version.Id, user));
        }

        var firstPublish = node.Status == ContentStatus.Draft;
        node.SetTitle(version.Title);
        node.RecordVersion(version, actor.UserId, now);
        db.Publish(firstPublish ? WikiEventTypes.ContentCreated : WikiEventTypes.ContentPublished, new WikiEvent(node.SpaceId, node.Id, actor.UserId, node.Title, version.VersionNumber), now);
        return version;
    }

    private Task PublishSnapshotAsync(ContentNode node, string title, RaaviDocument body, string? summary, IEnumerable<Guid> contributors, DateTimeOffset now, CancellationToken ct)
    {
        _ = ct;
        AddVersion(node, n => PageVersion.Create(node.Id, n, ContentNode.NormalizeTitle(title), body, summary, actor.UserId, now), contributors, now);
        return Task.CompletedTask;
    }

    private async Task WithdrawPendingAsync(Guid contentId, CancellationToken ct)
    {
        foreach (var pending in await db.PublishRequests.Where(r => r.ContentId == contentId && r.Status == PublishRequestStatus.Pending).ToListAsync(ct).ConfigureAwait(false))
        {
            pending.Decide(PublishRequestStatus.Superseded, actor.UserId, null, clock.UtcNow);
        }
    }

    // ── Approval foundation ────────────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<PublishRequestDto>>> ListRequestsAsync(string spaceKey, bool pendingOnly, CancellationToken ct)
    {
        var space = await pages.Spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<IReadOnlyList<PublishRequestDto>>.Failure(WikiErrors.SpaceNotFound);
        }

        if (!space.CanAdminister)
        {
            return Result<IReadOnlyList<PublishRequestDto>>.Failure(WikiErrors.Forbidden);
        }

        var rows = await (from r in db.PublishRequests.AsNoTracking()
                          join n in db.Nodes.AsNoTracking() on r.ContentId equals n.Id
                          where n.SpaceId == space.Space.Id && (!pendingOnly || r.Status == PublishRequestStatus.Pending)
                          orderby r.RequestedAt descending
                          select r).Take(200).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<PublishRequestDto>>.Success(await RequestDtosAsync(rows, ct).ConfigureAwait(false));
    }

    public async Task<Result<PublishResultDto>> DecideAsync(Guid requestId, bool approve, string? comment, CancellationToken ct)
    {
        var request = await db.PublishRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct).ConfigureAwait(false);
        if (request is null)
        {
            return Result<PublishResultDto>.Failure(PublishingErrors.RequestNotFound);
        }

        var loaded = await LoadVersionedAsync(request.ContentId, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<PublishResultDto>.Failure(PublishingErrors.RequestNotFound);
        }

        var (node, _, _, rights) = loaded.Value;
        var withdraw = !approve && request.RequestedBy == actor.UserId && !rights.CanApprove;
        if (!rights.CanApprove && !withdraw)
        {
            return Result<PublishResultDto>.Failure(WikiErrors.Forbidden);
        }

        var now = clock.UtcNow;
        request.Decide(approve ? PublishRequestStatus.Approved : withdraw ? PublishRequestStatus.Withdrawn : PublishRequestStatus.Rejected, actor.UserId, comment, now);
        if (approve)
        {
            // Exactly the proposed snapshot is published, whatever happened to the draft afterwards.
            await PublishSnapshotAsync(node, request.Title, RaaviDocument.Parse(request.ContentJson), request.ChangeSummary, request.ContributorIds.Append(request.RequestedBy), now, ct).ConfigureAwait(false);
            var draft = await db.Drafts.FirstOrDefaultAsync(d => d.ContentId == node.Id, ct).ConfigureAwait(false);
            if (draft is not null && draft.ContentHash == request.ContentHash && draft.Title == request.Title)
            {
                db.Drafts.Remove(draft);
            }
        }

        db.Publish(approve ? WikiEventTypes.ContentPublishApproved : WikiEventTypes.ContentPublishRejected, new WikiEvent(node.SpaceId, node.Id, actor.UserId, request.Title, null), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<PublishResultDto>.Success(new PublishResultDto(approve ? PublishOutcome.Published : PublishOutcome.PendingApproval, (await pages.GetAsync(node.Id, ct).ConfigureAwait(false)).Value));
    }

    public async Task<Result<SpaceDto>> SetApprovalRequiredAsync(string spaceKey, int expectedRevision, bool required, CancellationToken ct)
    {
        var space = await pages.Spaces.LoadAsync(spaceKey, requireView: false, ct, track: true).ConfigureAwait(false);
        if (space is null || !(space.CanView || space.CanAdminister))
        {
            return Result<SpaceDto>.Failure(WikiErrors.SpaceNotFound);
        }

        if (!space.CanAdminister)
        {
            return Result<SpaceDto>.Failure(WikiErrors.Forbidden);
        }

        if (space.Space.Revision != expectedRevision)
        {
            return Result<SpaceDto>.Failure(ETags.PreconditionFailed);
        }

        space.Space.SetPublishApproval(required, actor.UserId, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await pages.Spaces.GetAsync(spaceKey, ct).ConfigureAwait(false);
    }

    // ── History, diff, restore ─────────────────────────────────────────────────────────────────

    public async Task<Result<VersionDto>> GetVersionAsync(Guid id, int number, CancellationToken ct)
    {
        var loaded = await pages.LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<VersionDto>.Failure(loaded.Error!);
        }

        var v = await db.PageVersions.AsNoTracking().FirstOrDefaultAsync(x => x.ContentId == id && x.VersionNumber == number, ct).ConfigureAwait(false);
        if (v is null)
        {
            return Result<VersionDto>.Failure(PublishingErrors.VersionNotFound);
        }

        var contributors = await db.VersionContributors.AsNoTracking().Where(c => c.VersionId == v.Id).Select(c => c.UserId).ToListAsync(ct).ConfigureAwait(false);
        var names = await directory.GetUserNamesAsync([.. contributors, .. v.CreatedBy is { } c ? [c] : Array.Empty<Guid>()], ct).ConfigureAwait(false);
        using var json = JsonDocument.Parse(RaaviDocument.Parse(v.ContentJson).Json);
        return Result<VersionDto>.Success(new VersionDto(
            v.VersionNumber, v.Title, v.VersionType, v.RestoredFromVersionNumber, json.RootElement.Clone(), v.ChangeSummary,
            v.CreatedBy is { } u ? names.GetValueOrDefault(u) : null, v.CreatedAt,
            contributors.Select(c => names.GetValueOrDefault(c)).OfType<string>().ToList()));
    }

    /// <summary>Compares two versions (<c>to</c> may be "draft"; editors only). Older schema versions are migrated first.</summary>
    public async Task<Result<DiffDto>> DiffAsync(Guid id, int from, string to, CancellationToken ct)
    {
        var loaded = await pages.LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<DiffDto>.Failure(loaded.Error!);
        }

        var left = await db.PageVersions.AsNoTracking().FirstOrDefaultAsync(x => x.ContentId == id && x.VersionNumber == from, ct).ConfigureAwait(false);
        if (left is null)
        {
            return Result<DiffDto>.Failure(PublishingErrors.VersionNotFound);
        }

        string rightTitle, rightJson;
        DiffSideDto rightSide;
        if (string.Equals(to, "draft", StringComparison.OrdinalIgnoreCase))
        {
            var draft = loaded.Value.Rights.CanEdit ? await db.Drafts.AsNoTracking().FirstOrDefaultAsync(d => d.ContentId == id, ct).ConfigureAwait(false) : null;
            if (draft is null)
            {
                return Result<DiffDto>.Failure(PublishingErrors.NothingToPublish);
            }

            (rightTitle, rightJson) = (draft.Title, draft.ContentJson);
            rightSide = new DiffSideDto("draft", null, draft.Title);
        }
        else
        {
            var right = int.TryParse(to, out var n) ? await db.PageVersions.AsNoTracking().FirstOrDefaultAsync(x => x.ContentId == id && x.VersionNumber == n, ct).ConfigureAwait(false) : null;
            if (right is null)
            {
                return Result<DiffDto>.Failure(PublishingErrors.VersionNotFound);
            }

            (rightTitle, rightJson) = (right.Title, right.ContentJson);
            rightSide = new DiffSideDto("version", right.VersionNumber, right.Title);
        }

        var blocks = DocumentDiff.Compare(RaaviDocument.Parse(left.ContentJson).Doc, RaaviDocument.Parse(rightJson).Doc);
        return Result<DiffDto>.Success(new DiffDto(new DiffSideDto("version", left.VersionNumber, left.Title), rightSide, left.Title != rightTitle, blocks));
    }

    /// <summary>Restore creates a new version (history is never rewritten); in approval spaces the old content goes to the draft.</summary>
    public async Task<Result<PublishResultDto>> RestoreVersionAsync(Guid id, int number, int expectedRevision, CancellationToken ct)
    {
        var loaded = await LoadVersionedAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<PublishResultDto>.Failure(loaded.Error!);
        }

        var (node, space, _, rights) = loaded.Value;
        if (!rights.CanPublish)
        {
            return Result<PublishResultDto>.Failure(WikiErrors.Forbidden);
        }

        if (node.Revision != expectedRevision)
        {
            return Result<PublishResultDto>.Failure(ETags.PreconditionFailed);
        }

        var source = await db.PageVersions.AsNoTracking().FirstOrDefaultAsync(x => x.ContentId == id && x.VersionNumber == number, ct).ConfigureAwait(false);
        if (source is null)
        {
            return Result<PublishResultDto>.Failure(PublishingErrors.VersionNotFound);
        }

        var now = clock.UtcNow;
        if (space.Space.RequirePublishApproval && !rights.CanApprove)
        {
            var body = RaaviDocument.Parse(source.ContentJson);
            var draft = await db.Drafts.FirstOrDefaultAsync(d => d.ContentId == id, ct).ConfigureAwait(false);
            if (draft is null)
            {
                db.Drafts.Add(PageDraft.Start(id, source.Title, body, node.CurrentVersionNumber, actor.UserId, now));
            }
            else
            {
                draft.Save(source.Title, body, actor.UserId, now);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await ResetCollaborationAsync(id, ct).ConfigureAwait(false);
            return Result<PublishResultDto>.Success(new PublishResultDto(PublishOutcome.RestoredToDraft, (await pages.GetAsync(id, ct).ConfigureAwait(false)).Value));
        }

        AddVersion(node, n => PageVersion.Restore(id, n, source, actor.UserId, now), [], now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<PublishResultDto>.Success(new PublishResultDto(PublishOutcome.Published, (await pages.GetAsync(id, ct).ConfigureAwait(false)).Value));
    }

    // ── Trash ──────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<TrashItemDto>>> TrashAsync(string spaceKey, CancellationToken ct)
    {
        var space = await pages.Spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<IReadOnlyList<TrashItemDto>>.Failure(WikiErrors.SpaceNotFound);
        }

        if (!space.CanAdminister)
        {
            return Result<IReadOnlyList<TrashItemDto>>.Failure(WikiErrors.Forbidden);
        }

        // Roots: trashed nodes whose parent was not trashed together with them.
        var roots = await (from n in db.Nodes.AsNoTracking()
                           where n.SpaceId == space.Space.Id && n.Status == ContentStatus.Trashed
                                 && !db.Nodes.Any(p => p.Id == n.ParentId && p.Status == ContentStatus.Trashed && p.DeletedAt == n.DeletedAt)
                           orderby n.DeletedAt descending
                           select new { n.Id, n.Title, n.ContentType, n.DeletedAt, n.DeletedBy, Descendants = db.Closure.Count(c => c.AncestorId == n.Id) - 1 })
            .Take(500).ToListAsync(ct).ConfigureAwait(false);
        var names = await directory.GetUserNamesAsync(roots.Where(r => r.DeletedBy != null).Select(r => r.DeletedBy!.Value).ToList(), ct).ConfigureAwait(false);
        return Result<IReadOnlyList<TrashItemDto>>.Success(roots
            .Select(r => new TrashItemDto(r.Id, r.Title, r.ContentType, r.DeletedAt!.Value, r.DeletedBy is { } u ? names.GetValueOrDefault(u) : null, r.Descendants))
            .ToList());
    }

    public async Task<Result<PageDto>> RestoreFromTrashAsync(Guid id, CancellationToken ct)
    {
        var trashed = await LoadTrashedAsync(id, ct).ConfigureAwait(false);
        if (!trashed.IsSuccess)
        {
            return Result<PageDto>.Failure(trashed.Error!);
        }

        var node = trashed.Value;
        var now = clock.UtcNow;
        var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (tx.ConfigureAwait(false))
        {
            // Parent gone or not current → restore at the top level of the space.
            if (node.ParentId is { } parentId && !await db.Nodes.AnyAsync(n => n.Id == parentId && n.Status == ContentStatus.Current, ct).ConfigureAwait(false))
            {
                node.MoveTo(node.SpaceId, null, actor.UserId, now);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                await ClosureOps.RelinkAsync(db, node.Id, null, ct).ConfigureAwait(false);
            }

            var deletedAt = node.DeletedAt;
            var subtree = db.Closure.Where(c => c.AncestorId == node.Id && c.DescendantId != node.Id).Select(c => c.DescendantId);
            node.SetStatus(ContentStatus.Current, actor.UserId, now);
            db.Publish(WikiEventTypes.ContentRestored, new WikiEvent(node.SpaceId, node.Id, actor.UserId, node.Title, null), now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await db.Nodes.Where(n => subtree.Contains(n.Id) && n.Status == ContentStatus.Trashed && n.DeletedAt == deletedAt)
                .ExecuteUpdateAsync(s => s.SetProperty(n => n.Status, ContentStatus.Current).SetProperty(n => n.DeletedAt, (DateTimeOffset?)null).SetProperty(n => n.DeletedBy, (Guid?)null).SetProperty(n => n.Revision, n => n.Revision + 1), ct)
                .ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        db.ChangeTracker.Clear();
        return await pages.GetAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>Permanently deletes trashed content and everything that belongs to it (space administrators).</summary>
    public async Task<Result> PurgeAsync(Guid id, CancellationToken ct)
    {
        var trashed = await LoadTrashedAsync(id, ct).ConfigureAwait(false);
        if (!trashed.IsSuccess)
        {
            return Result.Failure(trashed.Error!);
        }

        await PurgeSubtreeAsync(id, ct).ConfigureAwait(false);
        db.Publish(WikiEventTypes.ContentPurged, new WikiEvent(trashed.Value.SpaceId, null, actor.UserId, trashed.Value.Title, null), clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<Result<ContentNode>> LoadTrashedAsync(Guid id, CancellationToken ct)
    {
        var node = await db.Nodes.FirstOrDefaultAsync(n => n.Id == id, ct).ConfigureAwait(false);
        if (node is null)
        {
            return Result<ContentNode>.Failure(WikiErrors.ContentNotFound);
        }

        var space = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == node.SpaceId, ct).ConfigureAwait(false);
        var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        if (!rights.CanView)
        {
            return Result<ContentNode>.Failure(WikiErrors.ContentNotFound);
        }

        if (!rights.CanAdminister || space.Status != SpaceStatus.Active)
        {
            return Result<ContentNode>.Failure(WikiErrors.Forbidden);
        }

        return node.Status == ContentStatus.Trashed ? Result<ContentNode>.Success(node) : Result<ContentNode>.Failure(PublishingErrors.NotTrashed);
    }

    private async Task PurgeSubtreeAsync(Guid id, CancellationToken ct)
    {
        var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (tx.ConfigureAwait(false))
        {
            var ids = await db.Closure.Where(c => c.AncestorId == id).Select(c => c.DescendantId).ToListAsync(ct).ConfigureAwait(false);
            var versionIds = db.PageVersions.Where(v => ids.Contains(v.ContentId)).Select(v => v.Id);
            await db.VersionContributors.Where(c => versionIds.Contains(c.VersionId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.ContentLabels.Where(l => ids.Contains(l.ContentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.Restrictions.Where(r => ids.Contains(r.ContentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.Attachments.Where(a => ids.Contains(a.ContentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.PublishRequests.Where(r => ids.Contains(r.ContentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.Drafts.Where(d => ids.Contains(d.ContentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            var collabDocs = db.CollaborationDocuments.Where(d => ids.Contains(d.ContentId)).Select(d => d.Id);
            await db.CollaborationUpdates.Where(u => collabDocs.Contains(u.DocumentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.CollaborationSnapshots.Where(s => collabDocs.Contains(s.DocumentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.CollaborationDocuments.Where(d => ids.Contains(d.ContentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.PageVersions.Where(v => ids.Contains(v.ContentId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.Closure.Where(c => ids.Contains(c.DescendantId) || ids.Contains(c.AncestorId)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await db.Nodes.Where(n => ids.Contains(n.Id)).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        db.ChangeTracker.Clear();
    }

    /// <summary>The caller's unpublished pages and drafts of published pages they contributed to.</summary>
    public async Task<IReadOnlyList<ContentListItemDto>> MyDraftsAsync(CancellationToken ct)
    {
        var me = actor.UserId;
        var me1 = me.ToString("D");
        var candidates = await (from d in db.Drafts.AsNoTracking()
                                join n in db.Nodes.AsNoTracking() on d.ContentId equals n.Id
                                where (n.Status == ContentStatus.Draft && n.CreatedBy == me) || (n.Status == ContentStatus.Current && d.Contributors.Contains(me1))
                                orderby d.UpdatedAt descending
                                select new { n.Id, n.ContentType, d.Title, n.Slug, n.SpaceId, d.UpdatedAt }).Take(100).ToListAsync(ct).ConfigureAwait(false);
        var viewable = await authz.FilterViewableAsync(candidates.Select(c => (c.Id, c.SpaceId)).ToList(), ct).ConfigureAwait(false);
        var spaceIds = candidates.Select(c => c.SpaceId).Distinct().ToList();
        var spaces = await db.Spaces.AsNoTracking().Where(s => spaceIds.Contains(s.Id)).ToDictionaryAsync(s => s.Id, ct).ConfigureAwait(false);
        return candidates.Where(c => viewable.Contains(c.Id))
            .Select(c => new ContentListItemDto(c.Id, c.ContentType, c.Title, c.Slug, c.SpaceId, spaces[c.SpaceId].Key, spaces[c.SpaceId].Name, c.UpdatedAt)).ToList();
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads a viewable page (bodies are versioned; folders have no drafts).</summary>
    private async Task<Result<LoadedContent>> LoadVersionedAsync(Guid id, bool track, CancellationToken ct)
    {
        var loaded = await pages.LoadAsync(id, track, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        return loaded.Value.Provider.Capabilities.HasFlag(ContentTypeCapabilities.Versioned)
            ? loaded
            : Result<LoadedContent>.Failure(PublishingErrors.NotADraftPage);
    }

    private async Task<DraftDto> DraftDtoAsync(ContentNode node, CancellationToken ct)
    {
        var draft = await db.Drafts.AsNoTracking().FirstOrDefaultAsync(d => d.ContentId == node.Id, ct).ConfigureAwait(false);
        var pending = await db.PublishRequests.AsNoTracking().Where(r => r.ContentId == node.Id && r.Status == PublishRequestStatus.Pending).OrderByDescending(r => r.RequestedAt).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var pendingDto = pending is null ? null : (await RequestDtosAsync([pending], ct).ConfigureAwait(false))[0];
        if (draft is null)
        {
            // No draft yet: the editor starts from the published version (revision 0 = create on first save).
            var json = node.CurrentVersionId is { } vid
                ? await db.PageVersions.AsNoTracking().Where(v => v.Id == vid).Select(v => v.ContentJson).FirstAsync(ct).ConfigureAwait(false)
                : RaaviDocument.Empty.Json;
            using var published = JsonDocument.Parse(RaaviDocument.Parse(json).Json);
            return new DraftDto(node.Id, false, node.Title, published.RootElement.Clone(), node.CurrentVersionNumber, node.CurrentVersionNumber, false, 0,
                null, null, null, [], null, null, pendingDto);
        }

        var ids = draft.ContributorIds.Append(draft.UpdatedBy).Concat(draft.ScheduledBy is { } s ? [s] : []).Distinct().ToList();
        var names = await directory.GetUserNamesAsync(ids, ct).ConfigureAwait(false);
        using var body = JsonDocument.Parse(RaaviDocument.Parse(draft.ContentJson).Json);
        return new DraftDto(
            node.Id, true, draft.Title, body.RootElement.Clone(), draft.BaseVersionNumber, node.CurrentVersionNumber, draft.BaseVersionNumber < node.CurrentVersionNumber,
            draft.Revision, draft.UpdatedBy, names.GetValueOrDefault(draft.UpdatedBy), draft.UpdatedAt,
            draft.ContributorIds.Select(c => names.GetValueOrDefault(c)).OfType<string>().ToList(),
            draft.ScheduledPublishAt, draft.ScheduledBy is { } by ? names.GetValueOrDefault(by) : null, pendingDto);
    }

    private async Task<IReadOnlyList<PublishRequestDto>> RequestDtosAsync(IReadOnlyList<PublishRequest> rows, CancellationToken ct)
    {
        var names = await directory.GetUserNamesAsync(rows.SelectMany(r => r.DecidedBy is { } d ? new[] { r.RequestedBy, d } : [r.RequestedBy]).Distinct().ToList(), ct).ConfigureAwait(false);
        return rows.Select(r => new PublishRequestDto(r.Id, r.ContentId, r.Title, r.ChangeSummary, r.Status, r.RequestedBy, names.GetValueOrDefault(r.RequestedBy), r.RequestedAt,
            r.DecidedBy is { } d ? names.GetValueOrDefault(d) : null, r.DecidedAt, r.DecisionComment)).ToList();
    }
}

public sealed record ScheduledPublishArgs(Guid ContentId, DateTimeOffset PublishAt, Guid UserId);

/// <summary>Tenant job that publishes a scheduled draft (runs in the tenant's scope via TenantJobRunner).</summary>
public sealed class ScheduledPublishJob(PublishingService publishing) : ITenantJob<ScheduledPublishArgs>
{
    public Task ExecuteAsync(ScheduledPublishArgs args, CancellationToken cancellationToken) => publishing.RunScheduledAsync(args, cancellationToken);
}

/// <summary>Closure-table maintenance shared by move and restore.</summary>
public static class ClosureOps
{
    /// <summary>Re-links the subtree of <paramref name="nodeId"/> below <paramref name="newParent"/> (null = top level).</summary>
    public static async Task RelinkAsync(WikiDbContext db, Guid nodeId, Guid? newParent, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(db);
        var tenantId = db.CurrentTenantId;
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            DELETE c FROM [content].[ContentClosure] c
            WHERE c.TenantId = {tenantId}
              AND c.DescendantId IN (SELECT s.DescendantId FROM [content].[ContentClosure] s WHERE s.TenantId = {tenantId} AND s.AncestorId = {nodeId})
              AND c.AncestorId NOT IN (SELECT s.DescendantId FROM [content].[ContentClosure] s WHERE s.TenantId = {tenantId} AND s.AncestorId = {nodeId});
            """, ct).ConfigureAwait(false);
        if (newParent is { } parent)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO [content].[ContentClosure] (TenantId, AncestorId, DescendantId, Depth)
                SELECT {tenantId}, p.AncestorId, s.DescendantId, p.Depth + s.Depth + 1
                FROM [content].[ContentClosure] p
                JOIN [content].[ContentClosure] s ON s.TenantId = {tenantId} AND s.AncestorId = {nodeId}
                WHERE p.TenantId = {tenantId} AND p.DescendantId = {parent};
                """, ct).ConfigureAwait(false);
        }
    }
}
