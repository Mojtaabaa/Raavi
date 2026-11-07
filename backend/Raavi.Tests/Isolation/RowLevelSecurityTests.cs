using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Tenancy.Domain;
using Raavi.Tenancy.Persistence;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Isolation;

/// <summary>
/// Database-level tenant isolation (TENANT_ISOLATION.md §2–§3): EF filters, write interceptor and SQL Server RLS,
/// each proven independently so that a failure of one layer is still caught by the others.
/// </summary>
[Collection(SqlTestGroup.Name)]
public sealed class RowLevelSecurityTests(SqlServerFixture fx)
{
    [Fact]
    public async Task Ef_filter_shows_only_the_scoped_tenants_rows()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("A"), SqlServerFixture.Unique("a").ToLowerInvariant());
        var b = await fx.CreateTenantAsync(SqlServerFixture.Unique("B"), SqlServerFixture.Unique("b").ToLowerInvariant());

        await using var scope = fx.TenantScope(a);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var settings = await db.TenantSettings.ToListAsync();
        Assert.Single(settings);
        Assert.Equal(a.Id, settings[0].TenantId);
        Assert.False(await db.TenantBranding.AnyAsync(x => x.TenantId == b.Id));
    }

    [Fact]
    public async Task Rls_hides_other_tenants_rows_even_when_ef_filters_are_bypassed()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("A"), SqlServerFixture.Unique("a").ToLowerInvariant());
        await fx.CreateTenantAsync(SqlServerFixture.Unique("B"), SqlServerFixture.Unique("b").ToLowerInvariant());

        await using var scope = fx.TenantScope(a);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var viaEf = await db.TenantSettings.IgnoreQueryFilters().Select(s => s.TenantId).ToListAsync();
        var viaRawSql = await db.Database.SqlQueryRaw<Guid>("SELECT TenantId AS Value FROM system.TenantSettings").ToListAsync();

        Assert.Equal([a.Id], viaEf);
        Assert.Equal([a.Id], viaRawSql);
    }

    [Fact]
    public async Task Without_a_tenant_no_tenant_rows_are_visible()
    {
        await fx.CreateTenantAsync(SqlServerFixture.Unique("A"), SqlServerFixture.Unique("a").ToLowerInvariant());

        await using var scope = fx.UnscopedScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        Assert.Equal(0, await db.TenantSettings.IgnoreQueryFilters().CountAsync());
        Assert.Empty(await db.Database.SqlQueryRaw<Guid>("SELECT TenantId AS Value FROM system.TenantBranding").ToListAsync());
    }

    [Fact]
    public async Task Rls_blocks_raw_inserts_and_updates_into_another_tenant()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("A"), SqlServerFixture.Unique("a").ToLowerInvariant());
        var b = await fx.CreateTenantAsync(SqlServerFixture.Unique("B"), SqlServerFixture.Unique("b").ToLowerInvariant());

        await using var scope = fx.TenantScope(a);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();

        var insert = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
            "INSERT INTO audit.TenantAuditLogs (Id, TenantId, OccurredAt, ActorType, ActorId, Action, TargetType, CorrelationId) VALUES (NEWID(), {0}, SYSUTCDATETIME(), 'test', 'x', 'probe', 'probe', 'c')", b.Id));
        Assert.Contains("block predicate", insert.Message, StringComparison.OrdinalIgnoreCase);

        // (audit table: no per-tenant unique key, so the RLS block predicate — not a unique index — is what stops it)
        await db.Database.ExecuteSqlRawAsync(
            "INSERT INTO audit.TenantAuditLogs (Id, TenantId, OccurredAt, ActorType, ActorId, Action, TargetType, CorrelationId) VALUES (NEWID(), {0}, SYSUTCDATETIME(), 'test', 'x', 'probe', 'probe', 'c')", a.Id);
        var moveAway = await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
            "UPDATE audit.TenantAuditLogs SET TenantId = {0} WHERE Action = 'probe'", b.Id));
        Assert.Contains("block predicate", moveAway.Message, StringComparison.OrdinalIgnoreCase);

        // Updates/deletes targeting the other tenant's rows silently affect nothing (filter predicate).
        Assert.Equal(0, await db.Database.ExecuteSqlRawAsync("DELETE FROM system.TenantBranding WHERE TenantId = {0}", b.Id));
    }

    [Fact]
    public async Task Session_context_tenant_cannot_be_changed_by_later_statements()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("A"), SqlServerFixture.Unique("a").ToLowerInvariant());
        var b = await fx.CreateTenantAsync(SqlServerFixture.Unique("B"), SqlServerFixture.Unique("b").ToLowerInvariant());

        await using var scope = fx.TenantScope(a);
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        await db.Database.OpenConnectionAsync();
        try
        {
            var ex = await Assert.ThrowsAsync<SqlException>(() =>
                db.Database.ExecuteSqlRawAsync("EXEC sys.sp_set_session_context @key = N'TenantId', @value = {0}", b.Id));
            Assert.Contains("read_only", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task Pooled_connections_never_carry_a_previous_tenant()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("A"), SqlServerFixture.Unique("a").ToLowerInvariant());
        var b = await fx.CreateTenantAsync(SqlServerFixture.Unique("B"), SqlServerFixture.Unique("b").ToLowerInvariant());

        // Alternate tenants and an unscoped context many times over the same pool: the read-only SESSION_CONTEXT
        // must be reset by sp_reset_connection and re-applied for the new scope every time.
        for (var i = 0; i < 25; i++)
        {
            foreach (var tenant in new[] { a, b })
            {
                await using var scope = fx.TenantScope(tenant);
                var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
                var seen = await db.Database.SqlQueryRaw<Guid>("SELECT TenantId AS Value FROM system.TenantSettings").ToListAsync();
                var ctx = await db.Database.SqlQueryRaw<Guid>("SELECT CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier) AS Value").SingleAsync();
                Assert.Equal([tenant.Id], seen);
                Assert.Equal(tenant.Id, ctx);
            }

            await using var none = fx.UnscopedScope();
            var unscoped = none.ServiceProvider.GetRequiredService<TenancyDbContext>();
            Assert.Empty(await unscoped.Database.SqlQueryRaw<Guid>("SELECT TenantId AS Value FROM system.TenantSettings").ToListAsync());
        }
    }

    [Fact]
    public async Task Interceptor_stamps_tenant_and_rejects_foreign_or_changed_tenant_ids()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("A"), SqlServerFixture.Unique("a").ToLowerInvariant());
        var b = await fx.CreateTenantAsync(SqlServerFixture.Unique("B"), SqlServerFixture.Unique("b").ToLowerInvariant());

        await using (var scope = fx.TenantScope(a))
        {
            var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            var log = TenantAuditLog.Create(DateTimeOffset.UtcNow, "test", "t", "probe", "probe", null, "c", null);
            db.TenantAuditLogs.Add(log);
            await db.SaveChangesAsync();
            Assert.Equal(a.Id, log.TenantId);

            var foreign = TenantAuditLog.Create(DateTimeOffset.UtcNow, "test", "t", "probe", "probe", null, "c", null);
            db.Entry(foreign).Property(nameof(TenantAuditLog.TenantId)).CurrentValue = b.Id;
            db.TenantAuditLogs.Add(foreign);
            await Assert.ThrowsAsync<InvalidOperationException>(() => db.SaveChangesAsync());
            db.Entry(foreign).State = EntityState.Detached;

            var settings = await db.TenantSettings.SingleAsync();
            db.Entry(settings).Property(nameof(TenantSettings.TenantId)).CurrentValue = b.Id;
            await Assert.ThrowsAnyAsync<InvalidOperationException>(() => db.SaveChangesAsync());
        }

        await using (var none = fx.UnscopedScope())
        {
            var db = none.ServiceProvider.GetRequiredService<TenancyDbContext>();
            db.TenantAuditLogs.Add(TenantAuditLog.Create(DateTimeOffset.UtcNow, "test", "t", "probe", "probe", null, "c", null));
            await Assert.ThrowsAsync<TenantNotResolvedException>(() => db.SaveChangesAsync());
        }
    }
}
