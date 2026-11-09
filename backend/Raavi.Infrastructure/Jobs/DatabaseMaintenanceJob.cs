using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Raavi.Infrastructure.Persistence;

namespace Raavi.Infrastructure.Jobs;

/// <summary>
/// Nightly housekeeping that keeps the database small and fast: removes finished outbox events, expired sessions and old error-log entries
/// (retention in <c>Raavi:Maintenance</c>), then refreshes optimizer statistics. Every step is independent and failure-tolerant: a missing
/// permission or a busy table only skips that step and is logged. Never touches page content or anything tenant-owned.
/// </summary>
public sealed partial class DatabaseMaintenanceJob(IConfiguration configuration, ILogger<DatabaseMaintenanceJob> logger)
{
    public const string RecurringJobId = "database-maintenance";

    private readonly int _outboxDays = Math.Max(1, configuration.GetValue("Raavi:Maintenance:OutboxRetentionDays", 14));
    private readonly int _sessionDays = Math.Max(1, configuration.GetValue("Raavi:Maintenance:SessionRetentionDays", 30));
    private readonly int _errorDays = Math.Max(1, configuration.GetValue("Raavi:Maintenance:ErrorLogRetentionDays", 90));

    public async Task RunAsync(CancellationToken ct)
    {
        await using var connection = new SqlConnection(configuration.GetSystemConnectionString());
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await StepAsync(connection, "outbox", "DELETE TOP (5000) FROM content.Outbox WHERE ProcessedAt IS NOT NULL AND ProcessedAt < DATEADD(day, -@days, SYSUTCDATETIME())", _outboxDays, ct).ConfigureAwait(false);
        await StepAsync(connection, "sessions", "DELETE TOP (5000) FROM identity.AuthenticationSessions WHERE (RevokedAt IS NOT NULL AND RevokedAt < DATEADD(day, -@days, SYSUTCDATETIME())) OR AbsoluteExpiresAt < DATEADD(day, -@days, SYSUTCDATETIME())", _sessionDays, ct).ConfigureAwait(false);
        await StepAsync(connection, "error log", "DELETE TOP (5000) FROM system.ErrorLog WHERE OccurredAt < DATEADD(day, -@days, SYSUTCDATETIME())", _errorDays, ct).ConfigureAwait(false);

        try
        {
            await using var stats = new SqlCommand("EXEC sp_updatestats", connection) { CommandTimeout = 600 };
            await stats.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            LogStatsDone(logger);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, "statistics", ex);
        }
    }

    private async Task StepAsync(SqlConnection connection, string name, string sql, int days, CancellationToken ct)
    {
        var total = 0;
        try
        {
            // in slices of 5 000 rows, so a large backlog never holds a long lock
            for (var round = 0; round < 40; round++)
            {
                await using var command = new SqlCommand(sql, connection) { CommandTimeout = 120 };
                command.Parameters.AddWithValue("@days", days);
                var deleted = await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                total += deleted;
                if (deleted < 5000)
                {
                    break;
                }
            }

            LogStepDone(logger, name, total, days);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogStepFailed(logger, name, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Maintenance: removed {Count} old {Step} rows (older than {Days} days).")]
    private static partial void LogStepDone(ILogger logger, string step, int count, int days);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Maintenance step {Step} was skipped.")]
    private static partial void LogStepFailed(ILogger logger, string step, Exception ex);

    [LoggerMessage(Level = LogLevel.Information, Message = "Maintenance: statistics refreshed.")]
    private static partial void LogStatsDone(ILogger logger);
}
