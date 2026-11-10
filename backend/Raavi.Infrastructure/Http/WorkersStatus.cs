using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Persistence;

namespace Raavi.Infrastructure.Http;

/// <summary>
/// "Is the RaaviWorkers service running?" for administrators (ADR-051). Hangfire servers write a heartbeat every ~30 s into <c>hangfire.Server</c>;
/// a heartbeat younger than two minutes means the service is alive. Also the queue picture (waiting / running / failed jobs) so a stopped service
/// is visible as a growing backlog. Read-only; no job content is returned.
/// </summary>
public static class WorkersStatus
{
    public static readonly TimeSpan Alive = TimeSpan.FromMinutes(2);

    public sealed record ServerInfo(string Name, DateTimeOffset? StartedAt, DateTimeOffset LastHeartbeat, int Workers, bool Alive);

    public sealed record Report(string State, DateTimeOffset CheckedAt, DateTimeOffset? LastHeartbeat, IReadOnlyList<ServerInfo> Servers, IReadOnlyDictionary<string, int> Jobs);

    public static async Task<Report> ReadAsync(IConfiguration configuration, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(configuration.GetSystemConnectionString()) { ConnectTimeout = 3 }.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var servers = new List<ServerInfo>();
        await using (var command = new SqlCommand("SELECT Id, Data, LastHeartbeat FROM hangfire.Server", connection) { CommandTimeout = 5 })
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var beat = new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc));
                DateTimeOffset? started = null;
                var workers = 0;
                try
                {
                    using var doc = JsonDocument.Parse(reader.GetString(1));
                    if (doc.RootElement.TryGetProperty("StartedAt", out var s) && s.TryGetDateTimeOffset(out var at))
                    {
                        started = at;
                    }

                    if (doc.RootElement.TryGetProperty("WorkerCount", out var w) && w.TryGetInt32(out var n))
                    {
                        workers = n;
                    }
                }
                catch (JsonException)
                {
                    // unreadable server data only means "no details"
                }

                // the server id is "machine:pid:guid": show the machine only (never the process id)
                var name = reader.GetString(0).Split(':')[0];
                servers.Add(new ServerInfo(name, started, beat, workers, now - beat <= Alive));
            }
        }

        var jobs = new Dictionary<string, int>();
        await using (var command = new SqlCommand("SELECT StateName, COUNT(*) FROM hangfire.Job WHERE StateName IN ('Enqueued','Processing','Scheduled','Failed') GROUP BY StateName", connection) { CommandTimeout = 5 })
        await using (var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                jobs[reader.GetString(0)] = reader.GetInt32(1);
            }
        }

        var last = servers.Count == 0 ? (DateTimeOffset?)null : servers.Max(x => x.LastHeartbeat);
        var state = servers.Any(x => x.Alive) ? "Running" : servers.Count == 0 ? "NeverStarted" : "Stopped";
        return new Report(state, now, last, servers, jobs);
    }

    public static void MapWorkersStatus(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);

        async Task<IResult> Handler(IConfiguration configuration, TimeProvider time, HttpContext http, CancellationToken ct)
        {
            http.Response.Headers.CacheControl = "no-store";
            try
            {
                return Results.Ok(await ReadAsync(configuration, time.GetUtcNow(), ct).ConfigureAwait(false));
            }
            catch (SqlException)
            {
                return Results.Ok(new Report("Unknown", time.GetUtcNow(), null, [], new Dictionary<string, int>()));
            }
        }

        api.MapGet("/system/workers", Handler).RequireHostKind(RaaviHostKind.System).RequireAuthorization(RaaviPolicies.SystemAdministrator);
        api.MapGet("/tenant/workers", Handler).RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.TenantAdministrator);
    }
}
