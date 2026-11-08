using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public sealed record TaskRightsDto(bool CanEdit, bool CanComplete, bool CanDelete);

public sealed record TaskDto(
    Guid Id, string SpaceKey, string SpaceName, Guid? ContentId, string Title, string? Notes, string Status, Guid? AssigneeId, string? AssigneeName,
    string? DueDate, bool IsOverdue, DateTimeOffset? CompletedAt, DateTimeOffset CreatedAt, string? CreatedByName, int Revision, TaskRightsDto Rights);

public sealed record TaskRequest(string? Title, string? Notes, Guid? AssigneeId, DateOnly? DueDate, Guid? ContentId);

public sealed record TaskExport(string FileName, byte[] Data);

/// <summary>Space tasks (Phase 9a, ADR-026): permissions from the space, assignees must be able to view it, "My tasks" across spaces, CSV export.</summary>
public sealed class TaskService(WikiDbContext db, SpaceService spaces, WikiAuthorizer authz, IPrincipalDirectory directory, ICurrentUser current, IClock clock)
{
    public const int MaxListed = 500;

    public static readonly Error NotFound = new("task.not_found", "Task not found.", ErrorKind.NotFound);
    public static readonly Error AssigneeInvalid = new("task.assignee_invalid", "The assignee is not a member of this workspace.", ErrorKind.Validation);
    public static readonly Error AssigneeNoAccess = new("task.assignee_no_access", "The assignee cannot view this space.", ErrorKind.Validation);
    public static readonly Error ContentInvalid = new("task.content_invalid", "The page must belong to the same space.", ErrorKind.Validation);

    public async Task<Result<IReadOnlyList<TaskDto>>> ListAsync(string spaceKey, string? status, Guid? assigneeId, CancellationToken ct)
    {
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<IReadOnlyList<TaskDto>>.Failure(WikiErrors.SpaceNotFound);
        }

        var query = db.WorkTasks.AsNoTracking().Where(t => t.SpaceId == space.Space.Id);
        query = Filter(query, status);
        if (assigneeId is { } a)
        {
            query = query.Where(t => t.AssigneeId == a);
        }

        var rows = await Order(query).Take(MaxListed).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<TaskDto>>.Success(await ToDtosAsync(rows, new Dictionary<Guid, SpaceRights> { [space.Space.Id] = space }, ct).ConfigureAwait(false));
    }

    /// <summary>Tasks assigned to the caller in every space the caller may still view.</summary>
    public async Task<IReadOnlyList<TaskDto>> MineAsync(string? status, CancellationToken ct)
    {
        var me = current.UserId;
        var viewable = (await authz.ViewableSpaceIdsAsync(ct).ConfigureAwait(false)).ToList();
        var query = Filter(db.WorkTasks.AsNoTracking().Where(t => t.AssigneeId == me && viewable.Contains(t.SpaceId)), status);
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
        return loaded.IsSuccess ? Result<TaskDto>.Success((await ToDtosAsync([loaded.Value.Task], new Dictionary<Guid, SpaceRights> { [loaded.Value.Space.Space.Id] = loaded.Value.Space }, ct).ConfigureAwait(false))[0]) : Result<TaskDto>.Failure(loaded.Error!);
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

        var check = await ValidateAsync(space, request, ct).ConfigureAwait(false);
        if (check is not null)
        {
            return Result<TaskDto>.Failure(check);
        }

        var task = WorkTask.Create(space.Space.Id, request.ContentId, request.Title ?? string.Empty, request.Notes, request.AssigneeId, request.DueDate, current.UserId, clock.UtcNow);
        db.WorkTasks.Add(task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TaskDto>.Success((await ToDtosAsync([task], new Dictionary<Guid, SpaceRights> { [space.Space.Id] = space }, ct).ConfigureAwait(false))[0]);
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

        var check = await ValidateAsync(space, request, ct).ConfigureAwait(false);
        if (check is not null)
        {
            return Result<TaskDto>.Failure(check);
        }

        task.Update(request.ContentId, request.Title ?? string.Empty, request.Notes, request.AssigneeId, request.DueDate, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TaskDto>.Success((await ToDtosAsync([task], new Dictionary<Guid, SpaceRights> { [space.Space.Id] = space }, ct).ConfigureAwait(false))[0]);
    }

    public async Task<Result<TaskDto>> SetDoneAsync(Guid id, bool done, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<TaskDto>.Failure(loaded.Error!);
        }

        var (task, space) = loaded.Value;
        if (!MayComplete(task, space))
        {
            return Result<TaskDto>.Failure(WikiErrors.Forbidden);
        }

        if (done)
        {
            task.Complete(current.UserId, clock.UtcNow);
        }
        else
        {
            task.Reopen(clock.UtcNow);
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

        db.WorkTasks.Remove(loaded.Value.Task);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>RFC 4180 CSV of a space's tasks. Cells that a spreadsheet would read as a formula are neutralised.</summary>
    public async Task<Result<TaskExport>> ExportCsvAsync(string spaceKey, CancellationToken ct)
    {
        var list = await ListAsync(spaceKey, null, null, ct).ConfigureAwait(false);
        if (!list.IsSuccess)
        {
            return Result<TaskExport>.Failure(list.Error!);
        }

        var sb = new StringBuilder("﻿"); // BOM: spreadsheets open UTF-8 (Persian) correctly
        sb.Append("Title,Status,Assignee,Due date,Completed at,Notes\r\n");
        foreach (var t in list.Value)
        {
            sb.Append(string.Join(',', new[] { t.Title, t.Status, t.AssigneeName ?? string.Empty, t.DueDate ?? string.Empty, t.CompletedAt?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty, t.Notes ?? string.Empty }.Select(Csv))).Append("\r\n");
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

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static IQueryable<WorkTask> Filter(IQueryable<WorkTask> q, string? status) =>
        Enum.TryParse<WorkTaskStatus>(status, true, out var s) ? q.Where(t => t.Status == s) : q;

    private static IOrderedQueryable<WorkTask> Order(IQueryable<WorkTask> q) =>
        q.OrderBy(t => t.Status).ThenBy(t => t.DueDate == null).ThenBy(t => t.DueDate).ThenBy(t => t.CreatedAt);

    private bool MayComplete(WorkTask task, SpaceRights space) =>
        space.Space.Status == SpaceStatus.Active && space.CanView && (space.Can(PermissionAction.Edit) || task.AssigneeId == current.UserId);

    private async Task<Result<(WorkTask Task, SpaceRights Space)>> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var task = await (track ? db.WorkTasks : db.WorkTasks.AsNoTracking()).FirstOrDefaultAsync(t => t.Id == id, ct).ConfigureAwait(false);
        var space = task is null ? null : await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == task.SpaceId, ct).ConfigureAwait(false);
        if (task is null || space is null)
        {
            return Result<(WorkTask, SpaceRights)>.Failure(NotFound);
        }

        // A task of a space the caller cannot view does not exist for them.
        var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        return rights.CanView ? Result<(WorkTask, SpaceRights)>.Success((task, rights)) : Result<(WorkTask, SpaceRights)>.Failure(NotFound);
    }

    private async Task<Error?> ValidateAsync(SpaceRights space, TaskRequest request, CancellationToken ct)
    {
        if (request.AssigneeId is { } assignee)
        {
            var principal = await directory.GetPrincipalAsync(assignee, fresh: false, ct).ConfigureAwait(false);
            if (!principal.IsActiveMember)
            {
                return AssigneeInvalid;
            }

            if (!(await authz.GetSpaceRightsAsync(space.Space, principal, ct).ConfigureAwait(false)).CanView)
            {
                return AssigneeNoAccess;
            }
        }

        if (request.ContentId is { } content && !await db.Nodes.AnyAsync(n => n.Id == content && n.SpaceId == space.Space.Id && n.Status != ContentStatus.Trashed, ct).ConfigureAwait(false))
        {
            return ContentInvalid;
        }

        return null;
    }

    private async Task<IReadOnlyList<TaskDto>> ToDtosAsync(IReadOnlyList<WorkTask> tasks, Dictionary<Guid, SpaceRights> rights, CancellationToken ct)
    {
        var names = await directory.GetUserNamesAsync(tasks.Select(t => t.AssigneeId).Concat(tasks.Select(t => (Guid?)t.CreatedBy)).OfType<Guid>().Distinct().ToList(), ct).ConfigureAwait(false);
        var today = DateOnly.FromDateTime(clock.UtcNow.UtcDateTime);
        var me = current.UserId;
        return tasks.Select(t =>
        {
            var r = rights[t.SpaceId];
            return new TaskDto(
                t.Id, r.Space.Key, r.Space.Name, t.ContentId, t.Title, t.Notes, t.Status.ToString(), t.AssigneeId, t.AssigneeId is { } a ? names.GetValueOrDefault(a) : null,
                t.DueDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), t.Status == WorkTaskStatus.Open && t.DueDate is { } d && d < today, t.CompletedAt, t.CreatedAt, names.GetValueOrDefault(t.CreatedBy), t.Revision,
                new TaskRightsDto(r.CanWrite(PermissionAction.Edit), r.Space.Status == SpaceStatus.Active && r.CanView && (r.Can(PermissionAction.Edit) || t.AssigneeId == me), r.CanWrite(PermissionAction.Edit)));
        }).ToList();
    }
}
