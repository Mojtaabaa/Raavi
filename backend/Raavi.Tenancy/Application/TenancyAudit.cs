using System.Text.Json;
using Raavi.Application.Abstractions.Time;
using Raavi.Infrastructure.Http;
using Raavi.Tenancy.Domain;
using Raavi.Tenancy.Persistence;

namespace Raavi.Tenancy.Application;

/// <summary>
/// Adds audit rows to the same DbContext (and therefore the same transaction) as the audited change: if the audit row
/// cannot be written, the change is not committed either (SECURITY_ARCHITECTURE.md §8).
/// </summary>
public sealed class TenancyAudit(TenancyDbContext db, IAuditActorAccessor actors, IClock clock)
{
    public void System(string action, Guid? tenantId, string targetType, string? targetId, object? details = null)
    {
        var actor = actors.Current;
        db.SystemAuditLogs.Add(SystemAuditLog.Create(clock.UtcNow, actor.ActorType, actor.ActorId, action, tenantId, targetType, targetId, actor.CorrelationId, Serialize(details)));
    }

    public void Tenant(string action, string targetType, string? targetId, object? details = null)
    {
        var actor = actors.Current;
        db.TenantAuditLogs.Add(TenantAuditLog.Create(clock.UtcNow, actor.ActorType, actor.ActorId, action, targetType, targetId, actor.CorrelationId, Serialize(details)));
    }

    private static string? Serialize(object? details) => details is null ? null : JsonSerializer.Serialize(details);
}
