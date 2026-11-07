using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Domain.Common;

namespace Raavi.Infrastructure.Persistence;

/// <summary>
/// Base for every Raavi DbContext (MODULE_BOUNDARIES.md §3). By convention, every entity implementing
/// <see cref="ITenantOwned"/> gets:
///   - the named query filter <see cref="TenantFilterName"/> (<c>TenantId == current tenant</c>; no tenant → no rows),
///   - a <c>TenantId</c> column that is required and never updated by EF,
///   - an index whose leading column is <c>TenantId</c> (added when the entity configuration did not declare one).
/// The TenantId stamping and SESSION_CONTEXT interceptors are added by <see cref="PersistenceExtensions"/>; SQL Server
/// Row-Level Security (added by migrations) is the independent safety net.
/// </summary>
public abstract class RaaviDbContextBase(DbContextOptions options, ITenantContext tenantContext) : DbContext(options)
{
    public const string TenantFilterName = "TenantIsolation";

    /// <summary>Current tenant id, or <see cref="Guid.Empty"/> (matches no row) when none is resolved. Evaluated per query.</summary>
    public Guid CurrentTenantId => tenantContext.IsResolved ? tenantContext.Tenant.TenantId : Guid.Empty;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        base.OnModelCreating(modelBuilder);
        ApplyTenantConventions(modelBuilder);
    }

    private void ApplyTenantConventions(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType) && t.BaseType is null))
        {
            var builder = modelBuilder.Entity(entityType.ClrType);
            var tenantProperty = builder.Property<Guid>(nameof(ITenantOwned.TenantId)).IsRequired();
            tenantProperty.Metadata.SetAfterSaveBehavior(Microsoft.EntityFrameworkCore.Metadata.PropertySaveBehavior.Throw);

            builder.HasQueryFilter(TenantFilterName, BuildTenantFilter(entityType.ClrType));

            var hasTenantLeadingIndex = entityType.GetIndexes().Any(i => i.Properties[0].Name == nameof(ITenantOwned.TenantId))
                || entityType.GetKeys().Any(k => k.Properties[0].Name == nameof(ITenantOwned.TenantId));
            if (!hasTenantLeadingIndex)
            {
                builder.HasIndex(nameof(ITenantOwned.TenantId));
            }
        }
    }

    private LambdaExpression BuildTenantFilter(Type clrType)
    {
        // e => EF.Property<Guid>(e, "TenantId") == this.CurrentTenantId   (EF parameterizes the context member per query)
        var e = Expression.Parameter(clrType, "e");
        var tenantId = Expression.Call(typeof(EF), nameof(EF.Property), [typeof(Guid)], e, Expression.Constant(nameof(ITenantOwned.TenantId)));
        var current = Expression.Property(Expression.Constant(this), nameof(CurrentTenantId));
        return Expression.Lambda(Expression.Equal(tenantId, current), e);
    }

    /// <summary>Entity types that are tenant-owned in this context (used by isolation model tests).</summary>
    public IReadOnlyList<IEntityType> TenantOwnedEntityTypes() =>
        Model.GetEntityTypes().Where(t => typeof(ITenantOwned).IsAssignableFrom(t.ClrType)).ToList();
}
