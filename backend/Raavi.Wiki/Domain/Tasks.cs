using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

public enum WorkTaskStatus
{
    Open = 1,
    Done = 2,
}

public enum WorkTaskPriority
{
    Lowest = 1,
    Low = 2,
    Normal = 3,
    High = 4,
    Highest = 5,
}

/// <summary>Assignment roles on a native task (ADR-056).</summary>
public static class TaskAssigneeRoles
{
    public const string Responsible = "Responsible";
    public const string Contributor = "Contributor";
    public const string Reviewer = "Reviewer";
    public const string Approver = "Approver";
    public const string Observer = "Observer";
    public const int MaxLength = 32;

    public static readonly HashSet<string> All = new(StringComparer.OrdinalIgnoreCase)
    {
        Responsible, Contributor, Reviewer, Approver, Observer,
    };

    public static string Normalize(string? role)
    {
        var r = (role ?? string.Empty).Trim();
        foreach (var known in All)
        {
            if (string.Equals(known, r, StringComparison.OrdinalIgnoreCase))
            {
                return known;
            }
        }

        throw new DomainException("task.role_invalid", "Assignment role is not valid.");
    }
}

/// <summary>
/// A task of a space (Phase 9a + ADR-056): title, notes, optional assignees, due/start dates, estimates, optional page link.
/// Permissions come from the space (read = View; create/edit/delete = Edit; an assignee may complete or reopen).
/// </summary>
public sealed class WorkTask : Entity, ITenantOwned
{
    public const int MaxTitleLength = 300;
    public const int MaxNotesLength = 4000;
    public const int MaxParentDepth = 8;

    private WorkTask() { }

    public Guid TenantId { get; private set; }

    public Guid SpaceId { get; private set; }

    /// <summary>Optional page of the same space the task is about.</summary>
    public Guid? ContentId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Notes { get; private set; }

    public WorkTaskStatus Status { get; private set; } = WorkTaskStatus.Open;

    public WorkTaskPriority Priority { get; private set; } = WorkTaskPriority.Normal;

    /// <summary>Denormalized primary Responsible (9a dual-read/write).</summary>
    public Guid? AssigneeId { get; private set; }

    public DateOnly? DueDate { get; private set; }

    public DateOnly? StartDate { get; private set; }

    public Guid? ParentTaskId { get; private set; }

    public int? OriginalEstimateMinutes { get; private set; }

    public int? RemainingEstimateMinutes { get; private set; }

    /// <summary>Denormalized sum of time entries (B5 writes; B1 column).</summary>
    public int? ActualSpentMinutes { get; private set; }

    /// <summary>0–100 manual progress (checklists may derive later).</summary>
    public byte? ProgressPercent { get; private set; }

    /// <summary>When true, Complete fails if any non-archived Open child exists.</summary>
    public bool BlockCompleteWhenOpenChildren { get; private set; } = true;

    /// <summary>When true, Complete fails if any required checklist item is incomplete.</summary>
    public bool BlockCompleteWhenMandatoryIncomplete { get; private set; } = true;

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public Guid? CompletedBy { get; private set; }

    public DateTimeOffset? ArchivedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public static WorkTask Create(
        Guid spaceId,
        Guid? contentId,
        string title,
        string? notes,
        Guid? assigneeId,
        DateOnly? dueDate,
        Guid userId,
        DateTimeOffset now,
        WorkTaskPriority priority = WorkTaskPriority.Normal,
        DateOnly? startDate = null,
        Guid? parentTaskId = null,
        int? originalEstimateMinutes = null,
        int? remainingEstimateMinutes = null,
        byte? progressPercent = null,
        bool blockCompleteWhenOpenChildren = true,
        bool blockCompleteWhenMandatoryIncomplete = true)
    {
        var t = new WorkTask
        {
            SpaceId = spaceId,
            ContentId = contentId,
            AssigneeId = assigneeId,
            DueDate = dueDate,
            StartDate = startDate,
            ParentTaskId = parentTaskId,
            Priority = priority,
            OriginalEstimateMinutes = NormalizeEstimate(originalEstimateMinutes),
            RemainingEstimateMinutes = NormalizeEstimate(remainingEstimateMinutes ?? originalEstimateMinutes),
            ProgressPercent = NormalizeProgress(progressPercent),
            BlockCompleteWhenOpenChildren = blockCompleteWhenOpenChildren,
            BlockCompleteWhenMandatoryIncomplete = blockCompleteWhenMandatoryIncomplete,
            CreatedBy = userId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        t.SetText(title, notes);
        return t;
    }

    public void Update(
        Guid? contentId,
        string title,
        string? notes,
        Guid? assigneeId,
        DateOnly? dueDate,
        DateTimeOffset now,
        WorkTaskPriority? priority = null,
        DateOnly? startDate = null,
        bool setStartDate = false,
        Guid? parentTaskId = null,
        bool setParent = false,
        int? originalEstimateMinutes = null,
        bool setOriginalEstimate = false,
        int? remainingEstimateMinutes = null,
        bool setRemainingEstimate = false,
        byte? progressPercent = null,
        bool setProgress = false,
        bool? blockCompleteWhenOpenChildren = null,
        bool? blockCompleteWhenMandatoryIncomplete = null)
    {
        SetText(title, notes);
        ContentId = contentId;
        AssigneeId = assigneeId;
        DueDate = dueDate;
        if (priority is { } p)
        {
            Priority = p;
        }

        if (setStartDate)
        {
            StartDate = startDate;
        }

        if (setParent)
        {
            if (parentTaskId == Id)
            {
                throw new DomainException("task.parent_invalid", "A task cannot be its own parent.");
            }

            ParentTaskId = parentTaskId;
        }

        if (setOriginalEstimate)
        {
            OriginalEstimateMinutes = NormalizeEstimate(originalEstimateMinutes);
        }

        if (setRemainingEstimate)
        {
            RemainingEstimateMinutes = NormalizeEstimate(remainingEstimateMinutes);
        }

        if (setProgress)
        {
            ProgressPercent = NormalizeProgress(progressPercent);
        }

        if (blockCompleteWhenOpenChildren is { } block)
        {
            BlockCompleteWhenOpenChildren = block;
        }

        if (blockCompleteWhenMandatoryIncomplete is { } blockMandatory)
        {
            BlockCompleteWhenMandatoryIncomplete = blockMandatory;
        }

        Touch(now);
    }

    /// <summary>Promote to a root task (clear parent) without losing other fields.</summary>
    public void ClearParent(DateTimeOffset now)
    {
        ParentTaskId = null;
        Touch(now);
    }

    public void SetActualSpentMinutes(int? minutes, DateTimeOffset now)
    {
        ActualSpentMinutes = NormalizeEstimate(minutes);
        Touch(now);
    }

    public void SetPrimaryAssignee(Guid? userId, DateTimeOffset now)
    {
        AssigneeId = userId;
        Touch(now);
    }

    public void Complete(Guid userId, DateTimeOffset now)
    {
        if (Status == WorkTaskStatus.Done)
        {
            return;
        }

        Status = WorkTaskStatus.Done;
        CompletedAt = now;
        CompletedBy = userId;
        Touch(now);
    }

    public void Reopen(DateTimeOffset now)
    {
        if (Status == WorkTaskStatus.Open)
        {
            return;
        }

        Status = WorkTaskStatus.Open;
        CompletedAt = null;
        CompletedBy = null;
        Touch(now);
    }

    public void Archive(DateTimeOffset now)
    {
        if (ArchivedAt is not null)
        {
            return;
        }

        ArchivedAt = now;
        Touch(now);
    }

    public void Restore(DateTimeOffset now)
    {
        if (ArchivedAt is null)
        {
            return;
        }

        ArchivedAt = null;
        Touch(now);
    }

    private static int? NormalizeEstimate(int? minutes)
    {
        if (minutes is null)
        {
            return null;
        }

        if (minutes is < 0 or > 10_000_000)
        {
            throw new DomainException("task.estimate_invalid", "Estimate must be between 0 and 10000000 minutes.");
        }

        return minutes;
    }

    private static byte? NormalizeProgress(byte? percent)
    {
        if (percent is null)
        {
            return null;
        }

        if (percent > 100)
        {
            throw new DomainException("task.progress_invalid", "Progress must be between 0 and 100.");
        }

        return percent;
    }

    private void SetText(string title, string? notes)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length is 0 or > MaxTitleLength || t.Any(char.IsControl))
        {
            throw new DomainException("task.title_invalid", $"A task needs a title of 1–{MaxTitleLength} characters.");
        }

        var n = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        if (n is { Length: > MaxNotesLength })
        {
            throw new DomainException("task.notes_too_long", $"Notes are limited to {MaxNotesLength} characters.");
        }

        Title = t;
        Notes = n;
    }

    private void Touch(DateTimeOffset now)
    {
        UpdatedAt = now;
        Revision++;
    }
}

/// <summary>A user assigned to a task with a role (ADR-056). Soft-removed rows keep history.</summary>
public sealed class TaskAssignee : Entity, ITenantOwned
{
    private TaskAssignee() { }

    public Guid TenantId { get; private set; }

    public Guid TaskId { get; private set; }

    public Guid UserId { get; private set; }

    public string Role { get; private set; } = TaskAssigneeRoles.Responsible;

    public bool IsPrimary { get; private set; }

    public Guid AssignedBy { get; private set; }

    public DateTimeOffset AssignedAt { get; private set; }

    public DateTimeOffset? RemovedAt { get; private set; }

    public static TaskAssignee Assign(Guid taskId, Guid userId, string role, bool isPrimary, Guid assignedBy, DateTimeOffset now) => new()
    {
        TaskId = taskId,
        UserId = userId,
        Role = TaskAssigneeRoles.Normalize(role),
        IsPrimary = isPrimary,
        AssignedBy = assignedBy,
        AssignedAt = now,
    };

    public void SetPrimary(bool primary) => IsPrimary = primary;

    public void SetRole(string role) => Role = TaskAssigneeRoles.Normalize(role);

    public void Remove(DateTimeOffset now) => RemovedAt ??= now;
}
