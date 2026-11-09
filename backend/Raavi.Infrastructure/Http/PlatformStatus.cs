using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Raavi.Infrastructure.Persistence;

namespace Raavi.Infrastructure.Http;

/// <summary>
/// Request counters of the last hour in one-minute buckets (in memory, per process): how many requests, how many failed (5xx) and how long
/// they took. Feeds the status page; nothing is stored and no request content is kept.
/// </summary>
public sealed class RequestMetrics
{
    public const int Minutes = 60;

    /// <summary>Upper bounds (ms) of the latency histogram; the last bucket is "slower than all of these".</summary>
    public static readonly int[] Bounds = [25, 50, 100, 250, 500, 1000, 2500, 5000];

    private readonly Lock _gate = new();
    private readonly Bucket[] _buckets = new Bucket[Minutes];

    private sealed class Bucket
    {
        public long Minute;
        public int Count;
        public int ClientErrors;
        public int ServerErrors;
        public double TotalMs;
        public double MaxMs;
        public readonly int[] Histogram = new int[Bounds.Length + 1];
    }

    public void Record(int status, double ms, DateTimeOffset now)
    {
        var minute = now.ToUnixTimeSeconds() / 60;
        lock (_gate)
        {
            var slot = (int)(minute % Minutes);
            var b = _buckets[slot];
            if (b is null || b.Minute != minute)
            {
                b = _buckets[slot] = new Bucket { Minute = minute };
            }

            b.Count++;
            b.TotalMs += ms;
            b.MaxMs = Math.Max(b.MaxMs, ms);
            if (status >= 500)
            {
                b.ServerErrors++;
            }
            else if (status >= 400 && status != 401 && status != 404)
            {
                b.ClientErrors++;
            }

            var i = Array.FindIndex(Bounds, x => ms <= x);
            b.Histogram[i < 0 ? Bounds.Length : i]++;
        }
    }

    public sealed record Point(DateTimeOffset Minute, int Requests, int Errors, double AverageMs, double MaxMs);

    public sealed record Summary(int Requests, int ServerErrors, int ClientErrors, double AverageMs, double P95Ms, double MaxMs, IReadOnlyList<Point> Series);

    /// <summary>The last <paramref name="minutes"/> minutes (oldest first, empty minutes included) and their totals.</summary>
    public Summary Snapshot(DateTimeOffset now, int minutes = Minutes)
    {
        minutes = Math.Clamp(minutes, 1, Minutes);
        var current = now.ToUnixTimeSeconds() / 60;
        var series = new List<Point>(minutes);
        var histogram = new int[Bounds.Length + 1];
        int count = 0, server = 0, client = 0;
        double total = 0, max = 0;
        lock (_gate)
        {
            for (var m = current - minutes + 1; m <= current; m++)
            {
                var b = _buckets[(int)(m % Minutes)];
                var at = DateTimeOffset.FromUnixTimeSeconds(m * 60);
                if (b is null || b.Minute != m)
                {
                    series.Add(new Point(at, 0, 0, 0, 0));
                    continue;
                }

                series.Add(new Point(at, b.Count, b.ServerErrors, b.Count == 0 ? 0 : Math.Round(b.TotalMs / b.Count, 1), Math.Round(b.MaxMs, 1)));
                count += b.Count;
                server += b.ServerErrors;
                client += b.ClientErrors;
                total += b.TotalMs;
                max = Math.Max(max, b.MaxMs);
                for (var i = 0; i < histogram.Length; i++)
                {
                    histogram[i] += b.Histogram[i];
                }
            }
        }

        // p95 from the histogram: the upper bound of the bucket that holds the 95th percentile (an estimate, never below the truth).
        double p95 = 0;
        if (count > 0)
        {
            var target = Math.Ceiling(count * 0.95);
            var seen = 0;
            for (var i = 0; i < histogram.Length; i++)
            {
                seen += histogram[i];
                if (seen >= target)
                {
                    p95 = i < Bounds.Length ? Bounds[i] : Math.Max(max, Bounds[^1]);
                    break;
                }
            }
        }

        return new Summary(count, server, client, count == 0 ? 0 : Math.Round(total / count, 1), p95, Math.Round(max, 1), series);
    }
}

internal sealed class RequestMetricsMiddleware(RequestDelegate next, RequestMetrics metrics, TimeProvider time)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path;
        if (path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
        {
            await next(context).ConfigureAwait(false); // the status page polls itself: do not count it
            return;
        }

        var start = Stopwatch.GetTimestamp();
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch
        {
            metrics.Record(500, Stopwatch.GetElapsedTime(start).TotalMilliseconds, time.GetUtcNow());
            throw;
        }

        metrics.Record(context.Response.StatusCode, Stopwatch.GetElapsedTime(start).TotalMilliseconds, time.GetUtcNow());
    }
}

/// <summary>Checks that only the status page uses (they never decide readiness): worker heartbeat, outbox backlog, disk space, SQL speed.</summary>
internal abstract class SqlProbe(IConfiguration configuration)
{
    protected async Task<T> QueryAsync<T>(string sql, Func<SqlDataReader, T> read, CancellationToken ct)
    {
        await using var connection = new SqlConnection(new SqlConnectionStringBuilder(configuration.GetSystemConnectionString()) { ConnectTimeout = 3 }.ConnectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var command = new SqlCommand(sql, connection) { CommandTimeout = 3 };
        await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
        await reader.ReadAsync(ct).ConfigureAwait(false);
        return read(reader);
    }
}

internal sealed class SqlSpeedHealthCheck(IConfiguration configuration) : SqlProbe(configuration), IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        var start = Stopwatch.GetTimestamp();
        try
        {
            await QueryAsync("SELECT 1", r => r.GetInt32(0), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Unhealthy("SQL Server did not answer.");
        }

        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        return ms > 750 ? HealthCheckResult.Degraded("SQL Server answers slowly.") : HealthCheckResult.Healthy("SQL Server answers.");
    }
}

internal sealed class WorkersHealthCheck(IConfiguration configuration) : SqlProbe(configuration), IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var beat = await QueryAsync("SELECT MAX(LastHeartbeat) FROM hangfire.Server", r => r.IsDBNull(0) ? (DateTime?)null : r.GetDateTime(0), cancellationToken).ConfigureAwait(false);
            if (beat is null)
            {
                return HealthCheckResult.Degraded("No worker is running: notifications, activity feed and scheduled jobs are on hold.");
            }

            var age = DateTime.UtcNow - DateTime.SpecifyKind(beat.Value, DateTimeKind.Utc);
            return age > TimeSpan.FromMinutes(2)
                ? HealthCheckResult.Degraded("The worker has not reported for " + (int)age.TotalMinutes + " minutes.")
                : HealthCheckResult.Healthy("The worker is running.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Healthy("Not available.");
        }
    }
}

internal sealed class OutboxBacklogHealthCheck(IConfiguration configuration) : SqlProbe(configuration), IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var (pending, oldest) = await QueryAsync(
                "SELECT COUNT(*), MIN(OccurredAt) FROM content.Outbox WHERE ProcessedAt IS NULL",
                r => (r.GetInt32(0), r.IsDBNull(1) ? (DateTimeOffset?)null : r.GetDateTimeOffset(1)),
                cancellationToken).ConfigureAwait(false);
            if (pending == 0 || oldest is null)
            {
                return HealthCheckResult.Healthy("Nothing waiting.");
            }

            var age = DateTimeOffset.UtcNow - oldest.Value;
            return age > TimeSpan.FromMinutes(5)
                ? HealthCheckResult.Degraded($"{pending} events waiting, the oldest for {(int)age.TotalMinutes} minutes.")
                : HealthCheckResult.Healthy($"{pending} events waiting.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return HealthCheckResult.Healthy("Not available.");
        }
    }
}

internal sealed class DiskSpaceHealthCheck(IHostEnvironment environment) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(environment.ContentRootPath))!);
            var free = (double)drive.AvailableFreeSpace / drive.TotalSize;
            var data = new Dictionary<string, object> { ["freePercent"] = Math.Round(free * 100, 1), ["freeGb"] = Math.Round(drive.AvailableFreeSpace / 1073741824d, 1) };
            // Both the share and the absolute size matter: 3 % of a 2 TB disk is plenty, 3 % of a 20 GB disk is not.
            var gb = drive.AvailableFreeSpace / 1073741824d;
            return Task.FromResult(gb < 1 || free < 0.01
                ? HealthCheckResult.Unhealthy("Almost no disk space left.", data: data)
                : gb < 5 || free < 0.05 ? HealthCheckResult.Degraded("Disk space is running low.", data: data) : HealthCheckResult.Healthy("Enough disk space.", data: data));
        }
        catch (Exception)
        {
            return Task.FromResult(HealthCheckResult.Healthy("Not available."));
        }
    }
}

public static class PlatformStatusExtensions
{
    /// <summary>Tag of checks that appear on the status page only (they never decide readiness).</summary>
    public const string StatusTag = "status";

    private static readonly DateTimeOffset Started = DateTimeOffset.UtcNow;

    /// <summary>The release version written by the package script (<c>release.json</c> next to the program folders), else the assembly version.</summary>
    private static string ResolveVersion(string contentRoot)
    {
        foreach (var up in new[] { "..", Path.Combine("..", "..") })
        {
            try
            {
                var file = Path.GetFullPath(Path.Combine(contentRoot, up, "release.json"));
                if (File.Exists(file))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(file));
                    if (doc.RootElement.TryGetProperty("version", out var v) && v.GetString() is { Length: > 0 and <= 40 } version)
                    {
                        return version;
                    }
                }
            }
            catch (Exception)
            {
                // an unreadable file just means "no release information"
            }
        }

        return typeof(PlatformStatusExtensions).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion?.Split('+')[0] ?? "unknown";
    }

    public static IServiceCollection AddRaaviPlatformStatus(this IServiceCollection services)
    {
        services.AddSingleton<RequestMetrics>();
        services.AddSingleton(TimeProvider.System);
        services.AddHealthChecks()
            .AddCheck<SqlSpeedHealthCheck>("sql_speed", tags: [StatusTag])
            .AddCheck<WorkersHealthCheck>("workers", tags: [StatusTag])
            .AddCheck<OutboxBacklogHealthCheck>("outbox", tags: [StatusTag])
            .AddCheck<DiskSpaceHealthCheck>("disk", tags: [StatusTag]);
        return services;
    }

    public static IApplicationBuilder UseRaaviRequestMetrics(this IApplicationBuilder app) => app.UseMiddleware<RequestMetricsMiddleware>();

    /// <summary>
    /// <c>GET /health/status</c>: the answer behind the status page. Public on purpose (the page must work when nobody can sign in) and limited to
    /// what is harmless: component states with a short fixed description, traffic counters, process size and version. Never connection strings,
    /// host names, paths, user names or request content. Lives under /health so it is reachable on every host and through the same proxy rule.
    /// </summary>
    public static WebApplication MapRaaviStatus(this WebApplication app)
    {
        var releaseVersion = ResolveVersion(app.Environment.ContentRootPath);
        app.MapGet("/health/status", async (Microsoft.Extensions.Diagnostics.HealthChecks.HealthCheckService health, RequestMetrics metrics, TimeProvider time, HttpContext http, CancellationToken ct) =>
        {
            if (!app.Configuration.GetValue("Raavi:Health:PublicStatus", true))
            {
                return Results.NotFound();
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(6));
            var started = Stopwatch.GetTimestamp();
            var report = await health.CheckHealthAsync(_ => true, timeout.Token).ConfigureAwait(false);
            var now = time.GetUtcNow();
            var process = Process.GetCurrentProcess();
            var summary = metrics.Snapshot(now);
            http.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                status = report.Status.ToString(),
                checkedAt = now,
                durationMs = (int)Stopwatch.GetElapsedTime(started).TotalMilliseconds,
                version = releaseVersion,
                environment = app.Environment.EnvironmentName,
                startedAt = Started,
                uptimeSeconds = (long)(now - Started).TotalSeconds,
                components = report.Entries.OrderBy(e => e.Key, StringComparer.Ordinal).Select(e => new
                {
                    name = e.Key,
                    status = e.Value.Status.ToString(),
                    durationMs = (int)e.Value.Duration.TotalMilliseconds,
                    description = e.Value.Description,
                    data = e.Value.Data.Count == 0 ? null : e.Value.Data,
                }),
                process = new
                {
                    memoryMb = (int)(process.WorkingSet64 / 1048576),
                    threads = process.Threads.Count,
                    gcGen2Collections = GC.CollectionCount(2),
                    cpuSeconds = Math.Round(process.TotalProcessorTime.TotalSeconds, 1),
                },
                traffic = new
                {
                    requests = summary.Requests,
                    serverErrors = summary.ServerErrors,
                    clientErrors = summary.ClientErrors,
                    errorRatePercent = summary.Requests == 0 ? 0 : Math.Round(100d * summary.ServerErrors / summary.Requests, 2),
                    averageMs = summary.AverageMs,
                    p95Ms = summary.P95Ms,
                    maxMs = summary.MaxMs,
                    series = summary.Series.Select(p => new { at = p.Minute, requests = p.Requests, errors = p.Errors, averageMs = p.AverageMs }),
                },
            });
        }).AllowAnonymous().DisableHttpMetrics();
        return app;
    }
}
