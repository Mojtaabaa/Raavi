namespace Raavi.Domain.Common;

/// <summary>Tenant lifecycle states (MULTI_TENANT_ARCHITECTURE.md §1, §7). Only <see cref="Active"/> serves normal traffic. Values are persisted.</summary>
public enum TenantStatus
{
    Provisioning = 1,
    Active = 2,
    Suspended = 3,
    Deactivated = 4,
    PendingDeletion = 5,
    Deleted = 6,
}
