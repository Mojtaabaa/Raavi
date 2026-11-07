using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.ContentTypes;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

/// <summary>Spaces: listing, creation (with default grants and a home page), settings, archive, space permissions.</summary>
public sealed class SpaceService(WikiDbContext db, WikiAuthorizer authz, IPrincipalDirectory directory, ICurrentUser current, IClock clock)
{
    private static readonly PermissionAction[] CreatorActions = WikiAuthorizer.SpaceActions;

    private static readonly PermissionAction[] OpenMemberActions =
        [PermissionAction.View, PermissionAction.Create, PermissionAction.Edit, PermissionAction.Publish, PermissionAction.Move, PermissionAction.Comment];

    public async Task<IReadOnlyList<SpaceSummaryDto>> ListAsync(bool includeArchived, bool administration, CancellationToken ct)
    {
        IQueryable<Space> query = db.Spaces.AsNoTracking();
        var principal = await authz.PrincipalAsync(ct).ConfigureAwait(false);
        if (!(administration && principal.IsTenantAdministrator))
        {
            var viewable = (await authz.ViewableSpaceIdsAsync(ct).ConfigureAwait(false)).ToList();
            query = query.Where(s => viewable.Contains(s.Id));
        }

        if (!includeArchived)
        {
            query = query.Where(s => s.Status == SpaceStatus.Active);
        }

        return await query.OrderBy(s => s.Name).Select(s => Summary(s)).ToListAsync(ct).ConfigureAwait(false);
    }

    public static SpaceSummaryDto Summary(Space s) => new(s.Id, s.Key, s.Name, s.Description, s.Type, s.Status, s.HomePageId, s.UpdatedAt);

    /// <summary>Space metadata for viewers and administrators (recovery). Everyone else: not found.</summary>
    public async Task<Result<SpaceDto>> GetAsync(string key, CancellationToken ct)
    {
        var loaded = await LoadAsync(key, requireView: false, ct).ConfigureAwait(false);
        if (loaded is null || !(loaded.CanView || loaded.CanAdminister))
        {
            return Result<SpaceDto>.Failure(WikiErrors.SpaceNotFound);
        }

        return Result<SpaceDto>.Success(await ToDtoAsync(loaded, ct).ConfigureAwait(false));
    }

    public async Task<Result<SpaceDto>> CreateAsync(CreateSpaceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await authz.CanCreateSpacesAsync(ct).ConfigureAwait(false))
        {
            return Result<SpaceDto>.Failure(WikiErrors.Forbidden);
        }

        var now = clock.UtcNow;
        var userId = current.UserId;
        var space = Space.Create(request.Key, request.Name, request.Description, request.Type, userId, now);
        if (await db.Spaces.AnyAsync(s => s.Key == space.Key, ct).ConfigureAwait(false))
        {
            return Result<SpaceDto>.Failure(WikiErrors.KeyTaken);
        }

        db.Spaces.Add(space);
        foreach (var action in CreatorActions)
        {
            db.Grants.Add(PermissionGrant.ForSpace(space.Id, PermissionSubjectType.User, userId, action, PermissionEffect.Allow));
        }

        if (request.Visibility == SpaceVisibility.Open)
        {
            foreach (var action in OpenMemberActions)
            {
                db.Grants.Add(PermissionGrant.ForSpace(space.Id, PermissionSubjectType.AllMembers, null, action, PermissionEffect.Allow));
            }
        }

        // Home page (Confluence semantics): the landing page of the space, titled like the space.
        var home = ContentNode.Create(space.Id, null, PageContentProvider.Type, space.Name, 0, userId, now);
        db.Nodes.Add(home);
        db.Closure.Add(ContentClosure.Link(home.Id, home.Id, 0));
        var version = PageVersion.Create(home.Id, 1, home.Title, RaaviDocument.Empty, null, userId, now);
        db.PageVersions.Add(version);
        home.RecordVersion(version, userId, now);
        space.SetHomePage(home.Id, userId, now);

        db.Publish(WikiEventTypes.SpaceCreated, new WikiEvent(space.Id, null, userId, space.Name, null), now);
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException)
        {
            // Lost a race for the key against a concurrent creation (unique index UX TenantId+Key).
            if (await KeyExistsAsync(space.Key).ConfigureAwait(false))
            {
                return Result<SpaceDto>.Failure(WikiErrors.KeyTaken);
            }

            throw;
        }

        return await GetAsync(space.Key, ct).ConfigureAwait(false);
    }

    private async Task<bool> KeyExistsAsync(string key)
    {
        db.ChangeTracker.Clear();
        return await db.Spaces.AnyAsync(s => s.Key == key).ConfigureAwait(false);
    }

    public async Task<Result<SpaceDto>> UpdateAsync(string key, int expectedRevision, UpdateSpaceRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAdministeredAsync(key, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<SpaceDto>.Failure(loaded.Error!);
        }

        var space = loaded.Value.Space;
        if (space.Revision != expectedRevision)
        {
            return Result<SpaceDto>.Failure(ETags.PreconditionFailed);
        }

        if (space.Status != SpaceStatus.Active)
        {
            return Result<SpaceDto>.Failure(WikiErrors.SpaceArchived);
        }

        if (request.HomePageId is { } homeId
            && !await db.Nodes.AnyAsync(n => n.Id == homeId && n.SpaceId == space.Id && n.ContentType == PageContentProvider.Type && n.Status == ContentStatus.Current, ct).ConfigureAwait(false))
        {
            return Result<SpaceDto>.Failure(WikiErrors.HomePageInvalid);
        }

        var now = clock.UtcNow;
        space.Update(request.Name, request.Description, request.Type, current.UserId, now);
        if (request.HomePageId != space.HomePageId)
        {
            space.SetHomePage(request.HomePageId, current.UserId, now);
        }

        db.Publish(WikiEventTypes.SpaceUpdated, new WikiEvent(space.Id, null, current.UserId, space.Name, null), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await GetAsync(space.Key, ct).ConfigureAwait(false);
    }

    public async Task<Result<SpaceDto>> SetArchivedAsync(string key, int expectedRevision, bool archived, CancellationToken ct)
    {
        var loaded = await LoadAdministeredAsync(key, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<SpaceDto>.Failure(loaded.Error!);
        }

        var space = loaded.Value.Space;
        if (space.Revision != expectedRevision)
        {
            return Result<SpaceDto>.Failure(ETags.PreconditionFailed);
        }

        var now = clock.UtcNow;
        if (archived)
        {
            space.Archive(current.UserId, now);
        }
        else
        {
            space.Restore(current.UserId, now);
        }

        db.Publish(archived ? WikiEventTypes.SpaceArchived : WikiEventTypes.SpaceRestored, new WikiEvent(space.Id, null, current.UserId, space.Name, null), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await GetAsync(space.Key, ct).ConfigureAwait(false);
    }

    // ── Permissions ────────────────────────────────────────────────────────────────────────────

    public async Task<Result<SpacePermissionsDto>> GetPermissionsAsync(string key, CancellationToken ct)
    {
        var loaded = await LoadAdministeredAsync(key, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<SpacePermissionsDto>.Failure(loaded.Error!);
        }

        var grants = await db.Grants.AsNoTracking().Where(g => g.Scope == PermissionScope.Space && g.SpaceId == loaded.Value.Space.Id).ToListAsync(ct).ConfigureAwait(false);
        return Result<SpacePermissionsDto>.Success(new SpacePermissionsDto(await DescribeAsync(grants, ct).ConfigureAwait(false), WikiAuthorizer.SpaceActions));
    }

    public async Task<Result<SpacePermissionsDto>> ReplacePermissionsAsync(string key, ReplaceGrantsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAdministeredAsync(key, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<SpacePermissionsDto>.Failure(loaded.Error!);
        }

        var space = loaded.Value.Space;
        var wanted = (request.Grants ?? []).Distinct().ToList();
        if (wanted.Any(g => !WikiAuthorizer.SpaceActions.Contains(g.Action)))
        {
            return Result<SpacePermissionsDto>.Failure(WikiErrors.ActionNotAllowed);
        }

        var validation = await ValidateSubjectsAsync(wanted.Select(g => (g.SubjectType, g.SubjectId)), ct).ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            return Result<SpacePermissionsDto>.Failure(validation.Error!);
        }

        // A space must keep an administrator that is a user or group (AllMembers + deny combinations are still allowed).
        if (!wanted.Any(g => g.Action == PermissionAction.ManagePermissions && g.Effect == PermissionEffect.Allow && g.SubjectType != PermissionSubjectType.AllMembers))
        {
            return Result<SpacePermissionsDto>.Failure(WikiErrors.LastAdministrator);
        }

        var existing = await db.Grants.Where(g => g.Scope == PermissionScope.Space && g.SpaceId == space.Id).ToListAsync(ct).ConfigureAwait(false);
        db.Grants.RemoveRange(existing);
        foreach (var g in wanted)
        {
            db.Grants.Add(PermissionGrant.ForSpace(space.Id, g.SubjectType, g.SubjectId, g.Action, g.Effect));
        }

        db.Publish(WikiEventTypes.SpacePermissionsChanged, new WikiEvent(space.Id, null, current.UserId, space.Name, null), clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return await GetPermissionsAsync(key, ct).ConfigureAwait(false);
    }

    // ── Tenant-scope grants (who may create spaces) ───────────────────────────────────────────

    public async Task<TenantPermissionsDto> GetTenantPermissionsAsync(CancellationToken ct)
    {
        var grants = await db.Grants.AsNoTracking().Where(g => g.Scope == PermissionScope.Tenant).ToListAsync(ct).ConfigureAwait(false);
        return new TenantPermissionsDto(await DescribeAsync(grants, ct).ConfigureAwait(false));
    }

    public async Task<Result<TenantPermissionsDto>> ReplaceTenantPermissionsAsync(ReplaceGrantsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var wanted = (request.Grants ?? []).Distinct().ToList();
        if (wanted.Any(g => g.Action != PermissionAction.Create))
        {
            return Result<TenantPermissionsDto>.Failure(WikiErrors.ActionNotAllowed);
        }

        var validation = await ValidateSubjectsAsync(wanted.Select(g => (g.SubjectType, g.SubjectId)), ct).ConfigureAwait(false);
        if (!validation.IsSuccess)
        {
            return Result<TenantPermissionsDto>.Failure(validation.Error!);
        }

        db.Grants.RemoveRange(await db.Grants.Where(g => g.Scope == PermissionScope.Tenant).ToListAsync(ct).ConfigureAwait(false));
        foreach (var g in wanted)
        {
            db.Grants.Add(PermissionGrant.ForTenant(g.SubjectType, g.SubjectId, g.Action, g.Effect));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TenantPermissionsDto>.Success(await GetTenantPermissionsAsync(ct).ConfigureAwait(false));
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads a space by key with the caller's rights; null when it does not exist (or <paramref name="requireView"/> fails).</summary>
    public async Task<SpaceRights?> LoadAsync(string key, bool requireView, CancellationToken ct, bool track = false)
    {
        var k = Space.NormalizeKey(key);
        var query = track ? db.Spaces : db.Spaces.AsNoTracking();
        var space = await query.FirstOrDefaultAsync(s => s.Key == k, ct).ConfigureAwait(false);
        if (space is null)
        {
            return null;
        }

        var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        return requireView && !rights.CanView ? null : rights;
    }

    private async Task<Result<SpaceRights>> LoadAdministeredAsync(string key, CancellationToken ct)
    {
        var loaded = await LoadAsync(key, requireView: false, ct, track: true).ConfigureAwait(false);
        if (loaded is null || !(loaded.CanView || loaded.CanAdminister))
        {
            return Result<SpaceRights>.Failure(WikiErrors.SpaceNotFound);
        }

        return loaded.CanAdminister ? Result<SpaceRights>.Success(loaded) : Result<SpaceRights>.Failure(WikiErrors.Forbidden);
    }

    private async Task<SpaceDto> ToDtoAsync(SpaceRights r, CancellationToken ct)
    {
        var s = r.Space;
        var names = await directory.GetUserNamesAsync([s.CreatedBy], ct).ConfigureAwait(false);
        return new SpaceDto(
            s.Id, s.Key, s.Name, s.Description, s.Type, s.Status, s.HomePageId, s.CreatedBy, names.GetValueOrDefault(s.CreatedBy), s.CreatedAt, s.UpdatedAt, s.Revision,
            new SpaceRightsDto(r.CanView, r.CanWrite(PermissionAction.Create), r.CanAdminister, r.CanWrite(PermissionAction.ManageTemplates)), s.RequirePublishApproval);
    }

    /// <summary>Users must be members of this tenant and groups must exist in it (foreign ids are rejected).</summary>
    public async Task<Result> ValidateSubjectsAsync(IEnumerable<(PermissionSubjectType Type, Guid? Id)> subjects, CancellationToken ct)
    {
        var list = subjects.ToList();
        try
        {
            foreach (var (type, id) in list)
            {
                PermissionGrant.ValidateSubject(type, id);
            }
        }
        catch (Raavi.Domain.Common.DomainException)
        {
            return Result.Failure(WikiErrors.SubjectNotFound);
        }

        var users = list.Where(s => s.Type == PermissionSubjectType.User).Select(s => s.Id!.Value).Distinct().ToList();
        var groups = list.Where(s => s.Type == PermissionSubjectType.Group).Select(s => s.Id!.Value).Distinct().ToList();
        var userNames = await directory.GetUserNamesAsync(users, ct).ConfigureAwait(false);
        var groupNames = await directory.GetGroupNamesAsync(groups, ct).ConfigureAwait(false);
        return users.All(userNames.ContainsKey) && groups.All(groupNames.ContainsKey) ? Result.Success() : Result.Failure(WikiErrors.SubjectNotFound);
    }

    private async Task<IReadOnlyList<GrantDto>> DescribeAsync(IReadOnlyCollection<PermissionGrant> grants, CancellationToken ct)
    {
        var users = await directory.GetUserNamesAsync(grants.Where(g => g.SubjectType == PermissionSubjectType.User).Select(g => g.SubjectId!.Value).ToList(), ct).ConfigureAwait(false);
        var groups = await directory.GetGroupNamesAsync(grants.Where(g => g.SubjectType == PermissionSubjectType.Group).Select(g => g.SubjectId!.Value).ToList(), ct).ConfigureAwait(false);
        return grants
            .Select(g => new GrantDto(g.SubjectType, g.SubjectId, g.SubjectType switch
            {
                PermissionSubjectType.User => users.GetValueOrDefault(g.SubjectId!.Value),
                PermissionSubjectType.Group => groups.GetValueOrDefault(g.SubjectId!.Value),
                _ => null,
            }, g.Action, g.Effect))
            .OrderBy(g => g.SubjectType).ThenBy(g => g.SubjectName).ThenBy(g => g.Action)
            .ToList();
    }
}
