using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Persistence;
using Raavi.Identity.Persistence;
using Raavi.Tenancy.Persistence;
using Raavi.Wiki.Persistence;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Isolation;

/// <summary>Every tenant-owned entity must have the tenant filter, a TenantId-leading index and an RLS policy entry (TENANT_ISOLATION.md §8).</summary>
[Collection(SqlTestGroup.Name)]
public sealed class TenantIsolationModelTests(SqlServerFixture fx)
{
    [Fact]
    public async Task Every_tenant_owned_entity_is_filtered_indexed_and_rls_protected()
    {
        await using var scope = fx.UnscopedScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        var owned = db.TenantOwnedEntityTypes().Concat(scope.ServiceProvider.GetRequiredService<IdentityDbContext>().TenantOwnedEntityTypes())
            .Concat(scope.ServiceProvider.GetRequiredService<WikiDbContext>().TenantOwnedEntityTypes()).ToList();
        Assert.True(owned.Count >= 20, $"expected tenant-owned entities from all modules, found {owned.Count}");

        var protectedTables = (await db.Database.SqlQueryRaw<string>("""
            SELECT s.name + '.' + t.name AS Value
            FROM sys.security_predicates p
            JOIN sys.security_policies sp ON sp.object_id = p.object_id
            JOIN sys.tables t ON t.object_id = p.target_object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE sp.name = 'TenantIsolation' AND sp.is_enabled = 1
            GROUP BY s.name, t.name
            HAVING COUNT(*) = 3
            """).ToListAsync()).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entity in owned)
        {
            var table = $"{entity.GetSchema()}.{entity.GetTableName()}";
            Assert.True(entity.GetDeclaredQueryFilters().Any(f => f.Key == RaaviDbContextBase.TenantFilterName), $"{table}: missing tenant query filter");
            Assert.True(entity.GetIndexes().Any(i => i.Properties[0].Name == nameof(ITenantOwned.TenantId)), $"{table}: missing TenantId-leading index");
            Assert.Contains(table, protectedTables);
        }
    }
}
