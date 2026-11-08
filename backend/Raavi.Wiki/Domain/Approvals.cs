using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

public enum ApprovalMode
{
    /// <summary>Every approver must approve.</summary>
    All = 1,

    /// <summary>The first approval is enough.</summary>
    Any = 2,
}

public enum ApprovalStatus
{
    Pending = 1,
    Approved = 2,
    Rejected = 3,
    Cancelled = 4,
}

public enum ApprovalDecision
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
}

/// <summary>
/// A request to have named people approve a page as it is now (Phase 10b). Independent of publishing (ADR-019): it records a decision, it does
/// not publish anything. One rejection ends the request; otherwise it is approved when all (mode <c>All</c>) or any one (mode <c>Any</c>) of the
/// approvers approved. The revision is a concurrency token, so two approvers deciding at the same moment cannot both miss the final outcome.
/// </summary>
public sealed class ApprovalRequest : Entity, ITenantOwned
{
    public const int MaxNoteLength = 500;
    public const int MaxCommentLength = 1000;
    public const int MaxApprovers = 10;

    private ApprovalRequest() { }

    public Guid TenantId { get; private set; }

    public Guid SpaceId { get; private set; }

    public Guid ContentId { get; private set; }

    public string Note { get; private set; } = string.Empty;

    public ApprovalMode Mode { get; private set; }

    public ApprovalStatus Status { get; private set; } = ApprovalStatus.Pending;

    /// <summary>Version of the page the approvers were asked about (0 for a page that has no published version yet).</summary>
    public int VersionNumber { get; private set; }

    public Guid RequestedBy { get; private set; }

    public DateTimeOffset RequestedAt { get; private set; }

    public DateTimeOffset? DueAt { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public static ApprovalRequest Create(Guid spaceId, Guid contentId, int versionNumber, string? note, ApprovalMode mode, DateTimeOffset? dueAt, Guid requestedBy, DateTimeOffset now)
    {
        var n = (note ?? string.Empty).Trim();
        if (n.Length > MaxNoteLength || n.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
        {
            throw new DomainException("approval.note_invalid", $"The note is limited to {MaxNoteLength} characters.");
        }

        if (!Enum.IsDefined(mode))
        {
            throw new DomainException("approval.mode_invalid", "The approval mode must be All or Any.");
        }

        if (dueAt is { } due && (due <= now || due > now.AddYears(1)))
        {
            throw new DomainException("approval.due_invalid", "The due date must be in the future and within a year.");
        }

        return new ApprovalRequest { SpaceId = spaceId, ContentId = contentId, VersionNumber = versionNumber, Note = n, Mode = mode, DueAt = dueAt, RequestedBy = requestedBy, RequestedAt = now };
    }

    public bool IsOverdue(DateTimeOffset now) => Status == ApprovalStatus.Pending && DueAt is { } d && d < now;

    /// <summary>Recomputes the outcome after a decision. Returns true when the request just became final.</summary>
    public bool Resolve(IReadOnlyCollection<ApprovalAssignment> assignments, DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Pending)
        {
            return false;
        }

        Revision++;
        ApprovalStatus? outcome = null;
        if (assignments.Any(a => a.Decision == ApprovalDecision.Rejected))
        {
            outcome = ApprovalStatus.Rejected;
        }
        else if (Mode == ApprovalMode.Any ? assignments.Any(a => a.Decision == ApprovalDecision.Approved) : assignments.All(a => a.Decision == ApprovalDecision.Approved))
        {
            outcome = ApprovalStatus.Approved;
        }

        if (outcome is null)
        {
            return false;
        }

        Status = outcome.Value;
        DecidedAt = now;
        return true;
    }

    public void Cancel(DateTimeOffset now)
    {
        if (Status != ApprovalStatus.Pending)
        {
            throw new DomainException("approval.not_pending", "This request has already been decided.");
        }

        Revision++;
        Status = ApprovalStatus.Cancelled;
        DecidedAt = now;
    }
}

/// <summary>One approver's slot in a request.</summary>
public sealed class ApprovalAssignment : ITenantOwned
{
    private ApprovalAssignment() { }

    public Guid TenantId { get; private set; }

    public Guid RequestId { get; private set; }

    public Guid UserId { get; private set; }

    public ApprovalDecision Decision { get; private set; }

    public string? Comment { get; private set; }

    public DateTimeOffset? DecidedAt { get; private set; }

    public static ApprovalAssignment Create(Guid requestId, Guid userId) => new() { RequestId = requestId, UserId = userId };

    public void Decide(ApprovalDecision decision, string? comment, DateTimeOffset now)
    {
        if (decision is not (ApprovalDecision.Approved or ApprovalDecision.Rejected))
        {
            throw new DomainException("approval.decision_invalid", "The decision must be Approve or Reject.");
        }

        if (Decision != ApprovalDecision.Pending)
        {
            throw new DomainException("approval.already_decided", "You have already decided on this request.");
        }

        var c = (comment ?? string.Empty).Trim();
        if (c.Length > ApprovalRequest.MaxCommentLength || c.Any(ch => char.IsControl(ch) && ch is not ('\n' or '\t')))
        {
            throw new DomainException("approval.comment_invalid", $"The comment is limited to {ApprovalRequest.MaxCommentLength} characters.");
        }

        if (decision == ApprovalDecision.Rejected && c.Length == 0)
        {
            throw new DomainException("approval.comment_required", "A rejection needs a comment.");
        }

        Decision = decision;
        Comment = c.Length == 0 ? null : c;
        DecidedAt = now;
    }
}
