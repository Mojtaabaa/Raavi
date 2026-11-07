using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Tenancy;

namespace Raavi.Infrastructure.Tenancy;

/// <summary>
/// Creates a DI scope bound to one explicit tenant. Used by background jobs and by audited system operations that must
/// act on a specific tenant's data (e.g. provisioning). There is no "all tenants" scope: cross-tenant work fans out
/// into one scope per tenant (TENANT_ISOLATION.md §6–§7).
/// </summary>
public interface ITenantScopeFactory
{
    AsyncServiceScope CreateScope(ResolvedTenant tenant);
}

public sealed class TenantScopeFactory(IServiceScopeFactory scopeFactory) : ITenantScopeFactory
{
    public AsyncServiceScope CreateScope(ResolvedTenant tenant)
    {
        ArgumentNullException.ThrowIfNull(tenant);
        var scope = scopeFactory.CreateAsyncScope();
        try
        {
            scope.ServiceProvider.GetRequiredService<TenantContextHolder>().Set(tenant);
            return scope;
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
