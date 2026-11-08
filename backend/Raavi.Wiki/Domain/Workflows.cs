using System.Text.Json;
using System.Text.Json.Serialization;
using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

public enum WfField
{
    Title = 1,
    ContentType = 2,
    Label = 3,
}

public enum WfOperator
{
    Contains = 1,
    Equals = 2,
    StartsWith = 3,
    Has = 4,
}

/// <summary>One condition; all conditions of a workflow must hold (AND). Text comparisons are case-insensitive.</summary>
public sealed record WfCondition(WfField Field, WfOperator Operator, string Value);

public enum WfActionType
{
    AddLabel = 1,
    Notify = 2,
}

public enum WfRecipients
{
    Watchers = 1,
    Actor = 2,
}

/// <summary>An action: <c>AddLabel</c> uses <see cref="Label"/>; <c>Notify</c> uses <see cref="Recipients"/> and <see cref="Message"/>.</summary>
public sealed record WfAction(WfActionType Type, string? Label, WfRecipients? Recipients, string? Message);

public sealed record WfConditionInput(WfField Field, WfOperator Operator, string? Value);

public sealed record WfActionInput(WfActionType Type, string? Label, WfRecipients? Recipients, string? Message);

public static class WorkflowTriggers
{
    /// <summary>Wiki events a workflow can start from (page events only; no workflow action emits any of them, so rules cannot loop).</summary>
    public static readonly IReadOnlyList<string> All =
    [
        "content.created", "content.updated", "content.moved", "content.archived", "content.trashed", "content.published", "content.publish_requested",
    ];

    public static bool IsValid(string? trigger) => trigger is not null && All.Contains(trigger);
}

internal static class WorkflowJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

/// <summary>
/// A rule of a space (Phase 10a): <i>when</i> a page event happens and the conditions hold, run the actions. Runs asynchronously from the
/// outbox, once per event (see <see cref="WorkflowRun"/>). Only space administrators manage rules.
/// </summary>
public sealed class Workflow : Entity, ITenantOwned
{
    public const int MaxNameLength = 200;
    public const int MaxConditions = 10;
    public const int MaxActions = 5;
    public const int MaxValueLength = 200;
    public const int MaxMessageLength = 200;
    public const int MaxRunsPerHour = 100;

    private Workflow() { }

    public Guid TenantId { get; private set; }

    public Guid SpaceId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public bool Enabled { get; private set; }

    public string Trigger { get; private set; } = string.Empty;

    public string ConditionsJson { get; private set; } = "[]";

    public string ActionsJson { get; private set; } = "[]";

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public IReadOnlyList<WfCondition> Conditions => JsonSerializer.Deserialize<List<WfCondition>>(ConditionsJson, WorkflowJson.Options) ?? [];

    public IReadOnlyList<WfAction> Actions => JsonSerializer.Deserialize<List<WfAction>>(ActionsJson, WorkflowJson.Options) ?? [];

    public static Workflow Create(Guid spaceId, string? name, bool enabled, string? trigger, IReadOnlyList<WfConditionInput>? conditions, IReadOnlyList<WfActionInput>? actions, Guid userId, DateTimeOffset now)
    {
        var w = new Workflow { SpaceId = spaceId, CreatedBy = userId, CreatedAt = now, UpdatedAt = now };
        w.Set(name, enabled, trigger, conditions ?? [], actions ?? []);
        return w;
    }

    public void Update(string? name, bool enabled, string? trigger, IReadOnlyList<WfConditionInput>? conditions, IReadOnlyList<WfActionInput>? actions, DateTimeOffset now)
    {
        Set(name, enabled, trigger, conditions ?? [], actions ?? []);
        UpdatedAt = now;
        Revision++;
    }

    private void Set(string? name, bool enabled, string? trigger, IReadOnlyList<WfConditionInput> conditions, IReadOnlyList<WfActionInput> actions)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length is 0 or > MaxNameLength || n.Any(char.IsControl))
        {
            throw new DomainException("workflow.name_invalid", $"A workflow needs a name of 1–{MaxNameLength} characters.");
        }

        if (!WorkflowTriggers.IsValid(trigger))
        {
            throw new DomainException("workflow.trigger_invalid", "The trigger is not a supported page event.");
        }

        if (conditions.Count > MaxConditions)
        {
            throw new DomainException("workflow.too_many_conditions", $"A workflow can have at most {MaxConditions} conditions.");
        }

        if (actions.Count is 0 or > MaxActions)
        {
            throw new DomainException("workflow.actions_invalid", $"A workflow needs 1–{MaxActions} actions.");
        }

        var cs = conditions.Select(ToCondition).ToList();
        var acts = actions.Select(ToAction).ToList();
        Name = n;
        Enabled = enabled;
        Trigger = trigger!;
        ConditionsJson = JsonSerializer.Serialize(cs, WorkflowJson.Options);
        ActionsJson = JsonSerializer.Serialize(acts, WorkflowJson.Options);
    }

    private static WfCondition ToCondition(WfConditionInput c)
    {
        if (!Enum.IsDefined(c.Field) || !Enum.IsDefined(c.Operator))
        {
            throw new DomainException("workflow.condition_invalid", "The condition is not supported.");
        }

        var allowed = c.Field switch
        {
            WfField.Title => new[] { WfOperator.Contains, WfOperator.Equals, WfOperator.StartsWith },
            WfField.ContentType => new[] { WfOperator.Equals },
            _ => new[] { WfOperator.Has },
        };
        var value = (c.Value ?? string.Empty).Trim();
        if (!allowed.Contains(c.Operator) || value.Length is 0 or > MaxValueLength || value.Any(char.IsControl))
        {
            throw new DomainException("workflow.condition_invalid", "The condition needs a supported operator and a value of up to 200 characters.");
        }

        if (c.Field == WfField.Label)
        {
            value = Label.Normalize(value);
        }

        return new WfCondition(c.Field, c.Operator, value);
    }

    private static WfAction ToAction(WfActionInput a)
    {
        switch (a.Type)
        {
            case WfActionType.AddLabel:
                return new WfAction(a.Type, Label.Normalize(a.Label), null, null);
            case WfActionType.Notify:
                var message = (a.Message ?? string.Empty).Trim();
                if (a.Recipients is not { } r || !Enum.IsDefined(r) || message.Length is 0 or > MaxMessageLength || message.Any(char.IsControl))
                {
                    throw new DomainException("workflow.action_invalid", "A notification needs recipients and a message of up to 200 characters.");
                }

                return new WfAction(a.Type, null, r, message);
            default:
                throw new DomainException("workflow.action_invalid", "The action is not supported.");
        }
    }
}

public enum WorkflowRunStatus
{
    Succeeded = 1,
    Skipped = 2,
    Failed = 3,
}

/// <summary>
/// Log of one workflow handling one event. Unique per (workflow, event): this is what makes the at-least-once outbox idempotent, and it is
/// the run history shown to administrators. Failed runs are retried by the outbox (with back-off) and keep counting attempts.
/// </summary>
public sealed class WorkflowRun : Entity, ITenantOwned
{
    public const int MaxMessageLength = 500;

    private WorkflowRun() { }

    public Guid TenantId { get; private set; }

    public Guid WorkflowId { get; private set; }

    public Guid EventId { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public Guid? ContentId { get; private set; }

    public WorkflowRunStatus Status { get; private set; }

    public string Message { get; private set; } = string.Empty;

    public int Attempts { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset FinishedAt { get; private set; }

    public static WorkflowRun Start(Guid workflowId, Guid eventId, string eventType, Guid? contentId, DateTimeOffset now) =>
        new() { WorkflowId = workflowId, EventId = eventId, EventType = eventType, ContentId = contentId, StartedAt = now, FinishedAt = now };

    public void Finish(WorkflowRunStatus status, string message, DateTimeOffset now)
    {
        Attempts++;
        Status = status;
        Message = message.Length > MaxMessageLength ? message[..MaxMessageLength] : message;
        FinishedAt = now;
    }
}
