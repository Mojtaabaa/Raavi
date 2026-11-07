using Raavi.Application.Abstractions.Tenancy;

namespace Raavi.Infrastructure.Tenancy;

/// <summary>
/// Scoped implementation of <see cref="ITenantContext"/>. Trusted infrastructure (resolution middleware, tenant scope
/// factory) calls <see cref="Set"/> exactly once per scope; any second call throws, which makes the context immutable.
/// Feature code only ever sees the read-only <see cref="ITenantContext"/> interface.
/// </summary>
public sealed class TenantContextHolder : ITenantContext
{
    private ResolvedTenant? _tenant;

    public bool IsResolved => _tenant is not null;

    public ResolvedTenant Tenant => _tenant ?? throw new TenantNotResolvedException();

    public void Set(ResolvedTenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        if (tenant.TenantId == Guid.Empty)
        {
            throw new ArgumentException("Tenant id must not be empty.", nameof(tenant));
        }

        if (Interlocked.CompareExchange(ref _tenant, tenant, null) is not null)
        {
            throw new InvalidOperationException("The tenant context has already been set for this scope and is immutable.");
        }
    }
}
