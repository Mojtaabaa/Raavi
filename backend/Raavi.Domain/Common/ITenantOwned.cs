namespace Raavi.Domain.Common;

/// <summary>
/// Marks an entity as owned by exactly one tenant. The TenantId is assigned by the persistence layer
/// from the resolved tenant context and can never be changed afterwards (see docs/architecture/TENANT_ISOLATION.md).
/// </summary>
public interface ITenantOwned
{
    Guid TenantId { get; }
}
