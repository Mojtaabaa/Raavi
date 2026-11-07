using Microsoft.Data.SqlClient;
using Raavi.Infrastructure.Configuration;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Isolation;

/// <summary>
/// Verifies deployment/scripts/sql/01-database-roles.sql against the real migrated schema: the runtime role can read
/// and write data (still subject to RLS) but cannot alter tables, schemas or the tenant security policy.
/// </summary>
[Collection(SqlTestGroup.Name)]
public sealed class DatabaseRolesTests(SqlServerFixture fx)
{
    /// <summary>SQL Server permission-denied error numbers.</summary>
    private static readonly int[] PermissionErrors = [229, 262, 297, 300, 1088, 2760, 3701, 15151, 15247, 33268]; // 33268: "does not exist or you do not have permissions" (security policies)

    private static string Script()
    {
        var sandbox = SandboxConfiguration.ResolvePath(AppContext.BaseDirectory, null);
        var repoRoot = Directory.GetParent(sandbox)!.Parent!.FullName;
        return File.ReadAllText(Path.Combine(repoRoot, "deployment", "scripts", "sql", "01-database-roles.sql"));
    }

    private static async Task<object?> ScalarAsync(SqlConnection c, string sql)
    {
#pragma warning disable CA2100 // test-only, constant SQL
        await using var cmd = new SqlCommand(sql, c);
#pragma warning restore CA2100
        return await cmd.ExecuteScalarAsync();
    }

    [Fact]
    public async Task Runtime_role_has_data_access_but_cannot_change_schema_or_rls()
    {
        var tenant = await fx.CreateTenantAsync(SqlServerFixture.Unique("R"), SqlServerFixture.Unique("r").ToLowerInvariant());
        await using var c = new SqlConnection(fx.ConnectionString);
        await c.OpenAsync();
        await ScalarAsync(c, Script());
        await ScalarAsync(c, """
            IF DATABASE_PRINCIPAL_ID(N'raavi_app_test') IS NULL CREATE USER raavi_app_test WITHOUT LOGIN;
            ALTER ROLE raavi_app_role ADD MEMBER raavi_app_test;
            """);
        await ScalarAsync(c, Script()); // idempotent

        await ScalarAsync(c, "EXECUTE AS USER = N'raavi_app_test';");
        try
        {
            // Data access works, and RLS still hides tenant rows without a session tenant.
            Assert.True((int)(await ScalarAsync(c, "SELECT COUNT(*) FROM system.Tenants"))! > 0);
            Assert.Equal(0, (int)(await ScalarAsync(c, "SELECT COUNT(*) FROM system.TenantSettings"))!);

            foreach (var forbidden in new[]
            {
                "ALTER SECURITY POLICY security.TenantIsolation WITH (STATE = OFF);",
                "DROP SECURITY POLICY security.TenantIsolation;",
                "ALTER TABLE system.Tenants ADD Hack int NULL;",
                "CREATE TABLE system.Hack (Id int);",
                "DROP TABLE [identity].[TenantMembers];", // IDENTITY is a reserved word: always bracket this schema in raw SQL
                "CREATE FUNCTION security.fn_bypass() RETURNS TABLE AS RETURN SELECT 1 AS x;",
            })
            {
                var ex = await Assert.ThrowsAsync<SqlException>(() => ScalarAsync(c, forbidden));
                Assert.Contains(ex.Number, PermissionErrors);
            }

            // With a tenant in the session, exactly that tenant's row becomes visible.
            await ScalarAsync(c, $"EXEC sys.sp_set_session_context @key = N'TenantId', @value = '{tenant.Id}', @read_only = 1;");
            Assert.Equal(1, (int)(await ScalarAsync(c, "SELECT COUNT(*) FROM system.TenantSettings"))!);
        }
        finally
        {
            await ScalarAsync(c, "REVERT;");
        }

        // The policy is still enabled.
        Assert.Equal(true, await ScalarAsync(c, "SELECT is_enabled FROM sys.security_policies WHERE name = 'TenantIsolation'"));
    }
}
