using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Caching;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Identity.Providers;

namespace Raavi.Identity.Security;

/// <summary>Membership + built-in roles of a user in the <b>current</b> tenant scope.</summary>
public sealed record MemberSnapshot(bool IsActiveMember, bool IsTenantAdministrator);

/// <summary>
/// "Which tenant may this user enter" (AUTHENTICATION_ARCHITECTURE.md, membership ≠ authentication). Reads only the
/// current tenant's rows (EF filter + RLS) and caches per tenant via <see cref="ITenantCache"/>.
/// </summary>
public sealed class MembershipService(IdentityDbContext db, ITenantCache cache) : ITenantMembershipService
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    public async ValueTask<MemberSnapshot> GetAsync(Guid userId, CancellationToken ct) =>
        await cache.GetOrCreateAsync($"member:{userId:N}", async c =>
        {
            var status = await db.Members.AsNoTracking().Where(m => m.UserId == userId).Select(m => (MembershipStatus?)m.Status).FirstOrDefaultAsync(c).ConfigureAwait(false);
            var admin = status == MembershipStatus.Active
                && await db.TenantRoles.AsNoTracking().AnyAsync(r => r.UserId == userId && r.Role == TenantRole.TenantAdministrator, c).ConfigureAwait(false);
            return new MemberSnapshot(status == MembershipStatus.Active, admin);
        }, Ttl, ct).ConfigureAwait(false);

    public ValueTask InvalidateAsync(Guid userId, CancellationToken ct) => cache.RemoveAsync($"member:{userId:N}", ct);

    /// <summary>Contract for other modules. The tenant is the one of the current scope; <paramref name="tenantId"/> must match it.</summary>
    public async ValueTask<bool> IsActiveMemberAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken)
    {
        if (db.CurrentTenantId != tenantId)
        {
            throw new InvalidOperationException("Membership can only be checked inside the tenant's own scope.");
        }

        return (await GetAsync(userId, cancellationToken).ConfigureAwait(false)).IsActiveMember;
    }
}
