using System.Diagnostics;
using System.Net;
using System.Text.Json;
using Raavi.Tests.Infrastructure;
using Raavi.Tests.Wiki;
using Xunit.Abstractions;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Performance;

/// <summary>
/// Load baseline (Phase 12c). Runs a realistic read/write mix with many concurrent signed-in users against the in-process API on the test
/// database and prints latency percentiles per endpoint. Switched off unless <c>RAAVI_LOAD=1</c>, because it takes minutes and its numbers depend
/// on the machine: run it with
/// <c>RAAVI_LOAD=1 dotnet test --filter "Category=Load" --logger "console;verbosity=detailed"</c>.
/// Results are written to <c>RAAVI_LOAD_REPORT</c> (JSON) when set. They are a baseline for comparisons, not a guarantee.
/// </summary>
[Collection(SqlTestGroup.Name)]
[Trait("Category", "Load")]
public sealed class LoadTests(SqlServerFixture fx, ITestOutputHelper output)
{
    private readonly WikiTestKit _kit = new(fx);

    private static bool Enabled => Environment.GetEnvironmentVariable("RAAVI_LOAD") == "1";

    private static int Env(string name, int fallback) => int.TryParse(Environment.GetEnvironmentVariable(name), out var v) && v > 0 ? v : fallback;

    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    private sealed record Sample(string Endpoint, double Ms, HttpStatusCode Status);

    [Fact]
    public async Task Browsing_and_editing_mix_under_concurrent_users()
    {
        if (!Enabled)
        {
            output.WriteLine("Load tests are off (set RAAVI_LOAD=1).");
            return;
        }

        var pages = Env("RAAVI_LOAD_PAGES", 1000);
        var users = Env("RAAVI_LOAD_USERS", 50);
        var seconds = Env("RAAVI_LOAD_SECONDS", 20);
        var warmup = Env("RAAVI_LOAD_WARMUP", 8);

        // Data: one tenant, one space, `pages` pages in a three-level tree, `users` members.
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var seedClock = Stopwatch.StartNew();
        var roots = new List<Guid>();
        var all = new System.Collections.Concurrent.ConcurrentBag<Guid>();
        var rootCount = Math.Max(1, pages / 10);
        await Parallel.ForEachAsync(Enumerable.Range(0, rootCount), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
        {
            var id = Id(await CreatePageAsync(admin, key, $"Section {i}", body: Doc($"Section {i} " + new string('x', 400))));
            lock (roots)
            {
                roots.Add(id);
            }

            all.Add(id);
        });
        await Parallel.ForEachAsync(Enumerable.Range(0, pages - rootCount), new ParallelOptions { MaxDegreeOfParallelism = 8 }, async (i, _) =>
        {
            Guid parent;
            lock (roots)
            {
                parent = roots[i % roots.Count];
            }

            all.Add(Id(await CreatePageAsync(admin, key, $"Page {i} about topic {i % 37}", parentId: parent, body: Doc($"Body of page {i}. " + new string('y', 800)))));
        });
        output.WriteLine($"seeded {all.Count} pages in {seedClock.Elapsed.TotalSeconds:F1}s");

        var members = new List<Browser>();
        for (var i = 0; i < users; i++)
        {
            members.Add((await _kit.Identity.AddMemberAsync(tenant, admin)).Member);
        }

        await _kit.DispatchOutboxAsync();
        var ids = all.ToArray();
        var rootIds = roots.ToArray();

        var samples = new System.Collections.Concurrent.ConcurrentBag<Sample>();
        async Task<HttpResponseMessage> Timed(string endpoint, Func<Task<HttpResponseMessage>> call)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var response = await call();
                await response.Content.ReadAsByteArrayAsync();
                samples.Add(new Sample(endpoint, sw.Elapsed.TotalMilliseconds, response.StatusCode));
                return response;
            }
            catch (Exception)
            {
                samples.Add(new Sample(endpoint, sw.Elapsed.TotalMilliseconds, HttpStatusCode.InternalServerError));
                throw;
            }
        }

        async Task Virtual(Browser b, int seed, int duration)
        {
            var rnd = new Random(seed);
            var end = DateTime.UtcNow.AddSeconds(duration);
            while (DateTime.UtcNow < end)
            {
                var page = ids[rnd.Next(ids.Length)];
                var roll = rnd.Next(100);
                if (roll < 25)
                {
                    using var _ = await Timed("GET page", () => b.GetAsync($"api/v1/content/{page}"));
                }
                else if (roll < 45)
                {
                    using var _ = await Timed("GET tree children", () => b.GetAsync($"api/v1/spaces/{key}/tree?parentId={rootIds[rnd.Next(rootIds.Length)]}"));
                }
                else if (roll < 55)
                {
                    using var _ = await Timed("GET space tree (roots)", () => b.GetAsync($"api/v1/spaces/{key}/tree"));
                }
                else if (roll < 70)
                {
                    using var _ = await Timed("GET quick-find", () => b.GetAsync($"api/v1/quick-find?q=topic%20{rnd.Next(37)}"));
                }
                else if (roll < 78)
                {
                    using var _ = await Timed("GET comments", () => b.GetAsync($"api/v1/content/{page}/comments"));
                }
                else if (roll < 86)
                {
                    using var _ = await Timed("GET unread-count", () => b.GetAsync("api/v1/notifications/unread-count"));
                }
                else if (roll < 92)
                {
                    using var _ = await Timed("GET spaces", () => b.GetAsync("api/v1/spaces"));
                }
                else
                {
                    using var _ = await Timed("POST comment", () => b.PostAsync($"api/v1/content/{page}/comments", new { body = new { schema = "raavi.doc", schemaVersion = 2, doc = new { type = "doc", content = new object[] { new { type = "paragraph", content = new object[] { new { type = "text", text = "load comment" } } } } } }, kind = "Page" }));
                }
            }
        }

        // Warm-up (JIT, EF model, caches, connection pool) is not part of the measurement.
        await Task.WhenAll(members.Select((m, i) => Task.Run(() => Virtual(m, 1000 + i, warmup))));
        samples.Clear();
        var wall = Stopwatch.StartNew();
        await Task.WhenAll(members.Select((m, i) => Task.Run(() => Virtual(m, i + 1, seconds))));
        wall.Stop();

        var rows = samples.GroupBy(s => s.Endpoint).OrderBy(g => g.Key).Select(g =>
        {
            var ms = g.Select(s => s.Ms).Order().ToArray();
            double P(double p) => ms[Math.Min(ms.Length - 1, (int)Math.Ceiling(p * ms.Length) - 1)];
            return new
            {
                endpoint = g.Key,
                count = ms.Length,
                p50 = Math.Round(P(0.50), 1),
                p95 = Math.Round(P(0.95), 1),
                p99 = Math.Round(P(0.99), 1),
                max = Math.Round(ms[^1], 1),
                errors = g.Count(s => (int)s.Status >= 500),
                non2xx = g.Count(s => (int)s.Status is < 200 or >= 300),
            };
        }).ToArray();
        var total = samples.Count;
        var summary = new { pages = ids.Length, users, seconds, requests = total, requestsPerSecond = Math.Round(total / wall.Elapsed.TotalSeconds, 1), serverErrors = samples.Count(s => (int)s.Status >= 500), endpoints = rows };
        var json = JsonSerializer.Serialize(summary, Pretty);
        output.WriteLine(json);
        if (Environment.GetEnvironmentVariable("RAAVI_LOAD_REPORT") is { Length: > 0 } path)
        {
            await File.WriteAllTextAsync(path, json);
        }

        Assert.Equal(0, summary.serverErrors); // no 5xx under load
        Assert.True(total > users * 5, "the virtual users made too few requests");
    }
}

/// <summary>
/// Counts the SQL statements one request causes (from SQL Server's own plan-cache statistics), to spot N+1 patterns. Same switch as the load test.
/// The server is otherwise idle while a single request runs, so the difference of <c>execution_count</c> before and after is the request's statement count.
/// </summary>
[Collection(SqlTestGroup.Name)]
[Trait("Category", "Load")]
public sealed class QueryCountTests(SqlServerFixture fx, ITestOutputHelper output)
{
    private readonly WikiTestKit _kit = new(fx);

    private async Task<long> ExecutionsAsync()
    {
        await using var conn = new Microsoft.Data.SqlClient.SqlConnection(fx.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT ISNULL(SUM(CAST(qs.execution_count AS bigint)), 0) FROM sys.dm_exec_query_stats qs CROSS APPLY sys.dm_exec_sql_text(qs.sql_handle) t WHERE t.text NOT LIKE N'%dm_exec_query_stats%" + "'";
        return (long)(await cmd.ExecuteScalarAsync())!;
    }

    [Fact]
    public async Task Hot_endpoints_do_not_issue_one_query_per_item()
    {
        if (Environment.GetEnvironmentVariable("RAAVI_LOAD") != "1")
        {
            return;
        }

        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var root = Id(await CreatePageAsync(admin, key, "Root", body: Doc("root")));
        Guid last = root;
        for (var i = 0; i < 30; i++)
        {
            last = Id(await CreatePageAsync(admin, key, $"Child {i}", parentId: root, body: Doc("child")));
        }

        var deep = root;
        for (var i = 0; i < 6; i++)
        {
            deep = Id(await CreatePageAsync(admin, key, $"Deep {i}", parentId: deep, body: Doc("deep")));
        }

        for (var i = 0; i < 12; i++)
        {
            using var c = await member.PostAsync($"api/v1/content/{deep}/comments", new { body = new { schema = "raavi.doc", schemaVersion = 2, doc = new { type = "doc", content = new object[] { new { type = "paragraph", content = new object[] { new { type = "text", text = "c" } } } } } }, kind = "Page" });
            await c.Content.ReadAsByteArrayAsync();
        }

        var calls = new (string Name, string Url)[]
        {
            ("GET page (6 levels deep, 12 comments)", $"api/v1/content/{deep}"),
            ("GET tree children (31 children)", $"api/v1/spaces/{key}/tree?parentId={root}"),
            ("GET space tree (roots)", $"api/v1/spaces/{key}/tree"),
            ("GET comments (12)", $"api/v1/content/{deep}/comments"),
            ("GET quick-find", "api/v1/quick-find?q=child"),
            ("GET spaces", "api/v1/spaces"),
            ("GET unread-count", "api/v1/notifications/unread-count"),
        };
        foreach (var (_, url) in calls)
        {
            using var warm = await member.GetAsync(url); // plan compilation and caches are not what we count
            await warm.Content.ReadAsByteArrayAsync();
        }

        foreach (var (name, url) in calls)
        {
            long total = 0;
            for (var i = 0; i < 3; i++)
            {
                var before = await ExecutionsAsync();
                using var r = await member.GetAsync(url);
                await r.Content.ReadAsByteArrayAsync();
                total += await ExecutionsAsync() - before;
            }

            output.WriteLine($"{name,-44} ≈ {total / 3.0:F0} statements");
        }
    }
}
