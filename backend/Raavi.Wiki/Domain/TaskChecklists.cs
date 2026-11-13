using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

/// <summary>A named checklist on a WorkTask (ADR-056 B3). TipTap editor task lists are unrelated.</summary>
public sealed class TaskChecklist : Entity, ITenantOwned
{
    public const int MaxTitleLength = 200;

    private TaskChecklist() { }

    public Guid TenantId { get; private set; }

    public Guid TaskId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public int SortOrder { get; private set; }

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public static TaskChecklist Create(Guid taskId, string title, int sortOrder, Guid userId, DateTimeOffset now) => new()
    {
        TaskId = taskId,
        Title = NormalizeTitle(title),
        SortOrder = sortOrder,
        CreatedBy = userId,
        CreatedAt = now,
    };

    public void Rename(string title) => Title = NormalizeTitle(title);

    public void SetSortOrder(int sortOrder) => SortOrder = sortOrder;

    private static string NormalizeTitle(string title)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length is 0 or > MaxTitleLength || t.Any(char.IsControl))
        {
            throw new DomainException("task.checklist_title_invalid", $"Checklist title must be 1–{MaxTitleLength} characters.");
        }

        return t;
    }
}

/// <summary>An item on a task checklist; mandatory items can block task completion.</summary>
public sealed class TaskChecklistItem : Entity, ITenantOwned
{
    public const int MaxTitleLength = 500;

    private TaskChecklistItem() { }

    public Guid TenantId { get; private set; }

    public Guid ChecklistId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    public bool IsDone { get; private set; }

    public bool IsRequired { get; private set; }

    public int SortOrder { get; private set; }

    public DateTimeOffset? DoneAt { get; private set; }

    public Guid? DoneBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static TaskChecklistItem Create(Guid checklistId, string title, bool isRequired, int sortOrder, DateTimeOffset now) => new()
    {
        ChecklistId = checklistId,
        Title = NormalizeTitle(title),
        IsRequired = isRequired,
        SortOrder = sortOrder,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public void Rename(string title, DateTimeOffset now)
    {
        Title = NormalizeTitle(title);
        UpdatedAt = now;
    }

    public void SetRequired(bool required, DateTimeOffset now)
    {
        IsRequired = required;
        UpdatedAt = now;
    }

    public void SetSortOrder(int sortOrder, DateTimeOffset now)
    {
        SortOrder = sortOrder;
        UpdatedAt = now;
    }

    public void SetDone(bool done, Guid userId, DateTimeOffset now)
    {
        if (done == IsDone)
        {
            return;
        }

        IsDone = done;
        DoneAt = done ? now : null;
        DoneBy = done ? userId : null;
        UpdatedAt = now;
    }

    private static string NormalizeTitle(string title)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length is 0 or > MaxTitleLength || t.Any(char.IsControl))
        {
            throw new DomainException("task.checklist_item_title_invalid", $"Checklist item must be 1–{MaxTitleLength} characters.");
        }

        return t;
    }
}
