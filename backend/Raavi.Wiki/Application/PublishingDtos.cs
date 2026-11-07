using System.Text.Json;
using Raavi.Application.Common;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Editor;

namespace Raavi.Wiki.Application;

public sealed record DraftDto(
    Guid ContentId, bool Exists, string Title, JsonElement Body, int BaseVersion, int PublishedVersion, bool IsStale, int Revision,
    Guid? UpdatedBy, string? UpdatedByName, DateTimeOffset? UpdatedAt, IReadOnlyList<string> Contributors,
    DateTimeOffset? ScheduledPublishAt, string? ScheduledByName, PublishRequestDto? PendingRequest);

public sealed record SaveDraftRequest(string Title, JsonElement? Body);

public sealed record PublishRequestBody(string? ChangeSummary, DateTimeOffset? PublishAt);

public enum PublishOutcome
{
    Published = 1,
    Scheduled = 2,
    PendingApproval = 3,

    /// <summary>Restore in a space that requires approval: the old version was copied into the draft.</summary>
    RestoredToDraft = 4,
}

public sealed record PublishResultDto(PublishOutcome Outcome, PageDto Page);

public sealed record PublishRequestDto(
    Guid Id, Guid ContentId, string Title, string? ChangeSummary, PublishRequestStatus Status, Guid RequestedBy, string? RequestedByName,
    DateTimeOffset RequestedAt, string? DecidedByName, DateTimeOffset? DecidedAt, string? DecisionComment);

public sealed record DecisionRequest(string? Comment);

public sealed record VersionDto(int Version, string Title, VersionType Type, int? RestoredFrom, JsonElement Body, string? ChangeSummary, string? CreatedByName, DateTimeOffset CreatedAt, IReadOnlyList<string> Contributors);

public sealed record DiffSideDto(string Label, int? Version, string Title);

public sealed record DiffDto(DiffSideDto From, DiffSideDto To, bool TitleChanged, IReadOnlyList<DiffBlock> Blocks);

public sealed record TrashItemDto(Guid Id, string Title, string ContentType, DateTimeOffset DeletedAt, string? DeletedByName, int Descendants);

public sealed record SpacePublishingRequest(bool RequireApproval);

public static class PublishingErrors
{
    public static readonly Error ApprovalRequired = new("content.approval_required", "This space requires approval before publishing; use the draft and request publication.", ErrorKind.Conflict);
    public static readonly Error NothingToPublish = new("content.no_draft", "There is no draft to publish.", ErrorKind.Conflict);
    public static readonly Error VersionNotFound = new("version.not_found", "Version not found.", ErrorKind.NotFound);
    public static readonly Error RequestNotFound = new("publish_request.not_found", "Request not found.", ErrorKind.NotFound);
    public static readonly Error NotTrashed = new("content.not_trashed", "The content is not in the trash.", ErrorKind.Conflict);
    public static readonly Error ScheduleInPast = new("content.schedule_in_past", "The publishing time must be in the future.", ErrorKind.Validation);
    public static readonly Error NotADraftPage = new("content.not_versioned", "This content type has no versions.", ErrorKind.Validation);
}
