using System.Text.Json;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Common;
using Raavi.Wiki.Domain;

namespace Raavi.Wiki.Application;

public enum SpaceVisibility
{
    /// <summary>Every member may view, create, edit and move content; the creator administers.</summary>
    Open = 1,

    /// <summary>Only the creator (who may then grant others).</summary>
    Private = 2,
}

public sealed record CreateSpaceRequest(string Key, string Name, string? Description, SpaceType Type, SpaceVisibility Visibility);

public sealed record UpdateSpaceRequest(string Name, string? Description, SpaceType Type, Guid? HomePageId);

public sealed record SpaceSummaryDto(Guid Id, string Key, string Name, string? Description, SpaceType Type, SpaceStatus Status, Guid? HomePageId, DateTimeOffset UpdatedAt);

public sealed record SpaceRightsDto(bool CanView, bool CanCreate, bool CanAdminister, bool CanManageTemplates);

public sealed record SpaceDto(
    Guid Id, string Key, string Name, string? Description, SpaceType Type, SpaceStatus Status, Guid? HomePageId,
    Guid CreatedBy, string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Revision, SpaceRightsDto Rights, bool RequirePublishApproval = false);

public sealed record GrantDto(PermissionSubjectType SubjectType, Guid? SubjectId, string? SubjectName, PermissionAction Action, PermissionEffect Effect);

public sealed record SpacePermissionsDto(IReadOnlyList<GrantDto> Grants, IReadOnlyList<PermissionAction> Actions);

public sealed record GrantRequest(PermissionSubjectType SubjectType, Guid? SubjectId, PermissionAction Action, PermissionEffect Effect);

public sealed record ReplaceGrantsRequest(IReadOnlyList<GrantRequest> Grants);

public sealed record TreeNodeDto(Guid Id, Guid? ParentId, string ContentType, string Title, string Slug, int Position, bool HasChildren);

public sealed record BreadcrumbDto(Guid Id, string Title, string ContentType);

public sealed record ContentRightsDto(bool CanEdit, bool CanCreateChild, bool CanMove, bool CanArchive, bool CanDelete, bool CanRestrict, bool CanPublish, bool CanApprove, bool CanComment, bool CanModerateComments);

public sealed record PageDto(
    Guid Id, Guid SpaceId, string SpaceKey, string SpaceName, Guid? ParentId, string ContentType, string Title, string Slug,
    ContentStatus Status, JsonElement? Body, int Version, IReadOnlyList<string> Labels, IReadOnlyList<BreadcrumbDto> Breadcrumbs,
    bool IsRestricted, Guid CreatedBy, string? CreatedByName, DateTimeOffset CreatedAt, Guid UpdatedBy, string? UpdatedByName,
    DateTimeOffset UpdatedAt, int Revision, ContentRightsDto Rights,
    DateTimeOffset? PublishedAt = null, bool HasDraft = false, DateTimeOffset? DraftUpdatedAt = null, DateTimeOffset? ScheduledPublishAt = null, bool PendingApproval = false, bool RequirePublishApproval = false);

/// <param name="Publish">false = create an unpublished draft page (visible to its creator only) — the editor's flow. Defaults to true.</param>
public sealed record CreatePageRequest(string Title, Guid? ParentId, string? ContentType, JsonElement? Body, Guid? TemplateId, bool? Publish = null);

public sealed record UpdatePageRequest(string Title, JsonElement? Body, string? ChangeSummary);

public sealed record MovePageRequest(string? SpaceKey, Guid? ParentId, int? Position);

public sealed record VersionSummaryDto(int Version, string Title, string? ChangeSummary, Guid? CreatedBy, string? CreatedByName, DateTimeOffset CreatedAt, VersionType Type = VersionType.Published, int? RestoredFrom = null, IReadOnlyList<string>? Contributors = null);

public sealed record RestrictionSubjectDto(PermissionSubjectType SubjectType, Guid SubjectId, string? Name);

public sealed record InheritedRestrictionDto(Guid ContentId, string Title);

public sealed record RestrictionsDto(IReadOnlyList<RestrictionSubjectDto> View, IReadOnlyList<RestrictionSubjectDto> Edit, IReadOnlyList<InheritedRestrictionDto> InheritedFrom);

public sealed record RestrictionSubjectRequest(PermissionSubjectType SubjectType, Guid SubjectId);

public sealed record ReplaceRestrictionsRequest(IReadOnlyList<RestrictionSubjectRequest> View, IReadOnlyList<RestrictionSubjectRequest> Edit);

public sealed record LabelRequest(string Name);

public sealed record ContentListItemDto(Guid Id, string ContentType, string Title, string Slug, Guid SpaceId, string SpaceKey, string SpaceName, DateTimeOffset UpdatedAt);

public sealed record QuickFindDto(IReadOnlyList<SpaceSummaryDto> Spaces, IReadOnlyList<ContentListItemDto> Content);

public sealed record TemplateDto(Guid Id, Guid? SpaceId, string Name, string? Description, JsonElement Body, DateTimeOffset UpdatedAt, int Revision, bool CanManage);

public sealed record TemplateRequest(string Name, string? Description, JsonElement? Body);

public sealed record ActivityDto(Guid Id, string Kind, Guid SpaceId, string SpaceKey, Guid? ContentId, string Title, int? Version, Guid ActorId, string? ActorName, DateTimeOffset OccurredAt);

public sealed record TenantPermissionsDto(IReadOnlyList<GrantDto> Grants);

/// <summary>Stable error codes of the wiki module.</summary>
public static class WikiErrors
{
    public static readonly Error SpaceNotFound = new("space.not_found", "Space not found.", ErrorKind.NotFound);
    public static readonly Error ContentNotFound = new("content.not_found", "Content not found.", ErrorKind.NotFound);
    public static readonly Error TemplateNotFound = new("template.not_found", "Template not found.", ErrorKind.NotFound);
    public static readonly Error Forbidden = new("permission.denied", "You do not have permission to do this.", ErrorKind.Forbidden);
    public static readonly Error KeyTaken = new("space.key_taken", "This space key is already in use.", ErrorKind.Conflict);
    public static readonly Error LastAdministrator = new("space.last_administrator", "A space needs at least one administrator.", ErrorKind.Conflict);
    public static readonly Error SubjectNotFound = new("permission.subject_not_found", "A user or group in the list does not exist in this workspace.", ErrorKind.Validation);
    public static readonly Error ActionNotAllowed = new("permission.action_invalid", "This permission cannot be granted here.", ErrorKind.Validation);
    public static readonly Error MoveCycle = new("content.move_into_descendant", "Content cannot be moved below itself.", ErrorKind.Validation);
    public static readonly Error ParentNotContainer = new("content.parent_invalid", "The selected parent cannot contain content.", ErrorKind.Validation);
    public static readonly Error ParentNotCurrent = new("content.parent_not_current", "The parent is archived or deleted.", ErrorKind.Conflict);
    public static readonly Error ExcludesSelf = new("restriction.excludes_self", "You must stay included in the restriction.", ErrorKind.Validation);
    public static readonly Error HomePageInvalid = new("space.home_page_invalid", "The home page must be a page of this space.", ErrorKind.Validation);
    public static readonly Error IsHomePage = new("content.is_home_page", "The space home page cannot be moved to another space, archived or deleted.", ErrorKind.Conflict);
    public static readonly Error SpaceArchived = new("space.archived", "The space is archived and read-only.", ErrorKind.Conflict);
}
