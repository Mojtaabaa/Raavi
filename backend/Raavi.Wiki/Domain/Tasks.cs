using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

public enum WorkTaskStatus
{
    Open = 1,
    Done = 2,
}

/// <summary>
/// A task of a space (Phase 9a): title, notes, optional assignee, due date and link to a page of the same space. Permissions come
/// from the space (read = View; create/edit/delete = Edit; the assignee may also complete or reopen their own task).
/// </summary>
public sealed class WorkTask : Entity, ITenantOwned
{
    public const int MaxTitleLength = 300;
    public const int MaxNotesLength = 4000;

    private WorkTask() { }

    public Guid TenantId { get; private set; }

    public Guid SpaceId { get; private set; }

    /// <summary>Optional page of the same space the task is about.</summary>
    public Guid? ContentId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public string? Notes { get; private set; }

    public WorkTaskStatus Status { get; private set; } = WorkTaskStatus.Open;

    public Guid? AssigneeId { get; private set; }

    public DateOnly? DueDate { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public Guid? CompletedBy { get; private set; }

    public int Revision { get; private set; } = 1;

    public static WorkTask Create(Guid spaceId, Guid? contentId, string title, string? notes, Guid? assigneeId, DateOnly? dueDate, Guid userId, DateTimeOffset now)
    {
        var t = new WorkTask { SpaceId = spaceId, ContentId = contentId, AssigneeId = assigneeId, DueDate = dueDate, CreatedBy = userId, CreatedAt = now, UpdatedAt = now };
        t.SetText(title, notes);
        return t;
    }

    public void Update(Guid? contentId, string title, string? notes, Guid? assigneeId, DateOnly? dueDate, DateTimeOffset now)
    {
        SetText(title, notes);
        ContentId = contentId;
        AssigneeId = assigneeId;
        DueDate = dueDate;
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
