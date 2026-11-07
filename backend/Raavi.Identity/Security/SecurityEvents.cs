using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Raavi.Application.Abstractions.Time;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Infrastructure.Http;

namespace Raavi.Identity.Security;

/// <summary>
/// Adds security events to the identity context so they commit atomically with the change they describe.
/// Callers must never pass passwords, tokens, codes or secrets in <paramref name="details"/>.
/// </summary>
public sealed class SecurityEventWriter(IdentityDbContext db, IAuditActorAccessor actors, IHttpContextAccessor http, IClock clock)
{
    public void Tenant(string type, Guid? userId, object? details = null)
    {
        var actor = actors.Current;
        db.TenantSecurityEvents.Add(TenantSecurityEvent.Create(clock.UtcNow, type, userId, actor.ActorId, Ip(), actor.CorrelationId, Serialize(details)));
    }

    public void System(string type, Guid? userId, object? details = null)
    {
        var actor = actors.Current;
        db.SystemSecurityEvents.Add(SystemSecurityEvent.Create(clock.UtcNow, type, userId, actor.ActorId, Ip(), actor.CorrelationId, Serialize(details)));
    }

    private string? Ip() => http.HttpContext?.Connection.RemoteIpAddress?.ToString();

    private static string? Serialize(object? details) => details is null ? null : JsonSerializer.Serialize(details);
}
