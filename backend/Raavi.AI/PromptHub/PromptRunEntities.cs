using Raavi.Application.Abstractions.Tenancy;
using Raavi.Domain.Common;

namespace Raavi.AI.PromptHub;

public enum PromptExecutionStatus
{
    Running = 1,
    Completed = 2,
    Failed = 3,
}

/// <summary>One automatic run through the workspace's Managed AI. References the exact version used; the result text is kept for its owner (no automatic retention job yet, ADR-052 limits).</summary>
public sealed class PromptExecution : Entity, ITenantOwned
{
    private PromptExecution() { }

    public Guid TenantId { get; private set; }

    public Guid PromptId { get; private set; }

    public Guid VersionId { get; private set; }

    public Guid UserId { get; private set; }

    public string ContextKind { get; private set; } = string.Empty;

    public PromptExecutionStatus Status { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public int? DurationMs { get; private set; }

    public string? Model { get; private set; }

    public int? PromptTokens { get; private set; }

    public int? CompletionTokens { get; private set; }

    public string? ErrorClass { get; private set; }

    public int EstimatedTokens { get; private set; }

    /// <summary>JSON list of <see cref="PromptSource"/> (titles and addresses, no content).</summary>
    public string SourcesJson { get; private set; } = "[]";

    public string? ResultText { get; private set; }

    public static PromptExecution Start(Guid promptId, Guid versionId, Guid userId, string contextKind, int estimatedTokens, string sourcesJson, DateTimeOffset now) =>
        new() { PromptId = promptId, VersionId = versionId, UserId = userId, ContextKind = contextKind, EstimatedTokens = estimatedTokens, SourcesJson = sourcesJson, Status = PromptExecutionStatus.Running, StartedAt = now };

    public void Complete(string text, string model, int promptTokens, int completionTokens, DateTimeOffset now)
    {
        Status = PromptExecutionStatus.Completed;
        ResultText = text;
        Model = model;
        PromptTokens = promptTokens;
        CompletionTokens = completionTokens;
        Finish(now);
    }

    public void Fail(string errorClass, DateTimeOffset now)
    {
        Status = PromptExecutionStatus.Failed;
        ErrorClass = errorClass.Length > 64 ? errorClass[..64] : errorClass;
        Finish(now);
    }

    private void Finish(DateTimeOffset now)
    {
        CompletedAt = now;
        DurationMs = (int)Math.Min(int.MaxValue, Math.Max(0, (now - StartedAt).TotalMilliseconds));
    }
}

/// <summary>A person's rating of a workspace-public prompt: one row per person and prompt (changed in place), with optional feedback.</summary>
public sealed class PromptRating : Entity, ITenantOwned
{
    public static readonly string[] FeedbackKinds = ["useful", "accurate", "needs-improvement", "outdated"];

    private PromptRating() { }

    public Guid TenantId { get; private set; }

    public Guid PromptId { get; private set; }

    public Guid UserId { get; private set; }

    public int Stars { get; private set; }

    public string? FeedbackKind { get; private set; }

    public string? Comment { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public static PromptRating Create(Guid promptId, Guid userId, int stars, string? kind, string? comment, DateTimeOffset now)
    {
        var r = new PromptRating { PromptId = promptId, UserId = userId, CreatedAt = now };
        r.Set(stars, kind, comment, now);
        return r;
    }

    public void Set(int stars, string? kind, string? comment, DateTimeOffset now)
    {
        if (stars is < 1 or > 5)
        {
            throw new DomainException("prompt.rating_invalid", "A rating is 1 to 5 stars.");
        }

        var k = kind?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(k) && !FeedbackKinds.Contains(k))
        {
            throw new DomainException("prompt.rating_invalid", "Unknown feedback type.");
        }

        var c = comment?.Trim();
        if (c is { Length: > 1000 })
        {
            throw new DomainException("prompt.rating_invalid", "The comment is too long (1000 characters at most).");
        }

        Stars = stars;
        FeedbackKind = string.IsNullOrEmpty(k) ? null : k;
        Comment = string.IsNullOrEmpty(c) ? null : c;
        UpdatedAt = now;
    }
}

public static class PromptUsageEventTypes
{
    public const string ExecutionStarted = "PromptExecutionStarted";
    public const string ExecutionCompleted = "PromptExecutionCompleted";
    public const string ExecutionFailed = "PromptExecutionFailed";
    public const string Copied = "PromptCopied";
    public const string ManualUseConfirmed = "PromptManualUseConfirmed";
    public const string Rated = "PromptRated";
    public const string Forked = "PromptForked";
    public const string VersionRestored = "PromptVersionRestored";
}
