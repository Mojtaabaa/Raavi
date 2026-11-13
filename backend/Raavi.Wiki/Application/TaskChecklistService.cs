using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;

namespace Raavi.Wiki.Application;

public sealed record TaskChecklistItemDto(
    Guid Id, string Title, bool IsDone, bool IsRequired, int SortOrder,
    DateTimeOffset? DoneAt, Guid? DoneBy, string? DoneByName);

public sealed record TaskChecklistDto(Guid Id, string Title, int SortOrder, IReadOnlyList<TaskChecklistItemDto> Items);

public sealed record TaskChecklistWrite(string? Title, int? SortOrder);

public sealed record TaskChecklistItemWrite(string? Title, bool? IsRequired, int? SortOrder, bool? IsDone);

/// <summary>Task-scoped checklists (B3). TipTap editor task lists are separate.</summary>
public sealed class TaskChecklistService(WikiDbContext db, TaskService tasks, IPrincipalDirectory directory, ICurrentUser current, IClock clock)
{
    public static readonly Error ChecklistNotFound = new("task.checklist_not_found", "Checklist not found.", ErrorKind.NotFound);
    public static readonly Error ItemNotFound = new("task.checklist_item_not_found", "Checklist item not found.", ErrorKind.NotFound);

    public async Task<Result<IReadOnlyList<TaskChecklistDto>>> ListAsync(Guid taskId, CancellationToken ct)
    {
        var loaded = await tasks.LoadForCollaborationAsync(taskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<TaskChecklistDto>>.Failure(loaded.Error!);
        }

        return Result<IReadOnlyList<TaskChecklistDto>>.Success(await ToDtosAsync(taskId, ct).ConfigureAwait(false));
    }

    public async Task<Result<TaskChecklistDto>> CreateChecklistAsync(Guid taskId, TaskChecklistWrite body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var loaded = await tasks.LoadForCollaborationAsync(taskId, ct, requireEdit: true).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskChecklistDto>.Failure(loaded.Error!);
        }

        var now = clock.UtcNow;
        var order = body.SortOrder ?? await db.TaskChecklists.CountAsync(c => c.TaskId == taskId, ct).ConfigureAwait(false);
        try
        {
            var row = TaskChecklist.Create(taskId, body.Title ?? string.Empty, order, current.UserId, now);
            db.TaskChecklists.Add(row);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<TaskChecklistDto>.Success((await ToDtosAsync(taskId, ct).ConfigureAwait(false)).First(c => c.Id == row.Id));
        }
        catch (DomainException ex)
        {
            return Result<TaskChecklistDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    public async Task<Result<TaskChecklistDto>> UpdateChecklistAsync(Guid checklistId, TaskChecklistWrite body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var row = await db.TaskChecklists.FirstOrDefaultAsync(c => c.Id == checklistId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result<TaskChecklistDto>.Failure(ChecklistNotFound);
        }

        var loaded = await tasks.LoadForCollaborationAsync(row.TaskId, ct, requireEdit: true).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskChecklistDto>.Failure(loaded.Error!);
        }

        try
        {
            if (body.Title is not null)
            {
                row.Rename(body.Title);
            }

            if (body.SortOrder is { } o)
            {
                row.SetSortOrder(o);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<TaskChecklistDto>.Success((await ToDtosAsync(row.TaskId, ct).ConfigureAwait(false)).First(c => c.Id == row.Id));
        }
        catch (DomainException ex)
        {
            return Result<TaskChecklistDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    public async Task<Result> DeleteChecklistAsync(Guid checklistId, CancellationToken ct)
    {
        var row = await db.TaskChecklists.FirstOrDefaultAsync(c => c.Id == checklistId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(ChecklistNotFound);
        }

        var loaded = await tasks.LoadForCollaborationAsync(row.TaskId, ct, requireEdit: true).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        db.TaskChecklists.Remove(row);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<TaskChecklistItemDto>> AddItemAsync(Guid checklistId, TaskChecklistItemWrite body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var list = await db.TaskChecklists.FirstOrDefaultAsync(c => c.Id == checklistId, ct).ConfigureAwait(false);
        if (list is null)
        {
            return Result<TaskChecklistItemDto>.Failure(ChecklistNotFound);
        }

        var loaded = await tasks.LoadForCollaborationAsync(list.TaskId, ct, requireEdit: true).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskChecklistItemDto>.Failure(loaded.Error!);
        }

        var now = clock.UtcNow;
        var order = body.SortOrder ?? await db.TaskChecklistItems.CountAsync(i => i.ChecklistId == checklistId, ct).ConfigureAwait(false);
        try
        {
            var item = TaskChecklistItem.Create(checklistId, body.Title ?? string.Empty, body.IsRequired == true, order, now);
            if (body.IsDone == true)
            {
                item.SetDone(true, current.UserId, now);
            }

            db.TaskChecklistItems.Add(item);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var names = await directory.GetUserNamesAsync(item.DoneBy is { } d ? [d] : [], ct).ConfigureAwait(false);
            return Result<TaskChecklistItemDto>.Success(new TaskChecklistItemDto(
                item.Id, item.Title, item.IsDone, item.IsRequired, item.SortOrder, item.DoneAt, item.DoneBy,
                item.DoneBy is { } dbId ? names.GetValueOrDefault(dbId) : null));
        }
        catch (DomainException ex)
        {
            return Result<TaskChecklistItemDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    public async Task<Result<TaskChecklistItemDto>> UpdateItemAsync(Guid itemId, TaskChecklistItemWrite body, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(body);
        var item = await db.TaskChecklistItems.FirstOrDefaultAsync(i => i.Id == itemId, ct).ConfigureAwait(false);
        if (item is null)
        {
            return Result<TaskChecklistItemDto>.Failure(ItemNotFound);
        }

        var list = await db.TaskChecklists.AsNoTracking().FirstAsync(c => c.Id == item.ChecklistId, ct).ConfigureAwait(false);
        var loaded = await tasks.LoadForCollaborationAsync(list.TaskId, ct, requireEdit: true).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskChecklistItemDto>.Failure(loaded.Error!);
        }

        var now = clock.UtcNow;
        try
        {
            if (body.Title is not null)
            {
                item.Rename(body.Title, now);
            }

            if (body.IsRequired is { } req)
            {
                item.SetRequired(req, now);
            }

            if (body.SortOrder is { } o)
            {
                item.SetSortOrder(o, now);
            }

            if (body.IsDone is { } done)
            {
                item.SetDone(done, current.UserId, now);
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            var names = await directory.GetUserNamesAsync(item.DoneBy is { } d ? [d] : [], ct).ConfigureAwait(false);
            return Result<TaskChecklistItemDto>.Success(new TaskChecklistItemDto(
                item.Id, item.Title, item.IsDone, item.IsRequired, item.SortOrder, item.DoneAt, item.DoneBy,
                item.DoneBy is { } dbId ? names.GetValueOrDefault(dbId) : null));
        }
        catch (DomainException ex)
        {
            return Result<TaskChecklistItemDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
    }

    public async Task<Result> DeleteItemAsync(Guid itemId, CancellationToken ct)
    {
        var item = await db.TaskChecklistItems.FirstOrDefaultAsync(i => i.Id == itemId, ct).ConfigureAwait(false);
        if (item is null)
        {
            return Result.Failure(ItemNotFound);
        }

        var list = await db.TaskChecklists.AsNoTracking().FirstAsync(c => c.Id == item.ChecklistId, ct).ConfigureAwait(false);
        var loaded = await tasks.LoadForCollaborationAsync(list.TaskId, ct, requireEdit: true).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        db.TaskChecklistItems.Remove(item);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> ReorderItemsAsync(Guid checklistId, IReadOnlyList<Guid> orderedIds, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(orderedIds);
        var list = await db.TaskChecklists.FirstOrDefaultAsync(c => c.Id == checklistId, ct).ConfigureAwait(false);
        if (list is null)
        {
            return Result.Failure(ChecklistNotFound);
        }

        var loaded = await tasks.LoadForCollaborationAsync(list.TaskId, ct, requireEdit: true).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        var items = await db.TaskChecklistItems.Where(i => i.ChecklistId == checklistId).ToListAsync(ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        for (var i = 0; i < orderedIds.Count; i++)
        {
            var hit = items.FirstOrDefault(x => x.Id == orderedIds[i]);
            hit?.SetSortOrder(i, now);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task<IReadOnlyList<TaskChecklistDto>> ToDtosAsync(Guid taskId, CancellationToken ct)
    {
        var lists = await db.TaskChecklists.AsNoTracking().Where(c => c.TaskId == taskId).OrderBy(c => c.SortOrder).ThenBy(c => c.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        var ids = lists.ConvertAll(c => c.Id);
        var items = await db.TaskChecklistItems.AsNoTracking().Where(i => ids.Contains(i.ChecklistId)).OrderBy(i => i.SortOrder).ThenBy(i => i.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        var userIds = items.Select(i => i.DoneBy).OfType<Guid>().Distinct().ToList();
        var names = await directory.GetUserNamesAsync(userIds, ct).ConfigureAwait(false);
        return lists.Select(c => new TaskChecklistDto(
            c.Id, c.Title, c.SortOrder,
            items.Where(i => i.ChecklistId == c.Id).Select(i => new TaskChecklistItemDto(
                i.Id, i.Title, i.IsDone, i.IsRequired, i.SortOrder, i.DoneAt, i.DoneBy,
                i.DoneBy is { } d ? names.GetValueOrDefault(d) : null)).ToList())).ToList();
    }
}
