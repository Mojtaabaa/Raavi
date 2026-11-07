using Microsoft.EntityFrameworkCore.Migrations;

namespace Raavi.Infrastructure.Persistence;

/// <summary>
/// Migration helpers for SQL Server Row-Level Security (ADR-005, TENANT_ISOLATION.md §3).
/// The policy has <b>no bypass</b>: system operations on tenant data run inside an explicit tenant scope.
/// </summary>
public static class TenantIsolationMigrationExtensions
{
    public const string PolicyName = "security.TenantIsolation";

    /// <summary>Creates the security schema, predicate function and an (initially empty) policy. Call once, in the first migration.</summary>
    public static void CreateTenantIsolationPolicy(this MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql("IF SCHEMA_ID(N'security') IS NULL EXEC(N'CREATE SCHEMA security');");
        migrationBuilder.Sql("""
            CREATE FUNCTION security.fn_tenant_predicate(@TenantId uniqueidentifier)
            RETURNS TABLE
            WITH SCHEMABINDING
            AS
            RETURN SELECT 1 AS allowed
            WHERE @TenantId = CAST(SESSION_CONTEXT(N'TenantId') AS uniqueidentifier)
              AND @TenantId <> CAST(0x0 AS uniqueidentifier);
            """);
    }

    /// <summary>Protects a tenant-owned table: filter predicate (reads, updates, deletes) + block predicates (inserts, updates).</summary>
    public static void AddTenantIsolation(this MigrationBuilder migrationBuilder, string schema, string table, bool createPolicy = false)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        var target = $"[{schema}].[{table}]";
        var predicates = $"""
            ADD FILTER PREDICATE security.fn_tenant_predicate(TenantId) ON {target},
            ADD BLOCK PREDICATE security.fn_tenant_predicate(TenantId) ON {target} AFTER INSERT,
            ADD BLOCK PREDICATE security.fn_tenant_predicate(TenantId) ON {target} AFTER UPDATE
            """;
        migrationBuilder.Sql(createPolicy
            ? $"CREATE SECURITY POLICY {PolicyName} {predicates} WITH (STATE = ON);"
            : $"ALTER SECURITY POLICY {PolicyName} {predicates};");
    }

    public static void DropTenantIsolation(this MigrationBuilder migrationBuilder, string schema, string table)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        var target = $"[{schema}].[{table}]";
        migrationBuilder.Sql($"""
            ALTER SECURITY POLICY {PolicyName}
            DROP FILTER PREDICATE ON {target},
            DROP BLOCK PREDICATE ON {target} AFTER INSERT,
            DROP BLOCK PREDICATE ON {target} AFTER UPDATE;
            """);
    }

    public static void DropTenantIsolationPolicy(this MigrationBuilder migrationBuilder)
    {
        ArgumentNullException.ThrowIfNull(migrationBuilder);
        migrationBuilder.Sql($"IF OBJECT_ID(N'{PolicyName}') IS NOT NULL DROP SECURITY POLICY {PolicyName};");
        migrationBuilder.Sql("IF OBJECT_ID(N'security.fn_tenant_predicate') IS NOT NULL DROP FUNCTION security.fn_tenant_predicate;");
    }
}
