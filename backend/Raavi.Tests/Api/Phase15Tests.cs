using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using System.Net.Http.Json;
using System.Text.Json;
using Raavi.Infrastructure.Http;
using Raavi.Tests.Infrastructure;
using Raavi.Tests.Wiki;
using Raavi.Wiki.Persistence;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Api;

public sealed class Phase15UnitTests
{
    [Fact]
    public void Page_bodies_are_compressed_and_read_back_exactly()
    {
        var json = "{\"doc\":{\"type\":\"doc\",\"content\":[{\"type\":\"paragraph\",\"content\":[{\"type\":\"text\",\"text\":\"" + string.Concat(Enumerable.Repeat("سلام دنیا — hello world ", 400)) + "\"}]}]}}";
        var bytes = GzipText.Compress(json);
        Assert.True(bytes.Length < json.Length * 2 / 8, $"{bytes.Length} bytes for {json.Length * 2}"); // far smaller than the UTF-16 text
        Assert.Equal(json, GzipText.Decompress(bytes));
        Assert.Equal(string.Empty, GzipText.Decompress(GzipText.Compress(string.Empty)));
        // plain UTF-16 (a half-migrated row) is still readable
        Assert.Equal("plain", GzipText.Decompress(System.Text.Encoding.Unicode.GetBytes("plain")));
    }

    [Fact]
    public void Secrets_never_reach_the_error_log_and_long_text_is_cut()
    {
        var cleaned = ErrorText.Clean("Cannot open Server=db;Database=Raavi;User Id=sa;Password=Sup3r-Secret!;Encrypt=True; and Authorization: Bearer abcdefghijklmnop.qrstuv", 1000);
        Assert.DoesNotContain("Sup3r-Secret", cleaned, StringComparison.Ordinal);
        Assert.DoesNotContain("abcdefghijklmnop", cleaned, StringComparison.Ordinal);
        Assert.Contains("Password=***", cleaned, StringComparison.Ordinal);
        Assert.Equal(11, ErrorText.Clean(new string('x', 50), 10).Length); // 10 + the ellipsis
        Assert.Equal(string.Empty, ErrorText.Clean(null, 10));
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class Phase15Tests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private async Task<Browser> SystemBrowserAsync()
    {
        var b = new Browser(fx.Api, SqlServerFixture.SystemHost);
        await b.PrimeAsync();
        return b;
    }

    [Fact]
    public async Task Page_bodies_are_stored_as_gzip_in_a_binary_column()
    {
        using var scope = fx.UnscopedScope();
        var db = scope.ServiceProvider.GetRequiredService<Raavi.Tenancy.Persistence.TenancyDbContext>();
        var type = await db.Database.SqlQueryRaw<string>("SELECT DATA_TYPE AS Value FROM INFORMATION_SCHEMA.COLUMNS WHERE TABLE_SCHEMA = 'content' AND TABLE_NAME = 'PageVersions' AND COLUMN_NAME = 'ContentJson'").SingleAsync();
        Assert.Equal("varbinary", type);

        // And what the application reads back is the text that was written (round trip through SQL Server).
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = await CreatePageAsync(admin, key, "Compressed", body: Doc("alpha", "بتا"));
        var read = await admin.GetJsonAsync($"api/v1/content/{page.GetProperty("id").GetGuid()}");
        Assert.Equal("بتا", read.GetProperty("body").GetProperty("doc").GetProperty("content")[1].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Errors_reported_by_browsers_land_in_the_log_where_system_administrators_search_group_and_clear_them()
    {
        var token = SqlServerFixture.Unique("E").ToLowerInvariant();
        using var browser = await SystemBrowserAsync();
        for (var i = 0; i < 2; i++)
        {
            using var r = await browser.PostAsync("api/v1/errors/client", new { message = $"Cannot read x of undefined ({token}) #{i}", stack = $"TypeError: boom\n    at Foo (app.js:{i}:1)", url = "/spaces/ENG/pages/abc", type = "TypeError" });
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }

        using var key = fx.Api.ForSystemAdmin();
        JsonElement list = default;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            list = (await key.GetFromJsonAsync<JsonElement>($"api/v1/system/errors?q={token}", Browser.Json));
            if (list.GetProperty("total").GetInt32() >= 2)
            {
                break;
            }

            await Task.Delay(250); // written by a background task
        }

        Assert.Equal(2, list.GetProperty("total").GetInt32());
        var first = list.GetProperty("items")[0];
        Assert.Equal("client", first.GetProperty("source").GetString());
        Assert.Equal("TypeError", first.GetProperty("exceptionType").GetString());

        // Search looks in every field: type, path, stack.
        foreach (var term in new[] { "undefined", "TypeError", "/pages/abc" })
        {
            var hits = await key.GetFromJsonAsync<JsonElement>($"api/v1/system/errors?q={Uri.EscapeDataString(term)}&source=client", Browser.Json);
            Assert.True(hits.GetProperty("total").GetInt32() >= 2, term);
        }

        var stackHits = await key.GetFromJsonAsync<JsonElement>("api/v1/system/errors?q=app.js", Browser.Json);
        Assert.True(stackHits.GetProperty("total").GetInt32() >= 2);
        var none = await key.GetFromJsonAsync<JsonElement>($"api/v1/system/errors?q={token}zzz", Browser.Json);
        Assert.Equal(0, none.GetProperty("total").GetInt32());
        var wrongSource = await key.GetFromJsonAsync<JsonElement>($"api/v1/system/errors?q={token}&source=server", Browser.Json);
        Assert.Equal(0, wrongSource.GetProperty("total").GetInt32());

        // Detail with the stack; the two reports differ in text only by digits, so they share one fingerprint.
        var id = first.GetProperty("id").GetGuid();
        var detail = await key.GetFromJsonAsync<JsonElement>($"api/v1/system/errors/{id}", Browser.Json);
        Assert.Contains("at Foo", detail.GetProperty("stackTrace").GetString(), StringComparison.Ordinal);
        Assert.True(detail.GetProperty("sameCount").GetInt32() >= 2);
        var summary = await key.GetFromJsonAsync<JsonElement>("api/v1/system/errors/summary?days=3", Browser.Json);
        Assert.True(summary.GetProperty("client").GetInt32() >= 2);
        Assert.Equal(3, summary.GetProperty("days").GetArrayLength());
        Assert.Contains(summary.GetProperty("groups").EnumerateArray(), g => g.GetProperty("count").GetInt32() >= 2);

        // Only system administrators read the log: anonymous and workspace users do not.
        using var anonymous = fx.Api.ForHost(SqlServerFixture.SystemHost);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("api/v1/system/errors")).StatusCode);
        var (_, tenantAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        Assert.True((await tenantAdmin.GetAsync("api/v1/system/errors")).StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized);

        using var purge = await key.PostAsJsonAsync("api/v1/system/errors/purge", new { olderThanDays = 0 });
        Assert.True(purge.IsSuccessStatusCode);
        Assert.Equal(0, (await key.GetFromJsonAsync<JsonElement>($"api/v1/system/errors?q={token}", Browser.Json)).GetProperty("total").GetInt32());
    }

    [Fact]
    public async Task The_activity_log_is_for_administrators_searchable_by_every_field_and_the_stats_count_what_the_viewer_may_see()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin, name: "Engineering handbook")).GetProperty("key").GetString()!;
        await CreatePageAsync(admin, key, "Quarterly planning");
        await CreatePageAsync(admin, key, "Incident review");
        await _kit.DispatchOutboxAsync();

        var all = await admin.GetJsonAsync("api/v1/tenant/activity-log?take=100");
        Assert.True(all.GetProperty("total").GetInt32() >= 3);
        Assert.Contains("content.created", all.GetProperty("kinds").EnumerateArray().Select(k => k.GetString()));

        async Task<int> Count(string query) => (await admin.GetJsonAsync($"api/v1/tenant/activity-log?{query}")).GetProperty("total").GetInt32();
        Assert.Equal(1, await Count("q=Quarterly"));                        // title
        Assert.True(await Count("q=content.created") >= 2);                // kind
        Assert.True(await Count($"q={key}") >= 3);                          // space key
        Assert.True(await Count("q=handbook") >= 3);                        // space name
        Assert.Equal(0, await Count("q=nothing-like-this"));
        Assert.True(await Count("kind=content") >= 2);                      // prefix of the kind
        Assert.True(await Count($"space={key}&q=Incident") == 1);
        Assert.Equal(0, await Count($"from={Uri.EscapeDataString(DateTimeOffset.UtcNow.AddDays(1).ToString("o"))}"));
        var named = await admin.GetJsonAsync("api/v1/tenant/activity-log?q=Admin&take=5"); // the person's display name
        Assert.True(named.GetProperty("total").GetInt32() >= 3);

        // Members cannot read the administrators' log, but see statistics of what they may view.
        using (var denied = await member.GetAsync("api/v1/tenant/activity-log"))
        {
            Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        }

        var stats = await admin.GetJsonAsync("api/v1/activity/stats?days=7");
        Assert.True(stats.GetProperty("total").GetInt32() >= 3);
        Assert.Equal(7, stats.GetProperty("days").GetArrayLength());
        Assert.Equal(24, stats.GetProperty("byHour").GetArrayLength());
        Assert.NotEmpty(stats.GetProperty("topActors").EnumerateArray());
        Assert.DoesNotContain("Quarterly", stats.ToString(), StringComparison.Ordinal); // counts and names only, never titles
        var memberStats = await member.GetJsonAsync("api/v1/activity/stats?days=7");
        Assert.True(memberStats.GetProperty("total").GetInt32() >= 3); // the space is open to every member
        Assert.DoesNotContain("Quarterly", memberStats.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_nightly_maintenance_runs_against_the_real_schema_and_removes_only_old_rows()
    {
        var config = fx.Api.Services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var job = new Raavi.Infrastructure.Jobs.DatabaseMaintenanceJob(config, Microsoft.Extensions.Logging.Abstractions.NullLogger<Raavi.Infrastructure.Jobs.DatabaseMaintenanceJob>.Instance);
        await job.RunAsync(CancellationToken.None); // every statement must match the schema: a wrong column name would only be logged, so check the effect below too

        using var scope = fx.UnscopedScope();
        var db = scope.ServiceProvider.GetRequiredService<Raavi.Tenancy.Persistence.TenancyDbContext>();
        await db.Database.ExecuteSqlRawAsync("INSERT INTO system.ErrorLog (Id, OccurredAt, Source, Message, Fingerprint) VALUES (NEWID(), DATEADD(day, -400, SYSUTCDATETIME()), 'server', 'ancient', 'aaaaaaaaaaaaaaaa'), (NEWID(), SYSUTCDATETIME(), 'server', 'fresh-maintenance-test', 'bbbbbbbbbbbbbbbb')");
        await job.RunAsync(CancellationToken.None);
        Assert.Equal(0, await db.ErrorLog.CountAsync(e => e.Message == "ancient"));
        Assert.Equal(1, await db.ErrorLog.CountAsync(e => e.Message == "fresh-maintenance-test"));
    }

    [Fact]
    public async Task Security_events_are_searchable_and_carry_names()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (_, email, memberId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        using (var r = await admin.SendAsync(HttpMethod.Put, $"api/v1/tenant/members/{memberId}/password", JsonContent.Create(new { newPassword = "Another-Strong-Passw0rd!" }), withXsrf: true))
        {
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }

        var page = await admin.GetJsonAsync("api/v1/tenant/security-events/search?q=password_reset");
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        var row = page.GetProperty("items")[0];
        Assert.Equal("member.password_reset", row.GetProperty("eventType").GetString());
        Assert.Equal("Member", row.GetProperty("userName").GetString());
        Assert.Equal("Admin", row.GetProperty("actorName").GetString());
        Assert.Contains("member.password_reset", page.GetProperty("types").EnumerateArray().Select(t => t.GetString()));

        Assert.True((await admin.GetJsonAsync("api/v1/tenant/security-events/search?q=Member")).GetProperty("total").GetInt32() >= 1); // by the person's name
        Assert.Equal(0, (await admin.GetJsonAsync("api/v1/tenant/security-events/search?q=zzz-none")).GetProperty("total").GetInt32());
        Assert.NotEmpty(email);
    }
}
