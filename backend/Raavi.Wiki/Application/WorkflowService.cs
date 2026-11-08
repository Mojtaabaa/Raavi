using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Http;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using Raavi.Wiki.Security;

namespace Raavi.Wiki.Application;

public sealed record WfConditionDto(string Field, string Operator, string Value);

public sealed record WfActionDto(string Type, string? Label, string? Recipients, string? Message);

public sealed record WorkflowDto(
    Guid Id, string SpaceKey, string SpaceName, string Name, bool Enabled, string Trigger, WfConditionDto[] Conditions, WfActionDto[] Actions,
    string? CreatedByName, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, int Revision);

public sealed record WfConditionRequest(string? Field, string? Operator, string? Value);

public sealed record WfActionRequest(string? Type, string? Label, string? Recipients, string? Message);

public sealed record WorkflowRequest(string? Name, bool? Enabled, string? Trigger, WfConditionRequest[]? Conditions, WfActionRequest[]? Actions);

public sealed record WorkflowRunDto(Guid Id, Guid EventId, string EventType, Guid? ContentId, string? ContentTitle, string Status, string Message, int Attempts, DateTimeOffset StartedAt, DateTimeOffset FinishedAt);

public sealed record WorkflowCatalogDto(string[] Triggers);

/// <summary>Space workflows (Phase 10a, ADR-031): rule definitions, run history. Execution is <see cref="WorkflowEngine"/>.</summary>
public sealed class WorkflowService(WikiDbContext db, SpaceService spaces, WikiAuthorizer authz, IPrincipalDirectory directory, ICurrentUser current, IClock clock)
{
    public static readonly Error NotFound = new("workflow.not_found", "Workflow not found.", ErrorKind.NotFound);
    public static readonly Error Invalid = new("workflow.invalid", "The workflow definition is not valid.", ErrorKind.Validation);

    public static WorkflowCatalogDto Catalog() => new([.. WorkflowTriggers.All]);

    public async Task<Result<IReadOnlyList<WorkflowDto>>> ListAsync(string spaceKey, CancellationToken ct)
    {
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<IReadOnlyList<WorkflowDto>>.Failure(WikiErrors.SpaceNotFound);
        }

        if (!space.CanAdminister)
        {
            return Result<IReadOnlyList<WorkflowDto>>.Failure(WikiErrors.Forbidden);
        }

        var list = await db.Workflows.AsNoTracking().Where(w => w.SpaceId == space.Space.Id).OrderBy(w => w.Name).Take(200).ToListAsync(ct).ConfigureAwait(false);
        var names = await directory.GetUserNamesAsync(list.Select(w => w.CreatedBy).Distinct().ToList(), ct).ConfigureAwait(false);
        return Result<IReadOnlyList<WorkflowDto>>.Success(list.Select(w => ToDto(w, space.Space, names.GetValueOrDefault(w.CreatedBy))).ToList());
    }

    public async Task<Result<WorkflowDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WorkflowDto>.Failure(loaded.Error!);
        }

        var names = await directory.GetUserNamesAsync([loaded.Value.Workflow.CreatedBy], ct).ConfigureAwait(false);
        return Result<WorkflowDto>.Success(ToDto(loaded.Value.Workflow, loaded.Value.Space.Space, names.GetValueOrDefault(loaded.Value.Workflow.CreatedBy)));
    }

    public async Task<Result<WorkflowDto>> CreateAsync(string spaceKey, WorkflowRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var space = await spaces.LoadAsync(spaceKey, requireView: true, ct).ConfigureAwait(false);
        if (space is null)
        {
            return Result<WorkflowDto>.Failure(WikiErrors.SpaceNotFound);
        }

        if (!space.CanAdminister)
        {
            return Result<WorkflowDto>.Failure(WikiErrors.Forbidden);
        }

        if (space.Space.Status == SpaceStatus.Archived)
        {
            return Result<WorkflowDto>.Failure(WikiErrors.SpaceArchived);
        }

        var parsed = Parse(request);
        if (!parsed.IsSuccess)
        {
            return Result<WorkflowDto>.Failure(parsed.Error!);
        }

        var w = Workflow.Create(space.Space.Id, request.Name, request.Enabled ?? true, request.Trigger, parsed.Value.Conditions, parsed.Value.Actions, current.UserId, clock.UtcNow);
        db.Workflows.Add(w);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<WorkflowDto>.Success(ToDto(w, space.Space, null));
    }

    public async Task<Result<WorkflowDto>> UpdateAsync(Guid id, int expectedRevision, WorkflowRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<WorkflowDto>.Failure(loaded.Error!);
        }

        var (w, space) = loaded.Value;
        if (w.Revision != expectedRevision)
        {
            return Result<WorkflowDto>.Failure(ETags.PreconditionFailed);
        }

        var parsed = Parse(request);
        if (!parsed.IsSuccess)
        {
            return Result<WorkflowDto>.Failure(parsed.Error!);
        }

        w.Update(request.Name, request.Enabled ?? w.Enabled, request.Trigger, parsed.Value.Conditions, parsed.Value.Actions, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<WorkflowDto>.Success(ToDto(w, space.Space, null));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: true, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result.Failure(loaded.Error!);
        }

        db.WorkflowRuns.RemoveRange(await db.WorkflowRuns.Where(r => r.WorkflowId == id).ToListAsync(ct).ConfigureAwait(false));
        db.Workflows.Remove(loaded.Value.Workflow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<WorkflowRunDto>>> RunsAsync(Guid id, int take, CancellationToken ct)
    {
        var loaded = await LoadAsync(id, track: false, ct).ConfigureAwait(false);
        if (!loaded.IsSuccess)
        {
            return Result<IReadOnlyList<WorkflowRunDto>>.Failure(loaded.Error!);
        }

        var runs = await db.WorkflowRuns.AsNoTracking().Where(r => r.WorkflowId == id).OrderByDescending(r => r.StartedAt).ThenByDescending(r => r.Id).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct).ConfigureAwait(false);
        var ids = runs.Where(r => r.ContentId is not null).Select(r => r.ContentId!.Value).Distinct().ToList();
        var titles = await db.Nodes.AsNoTracking().Where(n => ids.Contains(n.Id)).ToDictionaryAsync(n => n.Id, n => n.Title, ct).ConfigureAwait(false);
        return Result<IReadOnlyList<WorkflowRunDto>>.Success(runs.Select(r => new WorkflowRunDto(
            r.Id, r.EventId, r.EventType, r.ContentId, r.ContentId is { } c ? titles.GetValueOrDefault(c) : null, r.Status.ToString(), r.Message, r.Attempts, r.StartedAt, r.FinishedAt)).ToList());
    }

    private static Result<(List<WfConditionInput> Conditions, List<WfActionInput> Actions)> Parse(WorkflowRequest request)
    {
        var conditions = new List<WfConditionInput>();
        foreach (var c in request.Conditions ?? [])
        {
            if (!Enum.TryParse<WfField>(c.Field, true, out var field) || !Enum.TryParse<WfOperator>(c.Operator, true, out var op))
            {
                return Result<(List<WfConditionInput>, List<WfActionInput>)>.Failure(new Error("workflow.condition_invalid", "The condition is not supported.", ErrorKind.Validation));
            }

            conditions.Add(new WfConditionInput(field, op, c.Value));
        }

        var actions = new List<WfActionInput>();
        foreach (var a in request.Actions ?? [])
        {
            if (!Enum.TryParse<WfActionType>(a.Type, true, out var type))
            {
                return Result<(List<WfConditionInput>, List<WfActionInput>)>.Failure(new Error("workflow.action_invalid", "The action is not supported.", ErrorKind.Validation));
            }

            WfRecipients? recipients = null;
            if (!string.IsNullOrWhiteSpace(a.Recipients))
            {
                if (!Enum.TryParse<WfRecipients>(a.Recipients, true, out var r))
                {
                    return Result<(List<WfConditionInput>, List<WfActionInput>)>.Failure(new Error("workflow.action_invalid", "The recipients are not supported.", ErrorKind.Validation));
                }

                recipients = r;
            }

            actions.Add(new WfActionInput(type, a.Label, recipients, a.Message));
        }

        return Result<(List<WfConditionInput>, List<WfActionInput>)>.Success((conditions, actions));
    }

    private async Task<Result<(Workflow Workflow, SpaceRights Space)>> LoadAsync(Guid id, bool track, CancellationToken ct)
    {
        var w = await (track ? db.Workflows : db.Workflows.AsNoTracking()).FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        var space = w is null ? null : await db.Spaces.AsNoTracking().FirstOrDefaultAsync(s => s.Id == w.SpaceId, ct).ConfigureAwait(false);
        if (w is null || space is null)
        {
            return Result<(Workflow, SpaceRights)>.Failure(NotFound);
        }

        var rights = await authz.GetSpaceRightsAsync(space, ct).ConfigureAwait(false);
        if (!rights.CanView)
        {
            return Result<(Workflow, SpaceRights)>.Failure(NotFound);
        }

        return rights.CanAdminister ? Result<(Workflow, SpaceRights)>.Success((w, rights)) : Result<(Workflow, SpaceRights)>.Failure(WikiErrors.Forbidden);
    }

    private static WorkflowDto ToDto(Workflow w, Space space, string? createdBy) => new(
        w.Id, space.Key, space.Name, w.Name, w.Enabled, w.Trigger,
        w.Conditions.Select(c => new WfConditionDto(c.Field.ToString(), c.Operator.ToString(), c.Value)).ToArray(),
        w.Actions.Select(a => new WfActionDto(a.Type.ToString(), a.Label, a.Recipients?.ToString(), a.Message)).ToArray(),
        createdBy, w.CreatedAt, w.UpdatedAt, w.Revision);
}
