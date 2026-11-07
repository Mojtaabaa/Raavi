using Raavi.Domain.Common;

namespace Raavi.Tenancy.Domain;

/// <summary>
/// Append-only system audit (system host actions, cross-tenant operations). <see cref="TenantId"/> identifies the
/// affected tenant when there is one. Written in the same transaction as the audited change.
/// </summary>
public sealed class SystemAuditLog : Entity
{
    private SystemAuditLog() { }

    public DateTimeOffset OccurredAt { get; private set; }

    public string ActorType { get; private set; } = string.Empty;

    public string ActorId { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;

    public Guid? TenantId { get; private set; }

    public string TargetType { get; private set; } = string.Empty;

    public string? TargetId { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>Small JSON document with non-sensitive details. Never secrets or content.</summary>
    public string? Details { get; private set; }

    public static SystemAuditLog Create(DateTimeOffset now, string actorType, string actorId, string action, Guid? tenantId, string targetType, string? targetId, string correlationId, string? details) => new()
    {
        OccurredAt = now,
        ActorType = actorType,
        ActorId = actorId,
        Action = action,
        TenantId = tenantId,
        TargetType = targetType,
        TargetId = targetId,
        CorrelationId = correlationId,
        Details = details,
    };
}

/// <summary>Append-only, tenant-owned audit of actions performed inside a tenant. RLS-protected.</summary>
public sealed class TenantAuditLog : Entity, ITenantOwned
{
    private TenantAuditLog() { }

    public Guid TenantId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string ActorType { get; private set; } = string.Empty;

    public string ActorId { get; private set; } = string.Empty;

    public string Action { get; private set; } = string.Empty;

    public string TargetType { get; private set; } = string.Empty;

    public string? TargetId { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public string? Details { get; private set; }

    public static TenantAuditLog Create(DateTimeOffset now, string actorType, string actorId, string action, string targetType, string? targetId, string correlationId, string? details) => new()
    {
        OccurredAt = now,
        ActorType = actorType,
        ActorId = actorId,
        Action = action,
        TargetType = targetType,
        TargetId = targetId,
        CorrelationId = correlationId,
        Details = details,
    };
}
