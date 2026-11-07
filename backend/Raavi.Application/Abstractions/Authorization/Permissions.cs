namespace Raavi.Application.Abstractions.Authorization;

/// <summary>Actions evaluated by the central permission evaluator. Values are persisted; never renumber.</summary>
public enum PermissionAction
{
    View = 1,
    Create = 2,
    Edit = 3,
    Delete = 4,
    Publish = 5,
    Archive = 6,
    Move = 7,
    Comment = 8,
    ManageComments = 9,
    AttachFiles = 10,
    DownloadFiles = 11,
    Share = 12,
    ManagePermissions = 13,
    ManageSpaces = 14,
    ManageTemplates = 15,
    ManageWorkflows = 16,
    ManageAutomation = 17,
}

/// <summary>Scope levels of the permission model, from widest to narrowest.</summary>
public enum PermissionScope
{
    System = 1,
    Tenant = 2,
    Space = 3,
    Content = 4,
}

/// <summary>Identifies a protected resource. The tenant is always taken from <c>ITenantContext</c>, never from this reference.</summary>
public sealed record ResourceRef(PermissionScope Scope, Guid? SpaceId = null, Guid? ContentId = null, string? ContentType = null);

public sealed record AuthorizationDecision(bool IsAllowed, string ReasonCode)
{
    public static AuthorizationDecision Allow(string reason = "allowed") => new(true, reason);

    public static AuthorizationDecision Deny(string reason) => new(false, reason);
}

/// <summary>
/// Single authority for "may the current principal perform this action on this resource".
/// Every surface (REST, search, attachments, exports, AI context, collaboration tickets, notifications) must use it.
/// The evaluation order is defined in docs/architecture/adr/ADR-012-permission-and-acl.md.
/// </summary>
public interface IPermissionEvaluator
{
    ValueTask<AuthorizationDecision> AuthorizeAsync(ResourceRef resource, PermissionAction action, CancellationToken cancellationToken = default);
}
