using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

/// <summary>
/// A logged work interval on a WorkTask (B5 / ADR-056 M4). Soft-deleted rows keep history.
/// Opening-balance rows preserve pre–time-tracking <see cref="WorkTask.ActualSpentMinutes"/> without inventing fake work.
/// </summary>
public sealed class TaskTimeEntry : Entity, ITenantOwned
{
    public const int MaxDescriptionLength = 1000;
    public const int MaxDurationMinutes = 24 * 60 * 30; // 30 calendar days in one entry
    public const int MaxPastDays = 366 * 5;

    private TaskTimeEntry() { }

    public Guid TenantId { get; private set; }

    public Guid TaskId { get; private set; }

    /// <summary>Who the time is attributed to (not necessarily the actor who created the row).</summary>
    public Guid UserId { get; private set; }

    public DateOnly WorkDate { get; private set; }

    public int DurationMinutes { get; private set; }

    public string? Description { get; private set; }

    /// <summary>True for rows seeded from legacy ActualSpentMinutes (migration / reconciliation).</summary>
    public bool IsOpeningBalance { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public Guid? DeletedBy { get; private set; }

    public int Revision { get; private set; }

    public bool IsDeleted => DeletedAt is not null;

    public static TaskTimeEntry Log(Guid taskId, Guid userId, DateOnly workDate, int durationMinutes, string? description, Guid createdBy, DateTimeOffset now, DateOnly today)
    {
        var e = new TaskTimeEntry
        {
            TaskId = taskId,
            UserId = userId,
            CreatedBy = createdBy,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1,
        };
        e.Apply(workDate, durationMinutes, description, today);
        return e;
    }

    public static TaskTimeEntry OpeningBalance(Guid taskId, Guid userId, DateOnly workDate, int durationMinutes, DateTimeOffset now) =>
        new()
        {
            TaskId = taskId,
            UserId = userId,
            WorkDate = workDate,
            DurationMinutes = durationMinutes,
            Description = "Opening balance (pre–time-tracking)",
            IsOpeningBalance = true,
            CreatedBy = userId,
            CreatedAt = now,
            UpdatedAt = now,
            Revision = 1,
        };

    public void Update(DateOnly workDate, int durationMinutes, string? description, DateTimeOffset now, DateOnly today)
    {
        EnsureActive();
        if (IsOpeningBalance)
        {
            throw new DomainException("task.time_opening_balance", "Opening-balance entries cannot be edited; log an adjustment instead.");
        }

        Apply(workDate, durationMinutes, description, today);
        UpdatedAt = now;
        Revision++;
    }

    public void SoftDelete(Guid by, DateTimeOffset now)
    {
        if (IsDeleted)
        {
            return;
        }

        DeletedAt = now;
        DeletedBy = by;
        UpdatedAt = now;
        Revision++;
    }

    private void Apply(DateOnly workDate, int durationMinutes, string? description, DateOnly today)
    {
        if (durationMinutes is < 1 or > MaxDurationMinutes)
        {
            throw new DomainException("task.time_duration_invalid", $"Duration must be between 1 and {MaxDurationMinutes} minutes.");
        }

        if (workDate > today)
        {
            throw new DomainException("task.time_date_future", "Work date cannot be in the future.");
        }

        if (workDate < today.AddDays(-MaxPastDays))
        {
            throw new DomainException("task.time_date_too_old", $"Work date cannot be more than {MaxPastDays} days in the past.");
        }

        var d = string.IsNullOrWhiteSpace(description) ? null : description.Trim();
        if (d is { Length: > MaxDescriptionLength })
        {
            throw new DomainException("task.time_description_too_long", $"Description is limited to {MaxDescriptionLength} characters.");
        }

        if (d is not null && d.Any(char.IsControl))
        {
            throw new DomainException("task.time_description_invalid", "Description contains invalid characters.");
        }

        WorkDate = workDate;
        DurationMinutes = durationMinutes;
        Description = d;
    }

    private void EnsureActive()
    {
        if (IsDeleted)
        {
            throw new DomainException("task.time_deleted", "This time entry was deleted.");
        }
    }
}
