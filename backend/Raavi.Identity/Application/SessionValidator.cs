using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Time;
using Raavi.Identity.Persistence;

namespace Raavi.Identity.Application;

/// <summary>Reads the session row directly (no cache) so revocations made by any node are visible immediately.</summary>
public sealed class SessionValidator(IdentityDbContext db, IClock clock) : ISessionValidator
{
    public async Task<bool> IsActiveAsync(Guid sessionId, Guid userId, CancellationToken cancellationToken = default)
    {
        var now = clock.UtcNow;
        return await db.Sessions.AsNoTracking()
            .AnyAsync(s => s.Id == sessionId && s.UserId == userId && s.RevokedAt == null && s.IdleExpiresAt > now && s.AbsoluteExpiresAt > now, cancellationToken)
            .ConfigureAwait(false);
    }
}
