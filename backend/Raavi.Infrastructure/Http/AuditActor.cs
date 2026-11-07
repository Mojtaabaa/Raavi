using System.Diagnostics;
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Raavi.Application.Abstractions.Authorization;

namespace Raavi.Infrastructure.Http;

/// <summary>Who performed an audited action, derived from the authenticated principal (or the worker process).</summary>
public sealed record AuditActor(string ActorType, string ActorId, string CorrelationId);

public interface IAuditActorAccessor
{
    AuditActor Current { get; }
}

public sealed class HttpAuditActorAccessor(IHttpContextAccessor accessor) : IAuditActorAccessor
{
    public AuditActor Current
    {
        get
        {
            var ctx = accessor.HttpContext;
            var correlation = ctx?.TraceIdentifier ?? Activity.Current?.TraceId.ToString() ?? "none";
            var user = ctx?.User;
            if (user?.Identity?.IsAuthenticated == true)
            {
                return new AuditActor(
                    user.FindFirstValue(RaaviPolicies.ActorTypeClaim) ?? "user",
                    user.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown",
                    correlation);
            }

            return ctx is null ? new AuditActor("worker", Environment.MachineName, correlation) : new AuditActor("anonymous", "-", correlation);
        }
    }
}
