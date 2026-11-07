using Raavi.Domain.Common;

namespace Raavi.Identity.Domain;

/// <summary>
/// Global, non-sensitive index of "which tenants does this user belong to" — used only to list the signed-in user's own
/// workspaces for tenant switching (MULTI_TENANT_ARCHITECTURE.md §6). The authoritative membership remains the
/// RLS-protected <see cref="TenantMember"/>; this row is maintained automatically by <c>IdentityDbContext</c> in the same
/// SaveChanges as every membership change and is never used for authorization.
/// </summary>
public sealed class UserTenantMembership : Entity
{
    private UserTenantMembership() { }

    public Guid UserId { get; private set; }

    public Guid TenantId { get; private set; }

    public MembershipStatus Status { get; private set; }

    public static UserTenantMembership For(Guid userId, Guid tenantId, MembershipStatus status) =>
        new() { UserId = userId, TenantId = tenantId, Status = status };

    public void SetStatus(MembershipStatus status) => Status = status;
}
