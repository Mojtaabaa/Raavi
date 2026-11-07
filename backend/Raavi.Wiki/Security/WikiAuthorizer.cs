using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Content;
using Raavi.Application.Abstractions.Identity;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;

namespace Raavi.Wiki.Security;

/// <summary>Effective rights of the current principal on one space (grants only; restrictions are per content).</summary>
public sealed record SpaceRights(Space Space, IReadOnlySet<PermissionAction> Allowed, bool IsRecoveryAdministrator)
{
    public bool Can(PermissionAction action) => Allowed.Contains(action);

    public bool CanView => Can(PermissionAction.View);

    /// <summary>Space administration: settings, permissions, archive. Tenant administrators get it for recovery (ADR-012 step 9).</summary>
    public bool CanAdminister => Can(PermissionAction.ManagePermissions) || IsRecoveryAdministrator;

    public bool CanWrite(PermissionAction action) => Space.Status == SpaceStatus.Active && Can(action);
}

/// <summary>Effective rights on one content node, after space grants and content restrictions.</summary>
public sealed record ContentRights(bool CanView, bool CanEdit, bool CanCreateChild, bool CanMove, bool CanArchive, bool CanDelete, bool CanRestrict, bool CanPublish = false, bool CanApprove = false, bool CanComment = false, bool CanModerateComments = false);

/// <summary>
/// Who the wiki acts for: the signed-in user, or — inside a background job — the user who scheduled the work, whose
/// permissions are re-evaluated when the job runs.
/// </summary>
public sealed class WikiActor(ICurrentUser currentUser)
{
    private Guid? _override;

    public bool IsAuthenticated => _override is not null || currentUser.IsAuthenticated;

    public Guid UserId => _override ?? currentUser.UserId;

    /// <summary>True inside background work acting for a user: permissions must be read fresh.</summary>
    public bool IsBackground => _override is not null;

    public void ActAs(Guid userId) => _override = userId;
}

/// <summary>
/// PermissionEvaluator v1 (ADR-012): space grants (allow/deny, deny wins) at tenant and space scope, inherited View
/// restrictions and node-only Edit restrictions. Content the caller cannot view is reported as not found. Tenant
/// administrators may administer every space's permissions (recovery) but do <b>not</b> bypass View for reading content.
/// Not yet: content-scope grants, role subjects, anonymous access, shared links (later phases).
/// </summary>
public sealed class WikiAuthorizer(WikiDbContext db, IPrincipalDirectory directory, WikiActor currentUser, IContentProviderRegistry providers) : IPermissionEvaluator
{
    /// <summary>Actions that may be granted on a space.</summary>
    public static readonly PermissionAction[] SpaceActions =
    [
        PermissionAction.View, PermissionAction.Create, PermissionAction.Edit, PermissionAction.Move, PermissionAction.Archive,
        PermissionAction.Delete, PermissionAction.Publish, PermissionAction.Comment, PermissionAction.ManageTemplates, PermissionAction.ManagePermissions,
    ];

    private PrincipalSnapshot? _principal;

    public async ValueTask<PrincipalSnapshot> PrincipalAsync(CancellationToken ct = default)
    {
        if (_principal is not null)
        {
            return _principal;
        }

        _principal = currentUser.IsAuthenticated
            ? await directory.GetPrincipalAsync(currentUser.UserId, currentUser.IsBackground, ct).ConfigureAwait(false)
            : PrincipalSnapshot.None(Guid.Empty);
        return _principal;
    }

    public static bool Matches(PrincipalSnapshot principal, PermissionSubjectType type, Guid? subjectId)
    {
        ArgumentNullException.ThrowIfNull(principal);
        return type switch
        {
            PermissionSubjectType.AllMembers => principal.IsActiveMember,
            PermissionSubjectType.User => subjectId == principal.UserId,
            PermissionSubjectType.Group => subjectId is { } g && principal.GroupIds.Contains(g),
            _ => false,
        };
    }

    // ── Tenant scope ───────────────────────────────────────────────────────────────────────────

    public async Task<bool> CanCreateSpacesAsync(CancellationToken ct)
    {
        var p = await PrincipalAsync(ct).ConfigureAwait(false);
        if (!p.IsActiveMember)
        {
            return false;
        }

        if (p.IsTenantAdministrator)
        {
            return true;
        }

        var grants = await db.Grants.AsNoTracking().Where(g => g.Scope == PermissionScope.Tenant && g.Action == PermissionAction.Create).ToListAsync(ct).ConfigureAwait(false);
        return Decide(p, grants);
    }

    // ── Space scope ────────────────────────────────────────────────────────────────────────────

    public async Task<SpaceRights> GetSpaceRightsAsync(Space space, CancellationToken ct) =>
        await GetSpaceRightsAsync(space, await PrincipalAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    /// <summary>Rights of an explicit principal (e.g. a notification recipient) instead of the current actor.</summary>
    public async Task<SpaceRights> GetSpaceRightsAsync(Space space, PrincipalSnapshot p, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(p);
        if (!p.IsActiveMember)
        {
            return new SpaceRights(space, new HashSet<PermissionAction>(), false);
        }

        var grants = await db.Grants.AsNoTracking().Where(g => g.Scope == PermissionScope.Space && g.SpaceId == space.Id).ToListAsync(ct).ConfigureAwait(false);
        var allowed = new HashSet<PermissionAction>();
        foreach (var group in grants.Where(g => Matches(p, g.SubjectType, g.SubjectId)).GroupBy(g => g.Action))
        {
            if (group.Any(g => g.Effect == PermissionEffect.Allow) && group.All(g => g.Effect != PermissionEffect.Deny))
            {
                allowed.Add(group.Key);
            }
        }

        // Every right presupposes View: without it the space is invisible (administration is the only exception).
        if (!allowed.Contains(PermissionAction.View))
        {
            var admin = allowed.Contains(PermissionAction.ManagePermissions);
            allowed.Clear();
            if (admin)
            {
                allowed.Add(PermissionAction.ManagePermissions);
            }
        }

        return new SpaceRights(space, allowed, p.IsTenantAdministrator);
    }

    /// <summary>Ids of spaces the caller may view (allow minus deny on View).</summary>
    public async Task<HashSet<Guid>> ViewableSpaceIdsAsync(CancellationToken ct)
    {
        var p = await PrincipalAsync(ct).ConfigureAwait(false);
        if (!p.IsActiveMember)
        {
            return [];
        }

        var groupIds = p.GroupIds.ToList();
        var rows = await db.Grants.AsNoTracking()
            .Where(g => g.Scope == PermissionScope.Space && g.Action == PermissionAction.View
                && (g.SubjectType == PermissionSubjectType.AllMembers
                    || (g.SubjectType == PermissionSubjectType.User && g.SubjectId == p.UserId)
                    || (g.SubjectType == PermissionSubjectType.Group && groupIds.Contains(g.SubjectId!.Value))))
            .Select(g => new { g.SpaceId, g.Effect })
            .ToListAsync(ct).ConfigureAwait(false);
        var denied = rows.Where(r => r.Effect == PermissionEffect.Deny).Select(r => r.SpaceId!.Value).ToHashSet();
        return rows.Where(r => r.Effect == PermissionEffect.Allow && !denied.Contains(r.SpaceId!.Value)).Select(r => r.SpaceId!.Value).ToHashSet();
    }

    // ── Content scope ──────────────────────────────────────────────────────────────────────────

    public async Task<ContentRights> GetContentRightsAsync(ContentNode node, SpaceRights space, IContentProvider type, CancellationToken ct) =>
        await GetContentRightsAsync(node, space, type, await PrincipalAsync(ct).ConfigureAwait(false), ct).ConfigureAwait(false);

    /// <summary>Content rights of an explicit principal; <paramref name="space"/> must have been evaluated for the same principal.</summary>
    public async Task<ContentRights> GetContentRightsAsync(ContentNode node, SpaceRights space, IContentProvider type, PrincipalSnapshot p, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(p);
        var none = new ContentRights(false, false, false, false, false, false, false);
        if (!space.CanView || node.Status == ContentStatus.Trashed)
        {
            return none;
        }

        // A never-published page is visible to its creator only (ADR-019).
        if (node.Status == ContentStatus.Draft && node.CreatedBy != p.UserId)
        {
            return none;
        }

        if (!await PassesViewGateAsync(p, node.Id, ct).ConfigureAwait(false))
        {
            return none;
        }

        var editRestrictions = await db.Restrictions.AsNoTracking().Where(r => r.ContentId == node.Id && r.Kind == RestrictionKind.Edit).ToListAsync(ct).ConfigureAwait(false);
        var passesEdit = editRestrictions.Count == 0 || editRestrictions.Any(r => Matches(p, r.SubjectType, r.SubjectId));
        var current = node.Status == ContentStatus.Current;
        var editable = node.Status is ContentStatus.Current or ContentStatus.Draft;
        var edit = passesEdit && editable && space.CanWrite(PermissionAction.Edit);
        return new ContentRights(
            CanView: true,
            CanEdit: edit,
            CanCreateChild: current && type.Capabilities.HasFlag(ContentTypeCapabilities.CanHaveChildren) && space.CanWrite(PermissionAction.Create),
            CanMove: passesEdit && current && space.CanWrite(PermissionAction.Move),
            CanArchive: passesEdit && node.Status != ContentStatus.Draft && space.CanWrite(PermissionAction.Archive),
            CanDelete: passesEdit && node.Status != ContentStatus.Draft && space.CanWrite(PermissionAction.Delete),
            CanRestrict: current && ((passesEdit && space.CanWrite(PermissionAction.Edit)) || space.CanAdminister),
            CanPublish: edit && space.CanWrite(PermissionAction.Publish),
            CanApprove: space.Space.Status == SpaceStatus.Active && space.CanAdminister && space.CanView,
            CanComment: current && space.CanWrite(PermissionAction.Comment),
            CanModerateComments: current && space.Space.Status == SpaceStatus.Active && space.CanAdminister);
    }

    /// <summary>Every ancestor-or-self carrying View restrictions must list a principal of the caller (ADR-012 step 5).</summary>
    public async Task<bool> PassesViewGateAsync(PrincipalSnapshot principal, Guid contentId, CancellationToken ct)
    {
        var visible = await FilterByViewRestrictionsAsync(principal, [contentId], ct).ConfigureAwait(false);
        return visible.Contains(contentId);
    }

    /// <summary>
    /// Of <paramref name="contentIds"/>, those whose restricted ancestors-or-self all admit the caller. Space visibility is
    /// <b>not</b> checked here — combine with <see cref="ViewableSpaceIdsAsync"/>.
    /// </summary>
    public async Task<HashSet<Guid>> FilterByViewRestrictionsAsync(PrincipalSnapshot principal, IReadOnlyCollection<Guid> contentIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(principal);
        ArgumentNullException.ThrowIfNull(contentIds);
        var ids = contentIds.Distinct().ToList();
        if (ids.Count == 0 || !principal.IsActiveMember)
        {
            return [];
        }

        var rows = await (from c in db.Closure.AsNoTracking()
                          join r in db.Restrictions.AsNoTracking() on c.AncestorId equals r.ContentId
                          where ids.Contains(c.DescendantId) && r.Kind == RestrictionKind.View
                          select new { c.DescendantId, c.AncestorId, r.SubjectType, r.SubjectId })
            .ToListAsync(ct).ConfigureAwait(false);
        var blocked = rows
            .GroupBy(r => (r.DescendantId, r.AncestorId))
            .Where(g => !g.Any(r => Matches(principal, r.SubjectType, r.SubjectId)))
            .Select(g => g.Key.DescendantId)
            .ToHashSet();
        return ids.Where(id => !blocked.Contains(id)).ToHashSet();
    }

    /// <summary>Content the caller may view, for listings (quick find, labels, activity): space View + restrictions, not trashed.</summary>
    public async Task<HashSet<Guid>> FilterViewableAsync(IReadOnlyCollection<(Guid ContentId, Guid SpaceId)> candidates, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        if (candidates.Count == 0)
        {
            return [];
        }

        var spaces = await ViewableSpaceIdsAsync(ct).ConfigureAwait(false);
        var inSpace = candidates.Where(c => spaces.Contains(c.SpaceId)).Select(c => c.ContentId).ToList();
        var p = await PrincipalAsync(ct).ConfigureAwait(false);
        return await FilterByViewRestrictionsAsync(p, inSpace, ct).ConfigureAwait(false);
    }

    // ── IPermissionEvaluator (other modules) ─────────────────────────────────────────────────────

    public async ValueTask<AuthorizationDecision> AuthorizeAsync(ResourceRef resource, PermissionAction action, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resource);
        var p = await PrincipalAsync(cancellationToken).ConfigureAwait(false);
        if (!p.IsActiveMember)
        {
            return AuthorizationDecision.Deny("not_member");
        }

        switch (resource.Scope)
        {
            case PermissionScope.Tenant:
                return action == PermissionAction.Create && await CanCreateSpacesAsync(cancellationToken).ConfigureAwait(false)
                    || p.IsTenantAdministrator
                    ? AuthorizationDecision.Allow()
                    : AuthorizationDecision.Deny("forbidden");

            case PermissionScope.Space:
            {
                var space = resource.SpaceId is { } sid ? await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sid, cancellationToken).ConfigureAwait(false) : null;
                if (space is null)
                {
                    return AuthorizationDecision.Deny("not_found");
                }

                var rights = await GetSpaceRightsAsync(space, cancellationToken).ConfigureAwait(false);
                if (action == PermissionAction.ManagePermissions && rights.CanAdminister)
                {
                    return AuthorizationDecision.Allow();
                }

                return !rights.CanView ? AuthorizationDecision.Deny("not_found")
                    : rights.Can(action) ? AuthorizationDecision.Allow() : AuthorizationDecision.Deny("forbidden");
            }

            case PermissionScope.Content:
            {
                var node = resource.ContentId is { } cid ? await db.Nodes.AsNoTracking().FirstOrDefaultAsync(n => n.Id == cid, cancellationToken).ConfigureAwait(false) : null;
                var space = node is null ? null : await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == node.SpaceId, cancellationToken).ConfigureAwait(false);
                if (node is null || space is null)
                {
                    return AuthorizationDecision.Deny("not_found");
                }

                var spaceRights = await GetSpaceRightsAsync(space, cancellationToken).ConfigureAwait(false);
                var rights = await GetContentRightsAsync(node, spaceRights, providers.Get(node.ContentType), cancellationToken).ConfigureAwait(false);
                if (!rights.CanView)
                {
                    return AuthorizationDecision.Deny("not_found");
                }

                var ok = action switch
                {
                    PermissionAction.View => true,
                    PermissionAction.Edit => rights.CanEdit,
                    PermissionAction.Create => rights.CanCreateChild,
                    PermissionAction.Move => rights.CanMove,
                    PermissionAction.Archive => rights.CanArchive,
                    PermissionAction.Delete => rights.CanDelete,
                    PermissionAction.ManagePermissions => rights.CanRestrict,
                    _ => spaceRights.Can(action),
                };
                return ok ? AuthorizationDecision.Allow() : AuthorizationDecision.Deny("forbidden");
            }

            default:
                return AuthorizationDecision.Deny("forbidden");
        }
    }

    private static bool Decide(PrincipalSnapshot p, IEnumerable<PermissionGrant> grants)
    {
        var matching = grants.Where(g => Matches(p, g.SubjectType, g.SubjectId)).ToList();
        return matching.Any(g => g.Effect == PermissionEffect.Allow) && matching.All(g => g.Effect != PermissionEffect.Deny);
    }
}
