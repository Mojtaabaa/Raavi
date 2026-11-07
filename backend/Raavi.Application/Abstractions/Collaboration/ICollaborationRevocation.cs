namespace Raavi.Application.Abstractions.Collaboration;

/// <summary>
/// Asks the real-time collaboration server to re-check live editing connections now (ADR-011 §revocation) instead of
/// waiting for the periodic re-check. Implemented by the Wiki module; called by modules whose changes can remove access
/// (membership, groups, sessions). Best effort: failures are logged, the periodic re-check remains the safety net.
/// </summary>
public interface ICollaborationRevocation
{
    /// <summary>Re-check connections of the current tenant, optionally only those of <paramref name="userId"/>.</summary>
    Task RecheckAsync(Guid tenantId, Guid? userId, CancellationToken cancellationToken = default);
}
