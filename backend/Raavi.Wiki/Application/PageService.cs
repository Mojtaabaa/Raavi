using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Content;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.ContentTypes;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

/// <summary>Loaded content with the caller's effective rights.</summary>
public sealed record LoadedContent(ContentNode Node, SpaceRights Space, IContentProvider Provider, ContentRights Rights);

/// <summary>Content tree and lifecycle: navigation, CRUD, move/reorder, archive, trash, restrictions, labels.</summary>
/// <summary>Content tree and lifecycle. Drafts, publishing and history live in <see cref="PublishingService"/>.</summary>
public sealed class PageService(
    WikiDbContext db,
    WikiAuthorizer authz,
    SpaceService spaces,
    ContentProviderRegistry providers,
    IPrincipalDirectory directory,
    ICurrentUser current,
    IClock clock)
{
    public SpaceService Spaces => spaces;

    // ── Navigation ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Visible current children of <paramref name="parentId"/> (or the space's top level), in order.</summary>
    public async Task<Result<IReadOnlyList<TreeNodeDto>>> TreeAsync(string spaceKey, Guid? parentId, CancellationToken ct)
    {
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<IReadOnlyList<TreeNodeDto>>.Failure(WikiErrors.SpaceNotFound);
        }

        if (parentId is { } pid)
        {
            var parent = await LoadAsync(pid, track: false, ct).ConfigureAwait(false);
            if (!parent.IsSuccess || parent.Value.Node.SpaceId != space.Space.Id)
            {
                return Result<IReadOnlyList<TreeNodeDto>>.Failure(WikiErrors.ContentNotFound);
            }
        }

        var principal = await authz.PrincipalAsync(ct).ConfigureAwait(false);
        var children = await db.Nodes.AsNoTracking()
            .Where(n => n.SpaceId == space.Space.Id && n.ParentId == parentId && n.Status == ContentStatus.Current)
            .OrderBy(n => n.Position).ThenBy(n => n.Title)
            .Select(n => new { n.Id, n.ParentId, n.ContentType, n.Title, n.Slug, n.Position })
            .ToListAsync(ct).ConfigureAwait(false);

        // The parent is visible, so a child is visible iff its own View restrictions admit the caller.
        var visible = await authz.FilterByViewRestrictionsAsync(principal, children.Select(c => c.Id).ToList(), ct).ConfigureAwait(false);
        var visibleIds = children.Where(c => visible.Contains(c.Id)).Select(c => c.Id).ToList();
        var grandChildren = await db.Nodes.AsNoTracking()
            .Where(n => n.ParentId != null && visibleIds.Contains(n.ParentId.Value) && n.Status == ContentStatus.Current)
            .Select(n => new { n.Id, ParentId = n.ParentId!.Value })
            .ToListAsync(ct).ConfigureAwait(false);
        var visibleGrandChildren = await authz.FilterByViewRestrictionsAsync(principal, grandChildren.Select(g => g.Id).ToList(), ct).ConfigureAwait(false);
        var parentsWithChildren = grandChildren.Where(g => visibleGrandChildren.Contains(g.Id)).Select(g => g.ParentId).ToHashSet();

        return Result<IReadOnlyList<TreeNodeDto>>.Success(children
            .Where(c => visible.Contains(c.Id))
            .Select(c => new TreeNodeDto(c.Id, c.ParentId, c.ContentType, c.Title, c.Slug, c.Position, parentsWithChildren.Contains(c.Id)))
            .ToList());
    }

    // ── Read ───────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<PageDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        return loaded.IsSuccess ? Result<PageDto>.Success(await ToDtoAsync(loaded.Value, ct).ConfigureAwait(false)) : Result<PageDto>.Failure(loaded.Error!);
    }

    public async Task<Result<IReadOnlyList<VersionSummaryDto>>> VersionsAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<VersionSummaryDto>>.Failure(loaded.Error!);
        }

        var versions = await db.PageVersions.AsNoTracking().Where(v => v.ContentId == id).OrderByDescending(v => v.VersionNumber)
            .Select(v => new { v.Id, v.VersionNumber, v.Title, v.ChangeSummary, v.CreatedBy, v.CreatedAt, v.VersionType, v.RestoredFromVersionNumber }).Take(200).ToListAsync(ct).ConfigureAwait(false);
        var versionIds = versions.Select(v => v.Id).ToList();
        var contributors = await db.VersionContributors.AsNoTracking().Where(c => versionIds.Contains(c.VersionId)).ToListAsync(ct).ConfigureAwait(false);
        var names = await directory.GetUserNamesAsync(versions.Where(v => v.CreatedBy != null).Select(v => v.CreatedBy!.Value).Concat(contributors.Select(c => c.UserId)).Distinct().ToList(), ct).ConfigureAwait(false);
        return Result<IReadOnlyList<VersionSummaryDto>>.Success(versions
            .Select(v => new VersionSummaryDto(v.VersionNumber, v.Title, v.ChangeSummary, v.CreatedBy, v.CreatedBy is { } u ? names.GetValueOrDefault(u) : null, v.CreatedAt,
                v.VersionType, v.RestoredFromVersionNumber, contributors.Where(c => c.VersionId == v.Id).Select(c => names.GetValueOrDefault(c.UserId)).OfType<string>().ToList()))
            .ToList());
    }

    // ── Create ─────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<PageDto>> CreateAsync(string spaceKey, CreatePageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<PageDto>.Failure(WikiErrors.SpaceNotFound);
        }

        var type = request.ContentType ?? PageContentProvider.Type;
        if (!providers.IsKnown(type))
        {
            return Result<PageDto>.Failure(new Error("content.type_unknown", "Unknown content type."));
        }

        var provider = providers.Get(type);
        if (request.ParentId is { } parentId)
        {
            var parent = await LoadAsync(parentId, track: false, ct).ConfigureAwait(false);
            if (!parent.IsSuccess || parent.Value.Node.SpaceId != space.Space.Id)
            {
                return Result<PageDto>.Failure(WikiErrors.ContentNotFound);
            }

            if (parent.Value.Node.Status != ContentStatus.Current)
            {
                return Result<PageDto>.Failure(WikiErrors.ParentNotCurrent);
            }

            if (!parent.Value.Provider.Capabilities.HasFlag(ContentTypeCapabilities.CanHaveChildren))
            {
                return Result<PageDto>.Failure(WikiErrors.ParentNotContainer);
            }

            if (!parent.Value.Rights.CanCreateChild)
            {
                return Result<PageDto>.Failure(WikiErrors.Forbidden);
            }
        }
        else if (!space.CanWrite(PermissionAction.Create))
        {
            return Result<PageDto>.Failure(space.Space.Status == SpaceStatus.Active ? WikiErrors.Forbidden : WikiErrors.SpaceArchived);
        }

        RaaviDocument? body = null;
        if (provider.Capabilities.HasFlag(ContentTypeCapabilities.Versioned))
        {
            if (request.TemplateId is { } templateId)
            {
                var template = await db.Templates.AsNoTracking().FirstOrDefaultAsync(t => t.Id == templateId && (t.SpaceId == null || t.SpaceId == space.Space.Id), ct).ConfigureAwait(false);
                if (template is null)
                {
                    return Result<PageDto>.Failure(WikiErrors.TemplateNotFound);
                }

                body = RaaviDocument.Parse(template.ContentJson);
            }
            else
            {
                body = request.Body is { } b ? RaaviDocument.Parse(b) : RaaviDocument.Empty;
            }
        }

        // Versioned content is either published at once (API default) or starts as a private draft (editor flow).
        var asDraft = body is not null && request.Publish == false;
        if (body is not null && !asDraft)
        {
            if (!space.CanWrite(PermissionAction.Publish))
            {
                return Result<PageDto>.Failure(WikiErrors.Forbidden);
            }

            if (space.Space.RequirePublishApproval && !space.CanAdminister)
            {
                return Result<PageDto>.Failure(PublishingErrors.ApprovalRequired);
            }
        }

        var now = clock.UtcNow;
        var userId = current.UserId;
        var position = await NextPositionAsync(space.Space.Id, request.ParentId, ct).ConfigureAwait(false);
        var node = asDraft
            ? ContentNode.CreateDraft(space.Space.Id, request.ParentId, type, request.Title, position, userId, now)
            : ContentNode.Create(space.Space.Id, request.ParentId, type, request.Title, position, userId, now);
        db.Nodes.Add(node);
        db.Closure.Add(ContentClosure.Link(node.Id, node.Id, 0));
        if (request.ParentId is { } p)
        {
            var ancestors = await db.Closure.AsNoTracking().Where(c => c.DescendantId == p).ToListAsync(ct).ConfigureAwait(false);
            foreach (var a in ancestors)
            {
                db.Closure.Add(ContentClosure.Link(a.AncestorId, node.Id, a.Depth + 1));
            }
        }

        if (asDraft)
        {
            db.Drafts.Add(PageDraft.Start(node.Id, node.Title, body!, 0, userId, now));
            await db.SaveChangesAsync(ct).ConfigureAwait(false); // no event: nobody else can see a draft
            return await GetAsync(node.Id, ct).ConfigureAwait(false);
        }

        if (body is not null)
        {
            var version = PageVersion.Create(node.Id, 1, node.Title, body, null, userId, now);
            db.PageVersions.Add(version);
            db.VersionContributors.Add(PageVersionContributor.Create(version.Id, userId));
            node.RecordVersion(version, userId, now);
        }

        db.Publish(WikiEventTypes.ContentCreated, new WikiEvent(space.Space.Id, node.Id, userId, node.Title, body is null ? null : 1), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await GetAsync(node.Id, ct).ConfigureAwait(false);
    }

    // ── Update ─────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<PageDto>> UpdateAsync(Guid id, int expectedRevision, UpdatePageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadForChangeAsync(id, expectedRevision, r => r.CanEdit, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<PageDto>.Failure(loaded.Error!);
        }

        var (node, space, provider, rights) = loaded.Value;
        var versioned = provider.Capabilities.HasFlag(ContentTypeCapabilities.Versioned);
        if (versioned && !rights.CanPublish)
        {
            return Result<PageDto>.Failure(WikiErrors.Forbidden);
        }

        if (versioned && space.Space.RequirePublishApproval && !rights.CanApprove)
        {
            return Result<PageDto>.Failure(PublishingErrors.ApprovalRequired);
        }

        var now = clock.UtcNow;
        var userId = current.UserId;
        var title = ContentNode.NormalizeTitle(request.Title);
        var titleChanged = title != node.Title;
        if (provider.Capabilities.HasFlag(ContentTypeCapabilities.Versioned))
        {
            var body = request.Body is { } b ? RaaviDocument.Parse(b) : RaaviDocument.Empty;
            var currentHash = await db.PageVersions.AsNoTracking().Where(v => v.Id == node.CurrentVersionId).Select(v => v.ContentHash).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            if (!titleChanged && currentHash == body.Hash)
            {
                return await GetAsync(id, ct).ConfigureAwait(false); // no-op save creates no version
            }

            node.SetTitle(title);
            var version = PageVersion.Create(node.Id, node.CurrentVersionNumber + 1, node.Title, body, request.ChangeSummary, userId, now);
            db.PageVersions.Add(version);
            db.VersionContributors.Add(PageVersionContributor.Create(version.Id, userId));
            node.RecordVersion(version, userId, now);
        }
        else
        {
            if (!titleChanged)
            {
                return await GetAsync(id, ct).ConfigureAwait(false);
            }

            node.SetTitle(title);
            node.Touch(userId, now);
        }

        db.Publish(versioned ? WikiEventTypes.ContentPublished : WikiEventTypes.ContentUpdated, new WikiEvent(space.Space.Id, node.Id, userId, node.Title, node.CurrentVersionNumber == 0 ? null : node.CurrentVersionNumber), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    // ── Move / reorder ─────────────────────────────────────────────────────────────────────────

    public async Task<Result<PageDto>> MoveAsync(Guid id, int expectedRevision, MovePageRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadForChangeAsync(id, expectedRevision, r => r.CanMove, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<PageDto>.Failure(loaded.Error!);
        }

        var (node, sourceSpace, _, _) = loaded.Value;
        var target = sourceSpace;
        if (!string.IsNullOrWhiteSpace(request.SpaceKey) && Space.NormalizeKey(request.SpaceKey) != sourceSpace.Space.Key)
        {
            target = await spaces.LoadAsync(request.SpaceKey, requireView: true, ct).ConfigureAwait(false);
            if (target is null)
            {
                return Result<PageDto>.Failure(WikiErrors.SpaceNotFound);
            }

            if (await ContainsHomePageAsync(node, sourceSpace, ct).ConfigureAwait(false))
            {
                return Result<PageDto>.Failure(WikiErrors.IsHomePage);
            }
        }

        var parentChanged = request.ParentId != node.ParentId || target.Space.Id != node.SpaceId;
        if (request.ParentId is { } parentId)
        {
            var parent = await LoadAsync(parentId, track: false, ct).ConfigureAwait(false);
            if (!parent.IsSuccess || parent.Value.Node.SpaceId != target.Space.Id)
            {
                return Result<PageDto>.Failure(WikiErrors.ContentNotFound);
            }

            if (await db.Closure.AnyAsync(c => c.AncestorId == node.Id && c.DescendantId == parentId, ct).ConfigureAwait(false))
            {
                return Result<PageDto>.Failure(WikiErrors.MoveCycle);
            }

            if (!parent.Value.Provider.Capabilities.HasFlag(ContentTypeCapabilities.CanHaveChildren))
            {
                return Result<PageDto>.Failure(WikiErrors.ParentNotContainer);
            }

            if (parent.Value.Node.Status != ContentStatus.Current)
            {
                return Result<PageDto>.Failure(WikiErrors.ParentNotCurrent);
            }

            if (parentChanged && !parent.Value.Rights.CanCreateChild)
            {
                return Result<PageDto>.Failure(WikiErrors.Forbidden);
            }
        }
        else if (parentChanged && !target.CanWrite(PermissionAction.Create))
        {
            return Result<PageDto>.Failure(WikiErrors.Forbidden);
        }

        var now = clock.UtcNow;
        var userId = current.UserId;
        var oldParent = node.ParentId;
        var oldSpace = node.SpaceId;

        var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (tx.ConfigureAwait(false))
        {
            if (parentChanged)
            {
                node.MoveTo(target.Space.Id, request.ParentId, userId, now);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);

                // Re-link the subtree under its new ancestors (set-based; RLS confines it to this tenant).
                await ClosureOps.RelinkAsync(db, node.Id, request.ParentId, ct).ConfigureAwait(false);

                if (target.Space.Id != oldSpace)
                {
                    var subtree = db.Closure.Where(c => c.AncestorId == node.Id && c.DescendantId != node.Id).Select(c => c.DescendantId);
                    await db.Nodes.Where(n => subtree.Contains(n.Id)).ExecuteUpdateAsync(s => s.SetProperty(n => n.SpaceId, target.Space.Id), ct).ConfigureAwait(false);
                }
            }

            await PlaceAsync(node, request.Position, ct).ConfigureAwait(false);
            if (parentChanged)
            {
                await RenumberAsync(oldSpace, oldParent, null, ct).ConfigureAwait(false);
            }
            else
            {
                node.Touch(userId, now);
            }

            db.Publish(WikiEventTypes.ContentMoved, new WikiEvent(target.Space.Id, node.Id, userId, node.Title, null), now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        db.ChangeTracker.Clear();
        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    // ── Lifecycle ──────────────────────────────────────────────────────────────────────────────

    public async Task<Result<PageDto>> SetArchivedAsync(Guid id, int expectedRevision, bool archive, CancellationToken ct)
    {
        var loaded = await LoadForChangeAsync(id, expectedRevision, r => r.CanArchive, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<PageDto>.Failure(loaded.Error!);
        }

        var (node, space, _, _) = loaded.Value;
        if (archive && await ContainsHomePageAsync(node, space, ct).ConfigureAwait(false))
        {
            return Result<PageDto>.Failure(WikiErrors.IsHomePage);
        }

        var from = archive ? ContentStatus.Current : ContentStatus.Archived;
        var to = archive ? ContentStatus.Archived : ContentStatus.Current;
        if (node.Status != from)
        {
            return Result<PageDto>.Failure(new Error(archive ? "content.not_current" : "content.not_archived", "The content is not in a state that allows this.", ErrorKind.Conflict));
        }

        if (!archive && node.ParentId is { } parentId
            && await db.Nodes.AnyAsync(n => n.Id == parentId && n.Status != ContentStatus.Current, ct).ConfigureAwait(false))
        {
            return Result<PageDto>.Failure(WikiErrors.ParentNotCurrent);
        }

        await ChangeSubtreeStatusAsync(node, space, from, to, archive ? WikiEventTypes.ContentArchived : WikiEventTypes.ContentRestored, ct).ConfigureAwait(false);
        return await GetAsync(id, ct).ConfigureAwait(false);
    }

    /// <summary>Moves the subtree to the trash (soft delete; restore and purge arrive with Phase 5).</summary>
    public async Task<Result> TrashAsync(Guid id, int expectedRevision, CancellationToken ct)
    {
        var loaded = await LoadForChangeAsync(id, expectedRevision, r => r.CanDelete, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        var (node, space, _, _) = loaded.Value;
        if (await ContainsHomePageAsync(node, space, ct).ConfigureAwait(false))
        {
            return Result.Failure(WikiErrors.IsHomePage);
        }

        await ChangeSubtreeStatusAsync(node, space, null, ContentStatus.Trashed, WikiEventTypes.ContentTrashed, ct).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task ChangeSubtreeStatusAsync(ContentNode node, SpaceRights space, ContentStatus? from, ContentStatus to, string eventType, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var userId = current.UserId;
        var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await using (tx.ConfigureAwait(false))
        {
            node.SetStatus(to, userId, now);
            db.Publish(eventType, new WikiEvent(space.Space.Id, node.Id, userId, node.Title, null), now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            var subtree = db.Closure.Where(c => c.AncestorId == node.Id && c.DescendantId != node.Id).Select(c => c.DescendantId);
            var descendants = db.Nodes.Where(n => subtree.Contains(n.Id) && n.Status != ContentStatus.Trashed);
            if (from is { } f)
            {
                descendants = descendants.Where(n => n.Status == f);
            }

            DateTimeOffset? deletedAt = to == ContentStatus.Trashed ? now : null;
            Guid? deletedBy = to == ContentStatus.Trashed ? userId : null;
            await descendants.ExecuteUpdateAsync(s => s
                .SetProperty(n => n.Status, to)
                .SetProperty(n => n.DeletedAt, deletedAt)
                .SetProperty(n => n.DeletedBy, deletedBy)
                .SetProperty(n => n.UpdatedAt, now)
                .SetProperty(n => n.UpdatedBy, userId)
                .SetProperty(n => n.Revision, n => n.Revision + 1), ct).ConfigureAwait(false);
            await tx.CommitAsync(ct).ConfigureAwait(false);
        }

        db.ChangeTracker.Clear();
    }

    // ── Restrictions ───────────────────────────────────────────────────────────────────────────

    public async Task<Result<RestrictionsDto>> GetRestrictionsAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct, allowAdministratorRecovery: true).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<RestrictionsDto>.Failure(loaded.Error!);
        }

        var rows = await db.Restrictions.AsNoTracking().Where(r => r.ContentId == id).ToListAsync(ct).ConfigureAwait(false);
        var users = await directory.GetUserNamesAsync(rows.Where(r => r.SubjectType == PermissionSubjectType.User).Select(r => r.SubjectId).ToList(), ct).ConfigureAwait(false);
        var groups = await directory.GetGroupNamesAsync(rows.Where(r => r.SubjectType == PermissionSubjectType.Group).Select(r => r.SubjectId).ToList(), ct).ConfigureAwait(false);
        RestrictionSubjectDto Describe(ContentRestriction r) =>
            new(r.SubjectType, r.SubjectId, r.SubjectType == PermissionSubjectType.User ? users.GetValueOrDefault(r.SubjectId) : groups.GetValueOrDefault(r.SubjectId));

        var inherited = await (from c in db.Closure.AsNoTracking()
                               join n in db.Nodes.AsNoTracking() on c.AncestorId equals n.Id
                               where c.DescendantId == id && c.Depth > 0 && db.Restrictions.Any(r => r.ContentId == c.AncestorId && r.Kind == RestrictionKind.View)
                               orderby c.Depth descending
                               select new InheritedRestrictionDto(n.Id, n.Title)).ToListAsync(ct).ConfigureAwait(false);
        return Result<RestrictionsDto>.Success(new RestrictionsDto(
            rows.Where(r => r.Kind == RestrictionKind.View).Select(Describe).ToList(),
            rows.Where(r => r.Kind == RestrictionKind.Edit).Select(Describe).ToList(),
            inherited));
    }

    public async Task<Result<RestrictionsDto>> ReplaceRestrictionsAsync(Guid id, ReplaceRestrictionsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, track: false, ct, allowAdministratorRecovery: true).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<RestrictionsDto>.Failure(loaded.Error!);
        }

        var (node, space, _, rights) = loaded.Value;
        if (!rights.CanRestrict)
        {
            return Result<RestrictionsDto>.Failure(WikiErrors.Forbidden);
        }

        var view = (request.View ?? []).Distinct().ToList();
        var edit = (request.Edit ?? []).Distinct().ToList();
        var subjects = view.Concat(edit).Select(s => (s.SubjectType, (Guid?)s.SubjectId)).ToList();
        if (subjects.Any(s => s.SubjectType == PermissionSubjectType.AllMembers))
        {
            return Result<RestrictionsDto>.Failure(WikiErrors.SubjectNotFound);
        }

        var validation = await spaces.ValidateSubjectsAsync(subjects, ct).ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            return Result<RestrictionsDto>.Failure(validation.Error!);
        }

        // Editors cannot lock themselves out; space administrators may (they keep recovery through administration).
        var principal = await authz.PrincipalAsync(ct).ConfigureAwait(false);
        bool Includes(List<RestrictionSubjectRequest> list) => list.Count == 0 || list.Any(s => WikiAuthorizer.Matches(principal, s.SubjectType, s.SubjectId));
        if (!space.CanAdminister && (!Includes(view) || !Includes(edit)))
        {
            return Result<RestrictionsDto>.Failure(WikiErrors.ExcludesSelf);
        }

        db.Restrictions.RemoveRange(await db.Restrictions.Where(r => r.ContentId == id).ToListAsync(ct).ConfigureAwait(false));
        foreach (var s in view)
        {
            db.Restrictions.Add(ContentRestriction.Create(id, RestrictionKind.View, s.SubjectType, s.SubjectId));
        }

        foreach (var s in edit)
        {
            db.Restrictions.Add(ContentRestriction.Create(id, RestrictionKind.Edit, s.SubjectType, s.SubjectId));
        }

        db.Publish(WikiEventTypes.ContentRestrictionsChanged, new WikiEvent(space.Space.Id, id, current.UserId, node.Title, null), clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await GetRestrictionsAsync(id, ct).ConfigureAwait(false);
    }

    // ── Labels ─────────────────────────────────────────────────────────────────────────────────

    public async Task<Result<IReadOnlyList<string>>> AddLabelAsync(Guid id, string name, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<string>>.Failure(loaded.Error!);
        }

        if (!loaded.Value.Rights.CanEdit)
        {
            return Result<IReadOnlyList<string>>.Failure(WikiErrors.Forbidden);
        }

        var normalized = Label.Normalize(name);
        var label = await db.Labels.FirstOrDefaultAsync(l => l.Name == normalized, ct).ConfigureAwait(false);
        if (label is null)
        {
            label = Label.Create(normalized, clock.UtcNow);
            db.Labels.Add(label);
        }

        if (!await db.ContentLabels.AnyAsync(cl => cl.ContentId == id && cl.LabelId == label.Id, ct).ConfigureAwait(false))
        {
            if (await db.ContentLabels.CountAsync(cl => cl.ContentId == id, ct).ConfigureAwait(false) >= 100)
            {
                return Result<IReadOnlyList<string>>.Failure(new Error("label.too_many", "A page can have at most 100 labels."));
            }

            db.ContentLabels.Add(ContentLabel.Create(id, label.Id, current.UserId, clock.UtcNow));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return Result<IReadOnlyList<string>>.Success(await LabelsOfAsync(id, ct).ConfigureAwait(false));
    }

    public async Task<Result<IReadOnlyList<string>>> RemoveLabelAsync(Guid id, string name, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<string>>.Failure(loaded.Error!);
        }

        if (!loaded.Value.Rights.CanEdit)
        {
            return Result<IReadOnlyList<string>>.Failure(WikiErrors.Forbidden);
        }

        var normalized = Label.Normalize(name);
        var link = await (from cl in db.ContentLabels
                          join l in db.Labels on cl.LabelId equals l.Id
                          where cl.ContentId == id && l.Name == normalized
                          select cl).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (link is not null)
        {
            db.ContentLabels.Remove(link);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return Result<IReadOnlyList<string>>.Success(await LabelsOfAsync(id, ct).ConfigureAwait(false));
    }

    private async Task<IReadOnlyList<string>> LabelsOfAsync(Guid id, CancellationToken ct) =>
        await (from cl in db.ContentLabels.AsNoTracking()
               join l in db.Labels.AsNoTracking() on cl.LabelId equals l.Id
               where cl.ContentId == id
               orderby l.Name
               select l.Name).ToListAsync(ct).ConfigureAwait(false);

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads content the caller may view; anything else (missing, other tenant, trashed, restricted, invisible space) is
    /// "not found". With <paramref name="allowAdministratorRecovery"/>, space administrators also load content they cannot
    /// view (restriction management only).
    /// </summary>
    public async Task<Result<LoadedContent>> LoadAsync(Guid id, bool track, CancellationToken ct, bool allowAdministratorRecovery = false)
    {
        var query = track ? db.Nodes : db.Nodes.AsNoTracking();
        var node = await query.FirstOrDefaultAsync(n => n.Id == id, ct).ConfigureAwait(false);
        if (node is null || node.Status == ContentStatus.Trashed)
        {
            return Result<LoadedContent>.Failure(WikiErrors.ContentNotFound);
        }

        var space = await db.Spaces.AsNoTracking().FirstAsync(s => s.Id == node.SpaceId, ct).ConfigureAwait(false);
        var spaceRights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        var provider = providers.Get(node.ContentType);
        var rights = await authz.GetContentRightsAsync(node, spaceRights, provider, ct).ConfigureAwait(false);
        if (!rights.CanView)
        {
            if (allowAdministratorRecovery && spaceRights.CanAdminister)
            {
                return Result<LoadedContent>.Success(new LoadedContent(node, spaceRights, provider, rights with { CanRestrict = true }));
            }

            return Result<LoadedContent>.Failure(WikiErrors.ContentNotFound);
        }

        return Result<LoadedContent>.Success(new LoadedContent(node, spaceRights, provider, rights));
    }

    private async Task<Result<LoadedContent>> LoadForChangeAsync(Guid id, int expectedRevision, Func<ContentRights, bool> may, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        var value = loaded.Value;
        if (value.Space.Space.Status != SpaceStatus.Active)
        {
            return Result<LoadedContent>.Failure(WikiErrors.SpaceArchived);
        }

        if (!may(value.Rights))
        {
            return Result<LoadedContent>.Failure(WikiErrors.Forbidden);
        }

        if (value.Node.Revision != expectedRevision)
        {
            return Result<LoadedContent>.Failure(ETags.PreconditionFailed);
        }

        return loaded;
    }

    /// <summary>True when the space home page is <paramref name="node"/> or one of its descendants.</summary>
    private Task<bool> ContainsHomePageAsync(ContentNode node, SpaceRights space, CancellationToken ct) =>
        space.Space.HomePageId is { } home
            ? db.Closure.AnyAsync(c => c.AncestorId == node.Id && c.DescendantId == home, ct)
            : Task.FromResult(false);

    private async Task<int> NextPositionAsync(Guid spaceId, Guid? parentId, CancellationToken ct)
    {
        var max = await db.Nodes.Where(n => n.SpaceId == spaceId && n.ParentId == parentId).MaxAsync(n => (int?)n.Position, ct).ConfigureAwait(false);
        return (max ?? -1) + 1;
    }

    /// <summary>Puts <paramref name="node"/> at <paramref name="position"/> among its (non-trashed) siblings and renumbers densely.</summary>
    private async Task PlaceAsync(ContentNode node, int? position, CancellationToken ct)
    {
        var siblings = await db.Nodes
            .Where(n => n.SpaceId == node.SpaceId && n.ParentId == node.ParentId && n.Id != node.Id && n.Status != ContentStatus.Trashed)
            .OrderBy(n => n.Position).ThenBy(n => n.Title)
            .ToListAsync(ct).ConfigureAwait(false);
        var index = Math.Clamp(position ?? siblings.Count, 0, siblings.Count);
        siblings.Insert(index, node);
        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].Position != i)
            {
                siblings[i].SetPosition(i);
            }
        }
    }

    private async Task RenumberAsync(Guid spaceId, Guid? parentId, Guid? exclude, CancellationToken ct)
    {
        var siblings = await db.Nodes
            .Where(n => n.SpaceId == spaceId && n.ParentId == parentId && n.Id != exclude && n.Status != ContentStatus.Trashed)
            .OrderBy(n => n.Position).ThenBy(n => n.Title)
            .ToListAsync(ct).ConfigureAwait(false);
        for (var i = 0; i < siblings.Count; i++)
        {
            if (siblings[i].Position != i)
            {
                siblings[i].SetPosition(i);
            }
        }
    }

    private async Task<PageDto> ToDtoAsync(LoadedContent c, CancellationToken ct)
    {
        var node = c.Node;
        var space = c.Space.Space;
        JsonElement? body = null;
        if (node.CurrentVersionId is { } versionId)
        {
            var stored = await db.PageVersions.AsNoTracking().Where(v => v.Id == versionId).Select(v => v.ContentJson).FirstAsync(ct).ConfigureAwait(false);
            // Lazy migration (EDITOR_ARCHITECTURE.md §4): older schema versions are upgraded in memory; versions stay immutable.
            using var doc = JsonDocument.Parse(RaaviDocument.Parse(stored).Json);
            body = doc.RootElement.Clone();
        }

        var crumbs = await (from cl in db.Closure.AsNoTracking()
                            join n in db.Nodes.AsNoTracking() on cl.AncestorId equals n.Id
                            where cl.DescendantId == node.Id && cl.Depth > 0
                            orderby cl.Depth descending
                            select new BreadcrumbDto(n.Id, n.Title, n.ContentType)).ToListAsync(ct).ConfigureAwait(false);
        var restricted = await (from cl in db.Closure.AsNoTracking()
                                join r in db.Restrictions.AsNoTracking() on cl.AncestorId equals r.ContentId
                                where cl.DescendantId == node.Id
                                select r.Id).AnyAsync(ct).ConfigureAwait(false);
        var names = await directory.GetUserNamesAsync([node.CreatedBy, node.UpdatedBy], ct).ConfigureAwait(false);
        var draft = await db.Drafts.AsNoTracking().Where(d => d.ContentId == node.Id).Select(d => new { d.UpdatedAt, d.ScheduledPublishAt }).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var pending = await db.PublishRequests.AsNoTracking().AnyAsync(r => r.ContentId == node.Id && r.Status == PublishRequestStatus.Pending, ct).ConfigureAwait(false);
        var rights = c.Rights;
        return new PageDto(
            node.Id, space.Id, space.Key, space.Name, node.ParentId, node.ContentType, node.Title, node.Slug, node.Status, body,
            node.CurrentVersionNumber, await LabelsOfAsync(node.Id, ct).ConfigureAwait(false), crumbs, restricted,
            node.CreatedBy, names.GetValueOrDefault(node.CreatedBy), node.CreatedAt, node.UpdatedBy, names.GetValueOrDefault(node.UpdatedBy),
            node.UpdatedAt, node.Revision, new ContentRightsDto(rights.CanEdit, rights.CanCreateChild, rights.CanMove, rights.CanArchive, rights.CanDelete, rights.CanRestrict, rights.CanPublish, rights.CanApprove, rights.CanComment, rights.CanModerateComments),
            node.PublishedAt, draft is not null && rights.CanEdit, rights.CanEdit ? draft?.UpdatedAt : null, rights.CanEdit ? draft?.ScheduledPublishAt : null, pending, space.RequirePublishApproval);
    }
}
