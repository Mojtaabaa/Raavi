using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Domain.Common;

namespace Raavi.Infrastructure.Persistence;

/// <summary>
/// Enforces tenant ownership on writes (TENANT_ISOLATION.md §2, layer 5):
///   - Added tenant-owned entities get <c>TenantId</c> from the resolved context; a pre-set different value throws.
///   - Modifying <c>TenantId</c> throws.
///   - Writing tenant-owned entities without a resolved tenant throws.
/// </summary>
public sealed class TenantStampingInterceptor(ITenantContext tenantContext) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        Apply(eventData?.Context);
        return base.SavingChanges(eventData!, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        Apply(eventData?.Context);
        return base.SavingChangesAsync(eventData!, result, cancellationToken);
    }

    private void Apply(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        foreach (var entry in context.ChangeTracker.Entries<ITenantOwned>())
        {
            if (entry.State is EntityState.Unchanged or EntityState.Detached)
            {
                continue;
            }

            if (!tenantContext.IsResolved)
            {
                throw new TenantNotResolvedException($"Cannot write tenant-owned entity '{entry.Metadata.ClrType.Name}' without a resolved tenant.");
            }

            var current = tenantContext.Tenant.TenantId;
            var property = entry.Property(nameof(ITenantOwned.TenantId));

            switch (entry.State)
            {
                case EntityState.Added:
                    var value = (Guid)property.CurrentValue!;
                    if (value == Guid.Empty)
                    {
                        property.CurrentValue = current;
                    }
                    else if (value != current)
                    {
                        throw new InvalidOperationException($"Tenant-owned entity '{entry.Metadata.ClrType.Name}' belongs to another tenant.");
                    }

                    break;

                case EntityState.Modified:
                case EntityState.Deleted:
                    if (property.IsModified || (Guid)property.OriginalValue! != current)
                    {
                        throw new InvalidOperationException($"TenantId of '{entry.Metadata.ClrType.Name}' cannot be changed or written across tenants.");
                    }

                    break;
            }
        }
    }
}
