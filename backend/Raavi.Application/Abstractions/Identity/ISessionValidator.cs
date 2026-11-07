namespace Raavi.Application.Abstractions.Identity;

/// <summary>Whether a sign-in session is still valid (not revoked, not expired) — used to re-check long-lived connections.</summary>
public interface ISessionValidator
{
    Task<bool> IsActiveAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default);
}
