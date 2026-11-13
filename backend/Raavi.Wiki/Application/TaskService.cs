using System.Globalization;
using System.Text;
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

public sealed record TaskRightsDto(bool CanEdit, bool CanComplete, bool CanDelete);

public sealed record TaskAssigneeDto(Guid UserId, string? Name, string Role, bool IsPrimary, DateTimeOffset? AssignedAt = null, DateTimeOffset? RemovedAt = null, Guid? AssignedBy = null, string? AssignedByName = null);

public sealed record TaskDto(
    Guid Id, string SpaceKey, string SpaceName, Guid? ContentId, string Title, string? Notes, string Status, string Priority,
    Guid? AssigneeId, string? AssigneeName, IReadOnlyList<TaskAssigneeDto> Assignees,
    string? DueDate, string? StartDate, Guid? ParentTaskId, int? OriginalEstimateMinutes, int? RemainingEstimateMinutes,
    int? ActualSpentMinutes, byte? ProgressPercent, bool BlockCompleteWhenOpenChildren, bool BlockCompleteWhenMandatoryIncomplete,
    int ChildCount, int OpenChildCount, int? ChecklistProgressPercent,
    bool IsOverdue, DateTimeOffset? CompletedAt, DateTimeOffset? ArchivedAt, DateTimeOffset CreatedAt, string? CreatedByName,
    int Revision, TaskRightsDto Rights);

public sealed record TaskAssigneeWrite(Guid? UserId, string? Role, bool? IsPrimary);

public sealed record TaskRequest(
    string? Title,
    string? Notes,
    Guid? AssigneeId,
    DateOnly? DueDate,
    Guid? ContentId,
    string? Priority = null,
    DateOnly? StartDate = null,
    Guid? ParentTaskId = null,
    int? OriginalEstimateMinutes = null,
    int? RemainingEstimateMinutes = null,
    byte? ProgressPercent = null,
    bool? BlockCompleteWhenOpenChildren = null,
    bool? BlockCompleteWhenMandatoryIncomplete = null,
    IReadOnlyList<TaskAssigneeWrite>? Assignees = null);

public sealed record TaskExport(string FileName, byte[] Data);

/// <summary>Space tasks (ADR-026 + ADR-056): multi-assignee dual-read with AssigneeId, archive, outbox.</summary>
public sealed class TaskService(WikiDbContext db, SpaceService spaces, WikiAuthorizer authz, IPrincipalDirectory directory, ICurrentUser current, IClock clock)
{
    public const int MaxListed = 500;

    public static readonly Error NotFound = new("task.not_found", "Task not found.", ErrorKind.NotFound);
    public static readonly Error AssigneeInvalid = new("task.assignee_invalid", "The assignee is not a member of this workspace.", ErrorKind.Validation);
    public static readonly Error AssigneeNoAccess = new("task.assignee_no_access", "The assignee cannot view this space.", ErrorKind.Validation);
    public static readonly Error ContentInvalid = new("task.content_invalid", "The page must belong to the same space.", ErrorKind.Validation);
    public static readonly Error ParentInvalid = new("task.parent_invalid", "The parent task is not valid.", ErrorKind.Validation);
    public static readonly Error PriorityInvalid = new("task.priority_invalid", "Priority is not valid.", ErrorKind.Validation);
    public static readonly Error ProgressInvalid = new("task.progress_invalid", "Progress must be between 0 and 100.", ErrorKind.Validation);
    public static readonly Error OpenChildren = new("task.open_children", "Complete all open child tasks first, or turn off blocking.", ErrorKind.Conflict);
    public static readonly Error MultipleResponsible = new("task.multiple_responsible", "A task may have at most one Responsible.", ErrorKind.Validation);
    public static readonly Error AssigneeConflict = new("task.assignee_conflict", "Assignment could not be saved because of a concurrent change.", ErrorKind.Conflict);
    public static readonly Error MandatoryIncomplete = new("task.mandatory_incomplete", "Complete all required checklist items first, or turn off blocking.", ErrorKind.Conflict);
    public static readonly Error HasChildren = new("task.has_children", "Remove or reparent child tasks before deleting this task.", ErrorKind.Conflict);
    public static readonly Error ParentTooDeep = new("task.parent_too_deep", $"Task hierarchy cannot exceed {WorkTask.MaxParentDepth} levels.", ErrorKind.Validation);

    public async Task<Result<IReadOnlyList<TaskDto>>> ListAsync(string spaceKey, string? status, Guid? assigneeId, CancellationToken ct, bool includeArchived = false)
    {
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<IReadOnlyList<TaskDto>>.Failure(WikiErrors.SpaceNotFound);
        }

        var query = db.WorkTasks.AsNoTracking().Where(t => t.SpaceId == space.Space.Id);
        if (!includeArchived)
        {
            query = query.Where(t => t.ArchivedAt == null);
        }

        query = Filter(query, status);
        if (assigneeId is { } a)
        {
            var assignedIds = db.TaskAssignees.AsNoTracking()
                .Where(x => x.UserId == a && x.RemovedAt == null)
                .Select(x => x.TaskId);
            query = query.Where(t => t.AssigneeId == a || assignedIds.Contains(t.Id));
        }

        var rows = await Order(query).Take(MaxListed).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<TaskDto>>.Success(await ToDtosAsync(rows, new Dictionary<Guid, SpaceRights> { [space.Space.Id] = space }, ct).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<TaskDto>> MineAsync(string? status, CancellationToken ct)
    {
        var me = current.UserId;
        var viewable = (await authz.ViewableSpaceIdsAsync(ct).ConfigureAwait(false)).ToList();
        var assignedIds = db.TaskAssignees.AsNoTracking()
            .Where(x => x.UserId == me && x.RemovedAt == null)
            .Select(x => x.TaskId);
        var query = Filter(
            db.WorkTasks.AsNoTracking().Where(t =>
                t.ArchivedAt == null
                && viewable.Contains(t.SpaceId)
                && (t.AssigneeId == me || assignedIds.Contains(t.Id))),
            status);
        var rows = await Order(query).Take(MaxListed).ToListAsync(ct).ConfigureAwait(false);
        var rights = new Dictionary<Guid, SpaceRights>();
        foreach (var space in await db.Spaces.AsNoTracking().Where(s => rows.Select(r => r.SpaceId).Contains(s.Id)).ToListAsync(ct).ConfigureAwait(false))
        {
            rights[space.Id] = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        }

        return await ToDtosAsync(rows, rights, ct).ConfigureAwait(false);
    }

    public async Task<Result<TaskDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        return loaded.IsSuccess
            ? Result<TaskDto>.Success((await ToDtosAsync([loaded.Value.Task], new Dictionary<Guid, SpaceRights> { [loaded.Value.Space.Space.Id] = loaded.Value.Space }, ct).ConfigureAwait(false))[0])
            : Result<TaskDto>.Failure(loaded.Error!);
    }

    public async Task<Result<TaskDto>> CreateAsync(string spaceKey, TaskRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<TaskDto>.Failure(WikiErrors.SpaceNotFound);
        }

        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<TaskDto>.Failure(space.Space.Status == SpaceStatus.Archived ? WikiErrors.SpaceArchived : WikiErrors.Forbidden);
        }

        if (!TryParsePriority(request.Priority, out var priority))
        {
            return Result<TaskDto>.Failure(PriorityInvalid);
        }

        var check = await ValidateAsync(space, request, excludeTaskId: null, ct).ConfigureAwait(false);
        if (check is not null)
        {
            return Result<TaskDto>.Failure(check);
        }

        var now = clock.UtcNow;
        try
        {
            var task = WorkTask.Create(
                space.Space.Id, request.ContentId, request.Title ?? string.Empty, request.Notes, request.AssigneeId, request.DueDate,
                current.UserId, now, priority ?? WorkTaskPriority.Normal, request.StartDate, request.ParentTaskId, request.OriginalEstimateMinutes, request.RemainingEstimateMinutes,
                request.ProgressPercent, request.BlockCompleteWhenOpenChildren ?? true, request.BlockCompleteWhenMandatoryIncomplete ?? true);
            db.WorkTasks.Add(task);
            await ApplyAssigneesAsync(task, space, request, isCreate: true, now, ct).ConfigureAwait(false);
            await TaskCollaborationService.AutoWatchAsync(db, task.Id, current.UserId, now, ct).ConfigureAwait(false);
            db.Publish(WikiEventTypes.TaskCreated, new WikiEvent(space.Space.Id, task.ContentId, current.UserId, task.Title, null, task.Id), now);
            await SaveTaskChangesAsync(ct).ConfigureAwait(false);
            return Result<TaskDto>.Success((await ToDtosAsync([task], new Dictionary<Guid, SpaceRights> { [space.Space.Id] = space }, ct).ConfigureAwait(false))[0]);
        }
        catch (DomainException ex)
        {
            return Result<TaskDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<TaskDto>.Failure(ETags.PreconditionFailed);
        }
        catch (DbUpdateException)
        {
            return Result<TaskDto>.Failure(AssigneeConflict);
        }
    }

    public async Task<Result<TaskDto>> UpdateAsync(Guid id, int expectedRevision, TaskRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskDto>.Failure(loaded.Error!);
        }

        var (task, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<TaskDto>.Failure(WikiErrors.Forbidden);
        }

        if (task.Revision != expectedRevision)
        {
            return Result<TaskDto>.Failure(ETags.PreconditionFailed);
        }

        if (!TryParsePriority(request.Priority, out var priority))
        {
            return Result<TaskDto>.Failure(PriorityInvalid);
        }

        var check = await ValidateAsync(space, request, excludeTaskId: id, ct).ConfigureAwait(false);
        if (check is not null)
        {
            return Result<TaskDto>.Failure(check);
        }

        var now = clock.UtcNow;
        var prevOriginal = task.OriginalEstimateMinutes;
        var prevRemaining = task.RemainingEstimateMinutes;
        var prevProgress = task.ProgressPercent;
        try
        {
            task.Update(
                request.ContentId, request.Title ?? string.Empty, request.Notes, request.AssigneeId, request.DueDate, now,
                priority,
                request.StartDate, setStartDate: true,
                request.ParentTaskId, setParent: true,
                request.OriginalEstimateMinutes, setOriginalEstimate: true,
                request.RemainingEstimateMinutes, setRemainingEstimate: true,
                request.ProgressPercent, setProgress: true,
                request.BlockCompleteWhenOpenChildren,
                request.BlockCompleteWhenMandatoryIncomplete);
            await ApplyAssigneesAsync(task, space, request, isCreate: false, now, ct).ConfigureAwait(false);
            db.Publish(WikiEventTypes.TaskUpdated, new WikiEvent(space.Space.Id, task.ContentId, current.UserId, task.Title, null, task.Id), now);
            if (prevOriginal != task.OriginalEstimateMinutes)
            {
                db.Publish(WikiEventTypes.TaskEstimateOriginalChanged, new WikiEvent(space.Space.Id, task.ContentId, current.UserId, $"{task.Title}: {Fmt(prevOriginal)} → {Fmt(task.OriginalEstimateMinutes)}", null, task.Id), now);
            }

            if (prevRemaining != task.RemainingEstimateMinutes)
            {
                db.Publish(WikiEventTypes.TaskEstimateRemainingChanged, new WikiEvent(space.Space.Id, task.ContentId, current.UserId, $"{task.Title}: {Fmt(prevRemaining)} → {Fmt(task.RemainingEstimateMinutes)}", null, task.Id), now);
            }

            if (prevProgress != task.ProgressPercent)
            {
                db.Publish(WikiEventTypes.TaskProgressChanged, new WikiEvent(space.Space.Id, task.ContentId, current.UserId, $"{task.Title}: {Fmt(prevProgress)} → {Fmt(task.ProgressPercent)}", null, task.Id), now);
            }

            await SaveTaskChangesAsync(ct).ConfigureAwait(false);
            return Result<TaskDto>.Success((await ToDtosAsync([task], new Dictionary<Guid, SpaceRights> { [space.Space.Id] = space }, ct).ConfigureAwait(false))[0]);
        }
        catch (DomainException ex)
        {
            return Result<TaskDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Validation));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Result<TaskDto>.Failure(ETags.PreconditionFailed);
        }
        catch (DbUpdateException)
        {
            return Result<TaskDto>.Failure(AssigneeConflict);
        }
    }

    /// <summary>Active assignees, or full soft-removed history when <paramref name="includeRemoved"/> (Edit).</summary>
    public async Task<Result<IReadOnlyList<TaskAssigneeDto>>> ListAssigneesAsync(Guid id, bool includeRemoved, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<TaskAssigneeDto>>.Failure(loaded.Error!);
        }

        var (task, space) = loaded.Value;
        if (includeRemoved && !space.CanWrite(PermissionAction.Edit))
        {
            return Result<IReadOnlyList<TaskAssigneeDto>>.Failure(WikiErrors.Forbidden);
        }

        var q = db.TaskAssignees.AsNoTracking().Where(a => a.TaskId == task.Id);
        if (!includeRemoved)
        {
            q = q.Where(a => a.RemovedAt == null);
        }

        var rows = await q.OrderByDescending(a => a.AssignedAt).ToListAsync(ct).ConfigureAwait(false);
        if (rows.Count == 0 && !includeRemoved && task.AssigneeId is { } legacy)
        {
            var names = await directory.GetUserNamesAsync([legacy], ct).ConfigureAwait(false);
            return Result<IReadOnlyList<TaskAssigneeDto>>.Success([
                new TaskAssigneeDto(legacy, names.GetValueOrDefault(legacy), TaskAssigneeRoles.Responsible, true),
            ]);
        }

        var userIds = rows.Select(r => r.UserId).Concat(rows.Select(r => r.AssignedBy)).Distinct().ToList();
        var namesMap = await directory.GetUserNamesAsync(userIds, ct).ConfigureAwait(false);
        return Result<IReadOnlyList<TaskAssigneeDto>>.Success(rows.Select(r => new TaskAssigneeDto(
            r.UserId, namesMap.GetValueOrDefault(r.UserId), r.Role, r.IsPrimary, r.AssignedAt, r.RemovedAt, r.AssignedBy, namesMap.GetValueOrDefault(r.AssignedBy))).ToList());
    }

    public async Task<Result<TaskDto>> SetDoneAsync(Guid id, bool done, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskDto>.Failure(loaded.Error!);
        }

        var (task, space) = loaded.Value;
        if (!await MayCompleteAsync(task, space, ct).ConfigureAwait(false))
        {
            return Result<TaskDto>.Failure(WikiErrors.Forbidden);
        }

        if (done)
        {
            var block = await EvaluateCompleteBlocksAsync(task, ct).ConfigureAwait(false);
            if (block is not null)
            {
                return Result<TaskDto>.Failure(block);
            }
        }

        var now = clock.UtcNow;
        if (done)
        {
            task.Complete(current.UserId, now);
            db.Publish(WikiEventTypes.TaskCompleted, new WikiEvent(space.Space.Id, task.ContentId, current.UserId, task.Title, null, task.Id), now);
        }
        else
        {
            task.Reopen(now);
            db.Publish(WikiEventTypes.TaskReopened, new WikiEvent(space.Space.Id, task.ContentId, current.UserId, task.Title, null, task.Id), now);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TaskDto>.Success((await ToDtosAsync([task], new Dictionary<Guid, SpaceRights> { [space.Space.Id] = space }, ct).ConfigureAwait(false))[0]);
    }

    /// <summary>Used by checklist service — View, or Edit when <paramref name="requireEdit"/>.</summary>
    public async Task<Result<(WorkTask Task, SpaceRights Space)>> LoadForCollaborationAsync(Guid id, CancellationToken ct, bool requireEdit = false)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return loaded;
        }

        if (requireEdit && !loaded.Value.Space.CanWrite(PermissionAction.Edit))
        {
            return Result<(WorkTask, SpaceRights)>.Failure(WikiErrors.Forbidden);
        }

        return loaded;
    }

    public async Task<Result<IReadOnlyList<TaskDto>>> ListChildrenAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<TaskDto>>.Failure(loaded.Error!);
        }

        var (task, space) = loaded.Value;
        var rows = await Order(db.WorkTasks.AsNoTracking().Where(t => t.ParentTaskId == task.Id && t.ArchivedAt == null))
            .Take(MaxListed).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<TaskDto>>.Success(await ToDtosAsync(rows, new Dictionary<Guid, SpaceRights> { [space.Space.Id] = space }, ct).ConfigureAwait(false));
    }

    public async Task<Result<TaskDto>> PromoteAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskDto>.Failure(loaded.Error!);
        }

        var (task, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<TaskDto>.Failure(WikiErrors.Forbidden);
        }

        var now = clock.UtcNow;
        task.ClearParent(now);
        db.Publish(WikiEventTypes.TaskUpdated, new WikiEvent(space.Space.Id, task.ContentId, current.UserId, task.Title, null, task.Id), now);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TaskDto>.Success((await ToDtosAsync([task], new Dictionary<Guid, SpaceRights> { [space.Space.Id] = space }, ct).ConfigureAwait(false))[0]);
    }

    private async Task<Error?> EvaluateCompleteBlocksAsync(WorkTask task, CancellationToken ct)
    {
        if (task.BlockCompleteWhenOpenChildren
            && await db.WorkTasks.AsNoTracking().AnyAsync(
                c => c.ParentTaskId == task.Id && c.ArchivedAt == null && c.Status == WorkTaskStatus.Open, ct).ConfigureAwait(false))
        {
            return OpenChildren;
        }

        if (task.BlockCompleteWhenMandatoryIncomplete)
        {
            var checklistIds = db.TaskChecklists.AsNoTracking().Where(c => c.TaskId == task.Id).Select(c => c.Id);
            if (await db.TaskChecklistItems.AsNoTracking().AnyAsync(
                    i => checklistIds.Contains(i.ChecklistId) && i.IsRequired && !i.IsDone, ct).ConfigureAwait(false))
            {
                return MandatoryIncomplete;
            }
        }

        return null;
    }

    public async Task<Result<TaskDto>> ArchiveAsync(Guid id, bool restore, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskDto>.Failure(loaded.Error!);
        }

        var (task, space) = loaded.Value;
        if (!space.CanWrite(PermissionAction.Edit))
        {
            return Result<TaskDto>.Failure(WikiErrors.Forbidden);
        }

        var now = clock.UtcNow;
        if (restore)
        {
            task.Restore(now);
        }
        else
        {
            task.Archive(now);
            db.Publish(WikiEventTypes.TaskArchived, new WikiEvent(space.Space.Id, task.ContentId, current.UserId, task.Title, null, task.Id), now);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TaskDto>.Success((await ToDtosAsync([task], new Dictionary<Guid, SpaceRights> { [space.Space.Id] = space }, ct).ConfigureAwait(false))[0]);
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        if (!loaded.Value.Space.CanWrite(PermissionAction.Edit))
        {
            return Result.Failure(WikiErrors.Forbidden);
        }

        var (task, space) = loaded.Value;
        if (await db.WorkTasks.AsNoTracking().AnyAsync(c => c.ParentTaskId == task.Id, ct).ConfigureAwait(false))
        {
            return Result.Failure(HasChildren);
        }

        db.Publish(WikiEventTypes.TaskUpdated, new WikiEvent(space.Space.Id, task.ContentId, current.UserId, task.Title, null, task.Id), clock.UtcNow);
        db.WorkTasks.Remove(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<TaskExport>> ExportCsvAsync(string spaceKey, CancellationToken ct)
    {
        var list = await ListAsync(spaceKey, null, null, ct).ConfigureAwait(false);
        if (!list.IsSuccess)
        {
            return Result<TaskExport>.Failure(list.Error!);
        }

        var sb = new StringBuilder("﻿");
        sb.Append("Title,Status,Priority,Assignee,Due date,Start date,Estimate (min),Completed at,Notes\r\n");
        foreach (var t in list.Value)
        {
            sb.Append(string.Join(',', new[]
            {
                t.Title, t.Status, t.Priority, t.AssigneeName ?? string.Empty, t.DueDate ?? string.Empty, t.StartDate ?? string.Empty,
                t.OriginalEstimateMinutes?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
                t.CompletedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty, t.Notes ?? string.Empty,
            }.Select(Csv))).Append("\r\n");
        }

        return Result<TaskExport>.Success(new TaskExport($"tasks-{Raavi.Wiki.Domain.Space.NormalizeKey(spaceKey)}.csv", Encoding.UTF8.GetBytes(sb.ToString())));
    }

    public static string Csv(string value)
    {
        var v = value ?? string.Empty;
        if (v.Length > 0 && v[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            v = "'" + v;
        }

        return v.Contains('"', StringComparison.Ordinal) || v.Contains(',', StringComparison.Ordinal) || v.Contains('\n', StringComparison.Ordinal) || v.Contains('\r', StringComparison.Ordinal)
            ? "\"" + v.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : v;
    }

    private async Task SaveTaskChangesAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException)
        {
            throw; // revision / ETag path maps separately when caught higher if needed
        }
    }

    private async Task ApplyAssigneesAsync(WorkTask task, SpaceRights space, TaskRequest request, bool isCreate, DateTimeOffset now, CancellationToken ct)
    {
        var existing = await db.TaskAssignees.Where(a => a.TaskId == task.Id && a.RemovedAt == null).ToListAsync(ct).ConfigureAwait(false);

        // Explicit assignees list wins; otherwise keep dual-write from AssigneeId.
        if (request.Assignees is not null)
        {
            var desired = NormalizeDesiredAssignees(request.Assignees);
            foreach (var d in desired)
            {
                var err = await EnsureCanAssignAsync(space, d.UserId, ct).ConfigureAwait(false);
                if (err is not null)
                {
                    throw new DomainException(err.Code, err.Message);
                }
            }

            foreach (var row in existing.Where(e => e.RemovedAt is null).ToList())
            {
                if (!desired.Any(d => d.UserId == row.UserId))
                {
                    SoftUnassign(row, space, task, now);
                }
            }

            foreach (var d in desired)
            {
                var hit = existing.FirstOrDefault(e => e.UserId == d.UserId && e.RemovedAt is null);
                if (hit is null)
                {
                    AddAssignee(task, space, d.UserId, d.Role, d.Primary, now);
                }
                else if (!string.Equals(hit.Role, d.Role, StringComparison.Ordinal))
                {
                    // Role change: soft-remove + insert so history keeps the previous role.
                    SoftUnassign(hit, space, task, now);
                    AddAssignee(task, space, d.UserId, d.Role, d.Primary, now);
                }
                else if (hit.IsPrimary != d.Primary)
                {
                    hit.SetPrimary(d.Primary);
                }
            }

            var primaryId = desired.FirstOrDefault(d => d.Role == TaskAssigneeRoles.Responsible).UserId;
            task.SetPrimaryAssignee(primaryId == Guid.Empty ? null : primaryId, now);
            return;
        }

        // Legacy single AssigneeId path (does not clear Contributors / other roles).
        if (request.AssigneeId is { } assignee)
        {
            var err = await EnsureCanAssignAsync(space, assignee, ct).ConfigureAwait(false);
            if (err is not null)
            {
                throw new DomainException(err.Code, err.Message);
            }
        }

        foreach (var row in existing.Where(e => e.Role == TaskAssigneeRoles.Responsible && e.RemovedAt is null).ToList())
        {
            if (request.AssigneeId is null || row.UserId != request.AssigneeId)
            {
                SoftUnassign(row, space, task, now, publish: !isCreate);
            }
        }

        if (request.AssigneeId is { } aid)
        {
            var active = existing.FirstOrDefault(e => e.UserId == aid && e.RemovedAt is null);
            if (active is null)
            {
                AddAssignee(task, space, aid, TaskAssigneeRoles.Responsible, primary: true, now);
            }
            else if (!string.Equals(active.Role, TaskAssigneeRoles.Responsible, StringComparison.Ordinal))
            {
                SoftUnassign(active, space, task, now, publish: !isCreate);
                AddAssignee(task, space, aid, TaskAssigneeRoles.Responsible, primary: true, now);
            }
            else
            {
                active.SetPrimary(true);
            }
        }

        task.SetPrimaryAssignee(request.AssigneeId, now);
    }

    private static List<(Guid UserId, string Role, bool Primary)> NormalizeDesiredAssignees(IReadOnlyList<TaskAssigneeWrite> rows)
    {
        var byUser = new Dictionary<Guid, string>();
        foreach (var row in rows)
        {
            if (row.UserId is not { } uid)
            {
                continue;
            }

            byUser[uid] = TaskAssigneeRoles.Normalize(row.Role ?? TaskAssigneeRoles.Responsible);
        }

        var desired = byUser.Select(kv => (UserId: kv.Key, Role: kv.Value, Primary: false)).ToList();
        if (desired.Count(d => d.Role == TaskAssigneeRoles.Responsible) > 1)
        {
            throw new DomainException(MultipleResponsible.Code, MultipleResponsible.Message);
        }

        for (var i = 0; i < desired.Count; i++)
        {
            var d = desired[i];
            desired[i] = (d.UserId, d.Role, d.Role == TaskAssigneeRoles.Responsible);
        }

        return desired;
    }

    private void SoftUnassign(TaskAssignee row, SpaceRights space, WorkTask task, DateTimeOffset now, bool publish = true)
    {
        row.Remove(now);
        if (publish)
        {
            db.Publish(WikiEventTypes.TaskUnassigned, new WikiEvent(space.Space.Id, task.ContentId, current.UserId, $"{task.Title} · {row.Role}", null, task.Id), now);
        }
    }

    private void AddAssignee(WorkTask task, SpaceRights space, Guid userId, string role, bool primary, DateTimeOffset now)
    {
        db.TaskAssignees.Add(TaskAssignee.Assign(task.Id, userId, role, primary, current.UserId, now));
        db.Publish(WikiEventTypes.TaskAssigned, new WikiEvent(space.Space.Id, task.ContentId, current.UserId, $"{task.Title} · {role}", null, task.Id), now);
    }

    private async Task<Error?> EnsureCanAssignAsync(SpaceRights space, Guid userId, CancellationToken ct)
    {
        var principal = await directory.GetPrincipalAsync(userId, fresh: false, ct).ConfigureAwait(false);
        if (!principal.IsActiveMember)
        {
            return AssigneeInvalid;
        }

        if (!(await authz.GetSpaceRightsAsync(space.Space, principal, ct).ConfigureAwait(false)).CanView)
        {
            return AssigneeNoAccess;
        }

        return null;
    }

    private static IQueryable<WorkTask> Filter(IQueryable<WorkTask> q, string? status) =>
        Enum.TryParse<WorkTaskStatus>(status, true, out var s) ? q.Where(t => t.Status == s) : q;

    private static IOrderedQueryable<WorkTask> Order(IQueryable<WorkTask> q) =>
        q.OrderBy(t => t.Status).ThenByDescending(t => t.Priority).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.CreatedAt);

    private static string Fmt(int? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "—";

    private static string Fmt(byte? n) => n?.ToString(CultureInfo.InvariantCulture) ?? "—";

    private async Task<bool> MayCompleteAsync(WorkTask task, SpaceRights space, CancellationToken ct)
    {
        if (space.Space.Status != SpaceStatus.Active || !space.CanView)
        {
            return false;
        }

        if (space.Can(PermissionAction.Edit) || task.AssigneeId == current.UserId)
        {
            return true;
        }

        return await db.TaskAssignees.AsNoTracking().AnyAsync(
            a => a.TaskId == task.Id && a.UserId == current.UserId && a.RemovedAt == null
                && (a.Role == TaskAssigneeRoles.Responsible || a.Role == TaskAssigneeRoles.Contributor), ct).ConfigureAwait(false);
    }

    private async Task<Result<(WorkTask Task, SpaceRights Space)>> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var task = await (track ? db.WorkTasks : db.WorkTasks.AsNoTracking()).FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
        var space = task is null ? null : await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == task.SpaceId, ct).ConfigureAwait(false);
        if (task is null || space is null)
        {
            return Result<(WorkTask, SpaceRights)>.Failure(NotFound);
        }

        var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        return rights.CanView ? Result<(WorkTask, SpaceRights)>.Success((task, rights)) : Result<(WorkTask, SpaceRights)>.Failure(NotFound);
    }

    private async Task<Error?> ValidateAsync(SpaceRights space, TaskRequest request, Guid? excludeTaskId, CancellationToken ct)
    {
        if (request.AssigneeId is { } assignee)
        {
            var err = await EnsureCanAssignAsync(space, assignee, ct).ConfigureAwait(false);
            if (err is not null)
            {
                return err;
            }
        }

        if (request.Assignees is { } list)
        {
            try
            {
                _ = NormalizeDesiredAssignees(list);
            }
            catch (DomainException ex)
            {
                return new Error(ex.Code, ex.Message, ErrorKind.Validation);
            }

            foreach (var row in list.Where(r => r.UserId is not null))
            {
                var err = await EnsureCanAssignAsync(space, row.UserId!.Value, ct).ConfigureAwait(false);
                if (err is not null)
                {
                    return err;
                }
            }
        }

        if (request.ProgressPercent is > 100)
        {
            return ProgressInvalid;
        }

        if (request.ContentId is { } content && !await db.Nodes.AnyAsync(n => n.Id == content && n.SpaceId == space.Space.Id && n.Status != ContentStatus.Trashed, ct).ConfigureAwait(false))
        {
            return ContentInvalid;
        }

        if (request.ParentTaskId is { } parent)
        {
            if (excludeTaskId is { } self && parent == self)
            {
                return ParentInvalid;
            }

            var p = await db.WorkTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == parent && t.SpaceId == space.Space.Id && t.ArchivedAt == null, ct).ConfigureAwait(false);
            if (p is null)
            {
                return ParentInvalid;
            }

            // Depth of parent + 1 for the new child; cycle: parent chain must not include self.
            var depth = 1;
            var walk = p.ParentTaskId;
            var guard = 0;
            while (walk is { } w && guard++ < WorkTask.MaxParentDepth + 2)
            {
                if (excludeTaskId is { } id && w == id)
                {
                    return ParentInvalid;
                }

                depth++;
                if (depth >= WorkTask.MaxParentDepth)
                {
                    return ParentTooDeep;
                }

                walk = await db.WorkTasks.AsNoTracking().Where(t => t.Id == w).Select(t => t.ParentTaskId).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static bool TryParsePriority(string? raw, out WorkTaskPriority? priority)
    {
        priority = null;
        if (string.IsNullOrWhiteSpace(raw))
        {
            return true;
        }

        if (!Enum.TryParse<WorkTaskPriority>(raw, ignoreCase: true, out var parsed))
        {
            return false;
        }

        priority = parsed;
        return true;
    }

    private async Task<IReadOnlyList<TaskDto>> ToDtosAsync(IReadOnlyList<WorkTask> tasks, Dictionary<Guid, SpaceRights> rights, CancellationToken ct)
    {
        var list = tasks as List<WorkTask> ?? tasks.ToList();
        var ids = list.ConvertAll(t => t.Id);
        var assigneeRows = await db.TaskAssignees.AsNoTracking()
            .Where(a => ids.Contains(a.TaskId) && a.RemovedAt == null)
            .ToListAsync(ct).ConfigureAwait(false);
        var userIds = list.Select(t => t.AssigneeId)
            .Concat(list.Select(t => (Guid?)t.CreatedBy))
            .Concat(assigneeRows.Select(a => (Guid?)a.UserId))
            .OfType<Guid>().Distinct().ToList();
        var names = await directory.GetUserNamesAsync(userIds, ct).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var me = current.UserId;
        var byTask = assigneeRows.GroupBy(a => a.TaskId).ToDictionary(g => g.Key, g => g.ToList());
        var childCounts = await db.WorkTasks.AsNoTracking()
            .Where(c => c.ParentTaskId != null && ids.Contains(c.ParentTaskId.Value) && c.ArchivedAt == null)
            .GroupBy(c => c.ParentTaskId!.Value)
            .Select(g => new { ParentId = g.Key, Total = g.Count(), Open = g.Count(x => x.Status == WorkTaskStatus.Open) })
            .ToListAsync(ct).ConfigureAwait(false);
        var childMap = childCounts.ToDictionary(x => x.ParentId, x => (x.Total, x.Open));

        var checklistIds = await db.TaskChecklists.AsNoTracking().Where(c => ids.Contains(c.TaskId))
            .Select(c => new { c.Id, c.TaskId }).ToListAsync(ct).ConfigureAwait(false);
        var checklistIdList = checklistIds.ConvertAll(c => c.Id);
        var checklistItems = checklistIdList.Count == 0
            ? []
            : await db.TaskChecklistItems.AsNoTracking().Where(i => checklistIdList.Contains(i.ChecklistId))
                .Select(i => new { i.ChecklistId, i.IsDone }).ToListAsync(ct).ConfigureAwait(false);
        var progressByTask = checklistIds.GroupBy(c => c.TaskId).ToDictionary(
            g => g.Key,
            g =>
            {
                var itemRows = checklistItems.Where(i => g.Any(c => c.Id == i.ChecklistId)).ToList();
                if (itemRows.Count == 0)
                {
                    return (int?)null;
                }

                return (int)Math.Round(100.0 * itemRows.Count(i => i.IsDone) / itemRows.Count);
            });

        var result = new List<TaskDto>(list.Count);
        foreach (var t in list)
        {
            var r = rights[t.SpaceId];
            var rows = byTask.GetValueOrDefault(t.Id) ?? [];
            var assigneeDtos = rows.Select(x => new TaskAssigneeDto(x.UserId, names.GetValueOrDefault(x.UserId), x.Role, x.IsPrimary, x.AssignedAt, null, x.AssignedBy, names.GetValueOrDefault(x.AssignedBy))).ToList();
            if (assigneeDtos.Count == 0 && t.AssigneeId is { } legacy)
            {
                assigneeDtos.Add(new TaskAssigneeDto(legacy, names.GetValueOrDefault(legacy), TaskAssigneeRoles.Responsible, true));
            }

            var canComplete = r.Space.Status == SpaceStatus.Active && r.CanView
                && (r.Can(PermissionAction.Edit) || t.AssigneeId == me
                    || rows.Any(a => a.UserId == me && a.Role is TaskAssigneeRoles.Responsible or TaskAssigneeRoles.Contributor)
                    || assigneeDtos.Any(a => a.UserId == me && a.Role is TaskAssigneeRoles.Responsible or TaskAssigneeRoles.Contributor));
            var children = childMap.GetValueOrDefault(t.Id);

            result.Add(new TaskDto(
                t.Id, r.Space.Key, r.Space.Name, t.ContentId, t.Title, t.Notes, t.Status.ToString(), t.Priority.ToString(),
                t.AssigneeId, t.AssigneeId is { } a ? names.GetValueOrDefault(a) : null,
                assigneeDtos,
                t.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                t.StartDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                t.ParentTaskId, t.OriginalEstimateMinutes, t.RemainingEstimateMinutes,
                t.ActualSpentMinutes, t.ProgressPercent, t.BlockCompleteWhenOpenChildren, t.BlockCompleteWhenMandatoryIncomplete,
                children.Total, children.Open, progressByTask.GetValueOrDefault(t.Id),
                t.Status == WorkTaskStatus.Open && t.DueDate is { } d && d < today,
                t.CompletedAt, t.ArchivedAt, t.CreatedAt, names.GetValueOrDefault(t.CreatedBy), t.Revision,
                new TaskRightsDto(r.CanWrite(PermissionAction.Edit), canComplete, r.CanWrite(PermissionAction.Edit))));
        }

        return result;
    }
}
