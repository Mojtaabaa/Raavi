using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public sealed record TaskTimeEntryDto(
    Guid Id, Guid TaskId, Guid UserId, string? UserName, string WorkDate, int DurationMinutes,
    string? Description, bool IsOpeningBalance, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    int Revision, bool CanEdit, bool CanDelete);

public sealed record TaskTimeByUserDto(Guid UserId, string? UserName, int DurationMinutes);

public sealed record TaskTimeSummaryDto(
    int TotalMinutes, int EntryMinutes, int OpeningBalanceMinutes,
    int? ActualSpentMinutes, int? OriginalEstimateMinutes, int? RemainingEstimateMinutes,
    /// <summary>True when <see cref="ActualSpentMinutes"/> equals the unfiltered sum of active entries (incl. opening balance).</summary>
    bool ActualSpentMatchesEntries,
    IReadOnlyList<TaskTimeByUserDto> ByUser, IReadOnlyList<TaskTimeEntryDto> Items,
    bool CanLog, bool CanManageOthers);

public sealed record TaskTimeWrite(
    Guid? UserId,
    DateOnly? WorkDate,
    int? DurationMinutes,
    string? Description);

/// <summary>
/// Time tracking for WorkTask (B5). ActualSpentMinutes is a denormalized sum of non-deleted entries
/// (including opening-balance rows seeded from legacy ActualSpentMinutes).
/// </summary>
public sealed class TaskTimeService(
    WikiDbContext db,
    TaskService tasks,
    IPrincipalDirectory directory,
    ICurrentUser current,
    IClock clock)
{
    public static readonly Error NotFound = new("task.time_not_found", "Time entry not found.", ErrorKind.NotFound);
    public static readonly Error UserInvalid = new("task.time_user_invalid", "The user is not a member of this workspace.", ErrorKind.Validation);

    public async Task<Result<TaskTimeSummaryDto>> ListAsync(Guid taskId, Guid? userId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var loaded = await tasks.LoadForCollaborationAsync(taskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskTimeSummaryDto>.Failure(loaded.Error!);
        }

        var (task, space) = loaded.Value;
        var q = db.TaskTimeEntries.AsNoTracking().Where(e => e.TaskId == taskId && e.DeletedAt == null);
        if (userId is { } uid)
        {
            q = q.Where(e => e.UserId == uid);
        }

        if (from is { } f)
        {
            q = q.Where(e => e.WorkDate >= f);
        }

        if (to is { } t)
        {
            q = q.Where(e => e.WorkDate <= t);
        }

        var rows = await q.OrderByDescending(e => e.WorkDate).ThenByDescending(e => e.CreatedAt).Take(500).ToListAsync(ct).ConfigureAwait(false);
        var canLog = await MayLogOwnAsync(task, space, ct).ConfigureAwait(false) || space.CanWrite(PermissionAction.Edit);
        var fullSum = await db.TaskTimeEntries.AsNoTracking()
            .Where(e => e.TaskId == taskId && e.DeletedAt == null)
            .SumAsync(e => (int?)e.DurationMinutes, ct).ConfigureAwait(false) ?? 0;
        var matches = (task.ActualSpentMinutes ?? 0) == fullSum;
        return Result<TaskTimeSummaryDto>.Success(await ToSummaryAsync(task, space, rows, matches, canLog, space.CanWrite(PermissionAction.Edit), ct).ConfigureAwait(false));
    }

    public async Task<Result<TaskTimeEntryDto>> CreateAsync(Guid taskId, TaskTimeWrite request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await tasks.LoadForCollaborationAsync(taskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskTimeEntryDto>.Failure(loaded.Error!);
        }

        var (taskMeta, space) = loaded.Value;
        var forUser = request.UserId ?? current.UserId;
        if (forUser == current.UserId)
        {
            if (!await MayLogOwnAsync(taskMeta, space, ct).ConfigureAwait(false) && !space.CanWrite(PermissionAction.Edit))
            {
                return Result<TaskTimeEntryDto>.Failure(WikiErrors.Forbidden);
            }
        }
        else if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<TaskTimeEntryDto>.Failure(WikiErrors.Forbidden);
        }
        else
        {
            var names = await directory.GetUserNamesAsync([forUser], ct).ConfigureAwait(false);
            if (!names.ContainsKey(forUser))
            {
                return Result<TaskTimeEntryDto>.Failure(UserInvalid);
            }
        }

        var task = await db.WorkTasks.FirstAsync(t => t.Id == taskId, ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        try
        {
            var entry = TaskTimeEntry.Log(taskId, forUser, request.WorkDate ?? today, request.DurationMinutes ?? 0, request.Description, current.UserId, now, today);
            db.TaskTimeEntries.Add(entry);
            await RecomputeActualAsync(task, now, ct).ConfigureAwait(false);
            db.Publish(WikiEventTypes.TaskTimeLogged, new WikiEvent(task.SpaceId, task.ContentId, current.UserId, $"{task.Title}: +{entry.DurationMinutes}m", null, task.Id), now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<TaskTimeEntryDto>.Success((await ToEntryDtosAsync(space, [entry], ct).ConfigureAwait(false))[0]);
        }
        catch (DomainException ex)
        {
            return Result<TaskTimeEntryDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<TaskTimeEntryDto>.Failure(ETags.PreconditionFailed);
        }
    }

    public async Task<Result<TaskTimeEntryDto>> UpdateAsync(Guid entryId, int expectedRevision, TaskTimeWrite request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var entry = await db.TaskTimeEntries.FirstOrDefaultAsync(e => e.Id == entryId && e.DeletedAt == null, ct).ConfigureAwait(false);
        if (entry is null)
        {
            return Result<TaskTimeEntryDto>.Failure(NotFound);
        }

        var loaded = await tasks.LoadForCollaborationAsync(entry.TaskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskTimeEntryDto>.Failure(NotFound);
        }

        var (_, space) = loaded.Value;
        if (!MayEditEntry(entry, space))
        {
            return Result<TaskTimeEntryDto>.Failure(WikiErrors.Forbidden);
        }

        if (entry.Revision != expectedRevision)
        {
            return Result<TaskTimeEntryDto>.Failure(ETags.PreconditionFailed);
        }

        var task = await db.WorkTasks.FirstAsync(t => t.Id == entry.TaskId, ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        try
        {
            entry.Update(
                request.WorkDate ?? entry.WorkDate,
                request.DurationMinutes ?? entry.DurationMinutes,
                request.Description is null ? entry.Description : request.Description,
                now,
                today);
            await RecomputeActualAsync(task, now, ct).ConfigureAwait(false);
            db.Publish(WikiEventTypes.TaskTimeUpdated, new WikiEvent(task.SpaceId, task.ContentId, current.UserId, $"{task.Title}: {entry.DurationMinutes}m", null, task.Id), now);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<TaskTimeEntryDto>.Success((await ToEntryDtosAsync(space, [entry], ct).ConfigureAwait(false))[0]);
        }
        catch (DomainException ex)
        {
            return Result<TaskTimeEntryDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<TaskTimeEntryDto>.Failure(ETags.PreconditionFailed);
        }
    }

    public async Task<Result> DeleteAsync(Guid entryId, CancellationToken ct)
    {
        var entry = await db.TaskTimeEntries.FirstOrDefaultAsync(e => e.Id == entryId && e.DeletedAt == null, ct).ConfigureAwait(false);
        if (entry is null)
        {
            return Result.Failure(NotFound);
        }

        var loaded = await tasks.LoadForCollaborationAsync(entry.TaskId, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(NotFound);
        }

        var (_, space) = loaded.Value;
        if (!MayEditEntry(entry, space))
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        var task = await db.WorkTasks.FirstAsync(t => t.Id == entry.TaskId, ct).ConfigureAwait(false);
        var now = clock.UtcNow;
        entry.SoftDelete(current.UserId, now);
        await RecomputeActualAsync(task, now, ct).ConfigureAwait(false);
        db.Publish(WikiEventTypes.TaskTimeDeleted, new WikiEvent(task.SpaceId, task.ContentId, current.UserId, task.Title, null, task.Id), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    private async Task RecomputeActualAsync(WorkTask task, DateTimeOffset now, CancellationToken ct)
    {
        // Exclude every locally tracked row from the DB sum (including soft-deleted ones not yet saved).
        var localAll = db.TaskTimeEntries.Local.Where(e => e.TaskId == task.Id).ToList();
        var localIds = localAll.Select(e => e.Id).ToHashSet();
        var dbSum = await db.TaskTimeEntries.AsNoTracking()
            .Where(e => e.TaskId == task.Id && e.DeletedAt == null && !localIds.Contains(e.Id))
            .SumAsync(e => (int?)e.DurationMinutes, ct).ConfigureAwait(false) ?? 0;
        var localSum = localAll.Where(e => e.DeletedAt == null).Sum(e => e.DurationMinutes);
        task.SetActualSpentMinutes(dbSum + localSum, now);
    }

    private bool MayEditEntry(TaskTimeEntry entry, SpaceRights space)
    {
        if (entry.IsOpeningBalance)
        {
            return space.CanWrite(PermissionAction.Edit);
        }

        if (space.CanWrite(PermissionAction.Edit))
        {
            return true;
        }

        return entry.UserId == current.UserId;
    }

    private async Task<bool> MayLogOwnAsync(WorkTask task, SpaceRights space, CancellationToken ct)
    {
        if (!space.CanView || space.Space.Status != SpaceStatus.Active)
        {
            return false;
        }

        if (space.CanWrite(PermissionAction.Edit))
        {
            return true;
        }

        var me = current.UserId;
        if (task.AssigneeId == me)
        {
            return true;
        }

        return await db.TaskAssignees.AsNoTracking().AnyAsync(
            a => a.TaskId == task.Id && a.UserId == me && a.RemovedAt == null
                && (a.Role == TaskAssigneeRoles.Responsible || a.Role == TaskAssigneeRoles.Contributor), ct).ConfigureAwait(false);
    }

    private async Task<TaskTimeSummaryDto> ToSummaryAsync(
        WorkTask task, SpaceRights space, IReadOnlyList<TaskTimeEntry> rows, bool matches, bool canLog, bool canManage, CancellationToken ct)
    {
        var dtos = await ToEntryDtosAsync(space, rows, ct).ConfigureAwait(false);
        var opening = rows.Where(r => r.IsOpeningBalance).Sum(r => r.DurationMinutes);
        var entry = rows.Where(r => !r.IsOpeningBalance).Sum(r => r.DurationMinutes);
        var byUser = rows.GroupBy(r => r.UserId)
            .Select(g => new { g.Key, Minutes = g.Sum(x => x.DurationMinutes) })
            .ToList();
        var names = byUser.Count == 0
            ? new Dictionary<Guid, string>()
            : await directory.GetUserNamesAsync(byUser.Select(b => b.Key).ToList(), ct).ConfigureAwait(false);
        return new TaskTimeSummaryDto(
            opening + entry,
            entry,
            opening,
            task.ActualSpentMinutes,
            task.OriginalEstimateMinutes,
            task.RemainingEstimateMinutes,
            matches,
            byUser.Select(b => new TaskTimeByUserDto(b.Key, names.GetValueOrDefault(b.Key), b.Minutes)).OrderByDescending(b => b.DurationMinutes).ToList(),
            dtos,
            canLog,
            canManage);
    }

    private async Task<IReadOnlyList<TaskTimeEntryDto>> ToEntryDtosAsync(SpaceRights space, IReadOnlyList<TaskTimeEntry> rows, CancellationToken ct)
    {
        var ids = rows.Select(r => r.UserId).Distinct().ToList();
        var names = ids.Count == 0 ? new Dictionary<Guid, string>() : await directory.GetUserNamesAsync(ids, ct).ConfigureAwait(false);
        return rows.Select(e =>
        {
            var can = MayEditEntry(e, space);
            return new TaskTimeEntryDto(
                e.Id, e.TaskId, e.UserId, names.GetValueOrDefault(e.UserId),
                e.WorkDate.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                e.DurationMinutes, e.Description, e.IsOpeningBalance, e.CreatedAt, e.UpdatedAt, e.Revision, can, can);
        }).ToList();
    }
}
