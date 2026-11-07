using Raavi.Application.Abstractions.Authorization;
using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

/// <summary>Who a grant or restriction applies to. Values are persisted; 3 (Role) and 4 (Anonymous) are reserved (ADR-012).</summary>
public enum PermissionSubjectType
{
    User = 1,
    Group = 2,

    /// <summary>Every active member of the tenant.</summary>
    AllMembers = 5,
}

public enum PermissionEffect
{
    Allow = 1,
    Deny = 2,
}

public enum RestrictionKind
{
    /// <summary>Inherited by every descendant: a reader must satisfy every restricted ancestor-or-self.</summary>
    View = 1,

    /// <summary>Applies to the node only.</summary>
    Edit = 2,
}

/// <summary>
/// Grant at tenant scope (<see cref="SpaceId"/> null — e.g. "may create spaces") or space scope (ADR-012). Deny wins.
/// </summary>
public sealed class PermissionGrant : Entity, ITenantOwned
{
    private PermissionGrant() { }

    public Guid TenantId { get; private set; }

    public PermissionScope Scope { get; private set; }

    public Guid? SpaceId { get; private set; }

    public PermissionSubjectType SubjectType { get; private set; }

    /// <summary>User or group id; null for <see cref="PermissionSubjectType.AllMembers"/>.</summary>
    public Guid? SubjectId { get; private set; }

    public PermissionAction Action { get; private set; }

    public PermissionEffect Effect { get; private set; }

    public static PermissionGrant ForSpace(Guid spaceId, PermissionSubjectType subjectType, Guid? subjectId, PermissionAction action, PermissionEffect effect) =>
        Create(PermissionScope.Space, spaceId, subjectType, subjectId, action, effect);

    public static PermissionGrant ForTenant(PermissionSubjectType subjectType, Guid? subjectId, PermissionAction action, PermissionEffect effect) =>
        Create(PermissionScope.Tenant, null, subjectType, subjectId, action, effect);

    private static PermissionGrant Create(PermissionScope scope, Guid? spaceId, PermissionSubjectType subjectType, Guid? subjectId, PermissionAction action, PermissionEffect effect)
    {
        ValidateSubject(subjectType, subjectId);
        if (!Enum.IsDefined(action) || !Enum.IsDefined(effect))
        {
            throw new DomainException("permission.invalid", "Unknown permission action or effect.");
        }

        return new PermissionGrant { Scope = scope, SpaceId = spaceId, SubjectType = subjectType, SubjectId = subjectId, Action = action, Effect = effect };
    }

    public static void ValidateSubject(PermissionSubjectType subjectType, Guid? subjectId)
    {
        var ok = subjectType switch
        {
            PermissionSubjectType.User or PermissionSubjectType.Group => subjectId is { } id && id != Guid.Empty,
            PermissionSubjectType.AllMembers => subjectId is null,
            _ => false,
        };
        if (!ok)
        {
            throw new DomainException("permission.subject_invalid", "Invalid permission subject.");
        }
    }
}

/// <summary>Page restriction (Confluence semantics): only listed users/groups may view (inherited) or edit (node only).</summary>
public sealed class ContentRestriction : Entity, ITenantOwned
{
    private ContentRestriction() { }

    public Guid TenantId { get; private set; }

    public Guid ContentId { get; private set; }

    public RestrictionKind Kind { get; private set; }

    public PermissionSubjectType SubjectType { get; private set; }

    public Guid SubjectId { get; private set; }

    public static ContentRestriction Create(Guid contentId, RestrictionKind kind, PermissionSubjectType subjectType, Guid subjectId)
    {
        if (subjectType is not (PermissionSubjectType.User or PermissionSubjectType.Group) || subjectId == Guid.Empty || !Enum.IsDefined(kind))
        {
            throw new DomainException("restriction.subject_invalid", "Restrictions list users or groups.");
        }

        return new ContentRestriction { ContentId = contentId, Kind = kind, SubjectType = subjectType, SubjectId = subjectId };
    }
}
