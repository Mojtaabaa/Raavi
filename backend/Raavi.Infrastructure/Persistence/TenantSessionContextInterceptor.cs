using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Raavi.Application.Abstractions.Tenancy;

namespace Raavi.Infrastructure.Persistence;

/// <summary>
/// Sets <c>SESSION_CONTEXT(N'TenantId')</c> as <b>read-only</b> on every physical connection open, so SQL Server
/// Row-Level Security (security.TenantIsolation) filters and blocks rows of other tenants even for raw SQL and bulk
/// operations. Without a resolved tenant the value is <see cref="Guid.Empty"/>, which matches no tenant row.
/// Because the key is read-only, no later statement on the same session can change it.
/// </summary>
public sealed class TenantSessionContextInterceptor(ITenantContext tenantContext) : DbConnectionInterceptor
{
    internal const string Sql = "EXEC sys.sp_set_session_context @key = N'TenantId', @value = @tenantId, @read_only = 1;";

    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        using var command = CreateCommand(connection);
        command.ExecuteNonQuery();
    }

    public override async Task ConnectionOpenedAsync(DbConnection connection, ConnectionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        var command = CreateCommand(connection);
        await using (command.ConfigureAwait(false))
        {
            await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private DbCommand CreateCommand(DbConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var command = connection.CreateCommand();
#pragma warning disable CA2100 // constant SQL, value passed as parameter
        command.CommandText = Sql;
#pragma warning restore CA2100
        command.Parameters.Add(new SqlParameter("@tenantId", SqlDbType.UniqueIdentifier)
        {
            Value = tenantContext.IsResolved ? tenantContext.Tenant.TenantId : Guid.Empty,
        });
        return command;
    }
}
