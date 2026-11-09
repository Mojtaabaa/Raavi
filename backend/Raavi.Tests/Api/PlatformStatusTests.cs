using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Raavi.Infrastructure.Http;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Api;

public sealed class RequestMetricsTests
{
    private static readonly DateTimeOffset T = new(2026, 11, 9, 12, 0, 30, TimeSpan.Zero);

    [Fact]
    public void Counts_errors_and_latency_per_minute_and_estimates_the_p95_from_a_histogram()
    {
        var m = new RequestMetrics();
        for (var i = 0; i < 95; i++)
        {
            m.Record(200, 10, T);
        }

        for (var i = 0; i < 5; i++)
        {
            m.Record(i == 0 ? 500 : 200, 3000, T);
        }

        m.Record(401, 5, T); // expected failures of sign-in and unknown pages are not "errors"
        m.Record(404, 5, T);
        m.Record(400, 5, T);
        var s = m.Snapshot(T);
        Assert.Equal(103, s.Requests);
        Assert.Equal((1, 1), (s.ServerErrors, s.ClientErrors));
        Assert.Equal(25, s.P95Ms); // 95 % of the requests were ≤ 25 ms
        Assert.Equal(3000, s.MaxMs);
        Assert.Equal(60, s.Series.Count);
        Assert.Equal(103, s.Series[^1].Requests);
        Assert.All(s.Series.Take(59), p => Assert.Equal(0, p.Requests));
    }

    [Fact]
    public void Minutes_roll_over_and_old_minutes_are_forgotten()
    {
        var m = new RequestMetrics();
        m.Record(200, 10, T);
        Assert.Equal(1, m.Snapshot(T.AddMinutes(30)).Requests); // still inside the hour
        Assert.Equal(0, m.Snapshot(T.AddMinutes(61)).Requests); // fell out of the window
        m.Record(200, 10, T.AddMinutes(60)); // same slot as T: replaces it, does not add to it
        Assert.Equal(1, m.Snapshot(T.AddMinutes(60)).Requests);
    }

    [Fact]
    public void An_empty_window_is_all_zero()
    {
        var s = new RequestMetrics().Snapshot(T, 5);
        Assert.Equal((0, 0d, 0d, 5), (s.Requests, s.AverageMs, s.P95Ms, s.Series.Count));
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class PlatformStatusTests(SqlServerFixture fx)
{
    [Fact]
    public async Task The_status_answer_is_public_on_every_host_and_contains_nothing_sensitive()
    {
        foreach (var host in new[] { SqlServerFixture.SystemHost, "no-such-workspace." + SqlServerFixture.BaseDomain })
        {
            using var client = fx.Api.ForHost(host);
            using var response = await client.GetAsync("health/status");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
            var text = await response.Content.ReadAsStringAsync();
            var json = JsonDocument.Parse(text).RootElement;

            Assert.Contains(json.GetProperty("status").GetString(), new[] { "Healthy", "Degraded", "Unhealthy" });
            var names = json.GetProperty("components").EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToArray();
            Assert.Contains("self", names);
            Assert.Contains("sql", names);
            Assert.Contains("workers", names);
            Assert.Equal(60, json.GetProperty("traffic").GetProperty("series").GetArrayLength());

            // Nothing that identifies the installation: no connection string, server, path or user.
            foreach (var secret in new[] { "Server=", "Password", "Integrated Security", "localdb", "C:\\", "sandbox", "Exception", "StackTrace" })
            {
                Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    [Fact]
    public async Task Status_requests_are_not_counted_as_traffic_but_real_requests_are()
    {
        using var client = fx.Api.ForHost(SqlServerFixture.SystemHost);
        async Task<int> Requests()
        {
            using var r = await client.GetAsync("health/status");
            return (await r.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("traffic").GetProperty("requests").GetInt32();
        }

        var before = await Requests();
        Assert.Equal(before, await Requests()); // polling the status page does not inflate the numbers it shows
        using (await client.GetAsync("api/v1/host/context"))
        {
        }

        Assert.Equal(before + 1, await Requests());
    }

    [Fact]
    public async Task The_public_status_can_be_switched_off()
    {
        await using var off = fx.Api.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?> { ["Raavi:Health:PublicStatus"] = "false" })));
        using var client = off.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{SqlServerFixture.SystemHost}/"), AllowAutoRedirect = false });
        using var response = await client.GetAsync("health/status");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        using var live = await client.GetAsync("health/live"); // probes are not affected
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
    }
}
