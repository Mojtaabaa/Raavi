using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Messaging;
using Raavi.Infrastructure.Tenancy;
using Raavi.Integrations.Persistence;
using Raavi.Integrations.Webhooks;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 11d: host policy, signing and payload of outbound webhooks — no SQL needed.</summary>
public sealed class WebhookUnitTests
{
    [Fact]
    public void The_host_policy_accepts_only_listed_hosts_and_never_loopback_or_metadata_addresses()
    {
        var policy = new WebhookHostPolicy(["hooks.example.test", "*.corp.internal", "10.0.0.7"]);
        Assert.NotNull(policy.TryValidate("https://hooks.example.test/in?x=1", out _));
        Assert.NotNull(policy.TryValidate("http://erp.corp.internal:8080/hook", out _));
        Assert.NotNull(policy.TryValidate("http://10.0.0.7/hook", out _));
        foreach (var (url, code) in new[]
        {
            ("https://evil.example.test/", "webhook.host_not_allowed"),
            ("https://corp.internal/", "webhook.host_not_allowed"),
            ("ftp://hooks.example.test/", "webhook.url_invalid"),
            ("https://u:p@hooks.example.test/", "webhook.url_invalid"),
            ("https://hooks.example.test/#frag", "webhook.url_invalid"),
            ("nonsense", "webhook.url_invalid"),
        })
        {
            Assert.Null(policy.TryValidate(url, out var got));
            Assert.Equal(code, got);
        }

        var open = new WebhookHostPolicy(["127.0.0.1", "169.254.169.254"]);
        Assert.Equal("webhook.host_forbidden", Code(open, "http://127.0.0.1/x"));
        Assert.Equal("webhook.host_forbidden", Code(open, "http://169.254.169.254/latest"));
        Assert.False(new WebhookHostPolicy([]).IsConfigured);

        static string? Code(WebhookHostPolicy p, string url)
        {
            p.TryValidate(url, out var code);
            return code;
        }
    }

    [Fact]
    public void The_signature_is_an_hmac_the_receiver_can_recompute()
    {
        // Receiver side: HMAC-SHA256(secret, "<t>.<body>"), lower-case hex.
        var body = "{\"id\":\"x\"}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes("whsec_test"));
        var expected = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes("1700000000." + body))).ToLowerInvariant();
        Assert.Equal(expected, WebhookSender.Sign("whsec_test", 1_700_000_000, body));
        Assert.NotEqual(expected, WebhookSender.Sign("whsec_other", 1_700_000_000, body));
        Assert.NotEqual(expected, WebhookSender.Sign("whsec_test", 1_700_000_001, body));
    }

    [Fact]
    public void Payloads_carry_identifiers_only_and_ignore_anything_else_in_the_event()
    {
        var space = Guid.NewGuid();
        var page = Guid.NewGuid();
        var json = $$$"""{"spaceId":"{{{space}}}","contentId":"{{{page}}}","actorId":"not-a-guid","title":"Secret merger plan","excerpt":"top secret","version":3,"status":"Approved","nested":{"contentId":"{{{Guid.NewGuid()}}}"}}""";
        var (data, spaceId) = WebhookPayload.Extract(json);
        Assert.Equal(space, spaceId);
        Assert.Equal(["contentId", "spaceId", "status", "version"], data.Keys.Order());
        var body = WebhookPayload.Build(Guid.NewGuid(), "content.published", DateTimeOffset.UtcNow, "acme", data);
        Assert.DoesNotContain("merger", body, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", body, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(("content.published", "acme", 3), (JsonDocument.Parse(body).RootElement.GetProperty("type").GetString(), JsonDocument.Parse(body).RootElement.GetProperty("tenant").GetString(), JsonDocument.Parse(body).RootElement.GetProperty("data").GetProperty("version").GetInt32()));

        var (empty, none) = WebhookPayload.Extract("not json");
        Assert.Empty(empty);
        Assert.Null(none);
    }

    [Fact]
    public void Only_known_events_can_be_subscribed()
    {
        Assert.True(WebhookEvents.IsKnown("content.published"));
        Assert.False(WebhookEvents.IsKnown("ping")); // the test ping exists, but nobody subscribes to it
        Assert.False(WebhookEvents.IsKnown("content.deleted"));
        Assert.False(WebhookEvents.IsKnown("*"));
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class WebhookTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private static string Url(string path) => $"https://hooks.example.test/{path}";

    private static string Fresh() => "t" + Guid.NewGuid().ToString("N")[..10];

    private static async Task<JsonElement> CreateAsync(Browser admin, string path, object? events = null, Guid? spaceId = null, string name = "ERP sync")
    {
        using var r = await admin.PostAsync("api/v1/integrations/webhooks", new { name, url = Url(path), events = events ?? new[] { "content.created", "content.published" }, spaceId });
        return await ReadAsync(r);
    }

    private IEnumerable<ReceivedHook> Hits(string path) => fx.Hooks.Received.Where(h => h.Url.AbsolutePath == "/" + path);

    private static bool SignatureValid(ReceivedHook hook, string secret)
    {
        var parts = hook.Headers["X-Raavi-Signature"].Split(',').Select(p => p.Split('=', 2)).ToDictionary(p => p[0], p => p[1]);
        return CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(parts["v1"]), Encoding.ASCII.GetBytes(WebhookSender.Sign(secret, long.Parse(parts["t"], System.Globalization.CultureInfo.InvariantCulture), hook.Body)));
    }

    private AsyncServiceScope Scope(IdentityTestKit.TestTenant t) =>
        fx.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(new ResolvedTenant(t.Id, t.Code, t.Slug, TenantResolutionSource.BackgroundJob, null));

    [Fact]
    public async Task Webhooks_are_managed_by_administrators_and_the_secret_is_shown_exactly_once()
    {
        fx.Hooks.Reset();
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var path = Fresh();

        Assert.True((await admin.GetJsonAsync("api/v1/integrations/webhooks/catalog")).GetProperty("hostPolicyConfigured").GetBoolean());
        var created = await CreateAsync(admin, path);
        var secret = created.GetProperty("secret").GetString()!;
        var id = created.GetProperty("webhook").GetProperty("id").GetGuid();
        Assert.StartsWith("whsec_", secret, StringComparison.Ordinal);
        Assert.True(secret.Length > 30);
        foreach (var url in new[] { "api/v1/integrations/webhooks", $"api/v1/integrations/webhooks/{id}" })
        {
            Assert.DoesNotContain(secret, (await admin.GetJsonAsync(url)).GetRawText(), StringComparison.Ordinal); // never returned again
        }

        foreach (var (body, code) in new (object, string)[]
        {
            (new { name = "x", url = "https://other.example.test/hook", events = new[] { "content.created" } }, "webhook.host_not_allowed"),
            (new { name = "x", url = "http://127.0.0.1/hook", events = new[] { "content.created" } }, "webhook.host_not_allowed"),
            (new { name = "x", url = Url("a#frag"), events = new[] { "content.created" } }, "webhook.url_invalid"),
            (new { name = "x", url = Url("a"), events = new[] { "content.deleted" } }, "webhook.events_invalid"),
            (new { name = "x", url = Url("a"), events = Array.Empty<string>() }, "webhook.events_invalid"),
            (new { name = " ", url = Url("a"), events = new[] { "content.created" } }, "webhook.name_invalid"),
        })
        {
            using var bad = await admin.PostAsync("api/v1/integrations/webhooks", body);
            await Browser.AssertStatusAsync(bad, HttpStatusCode.BadRequest, code);
        }

        // Update needs the revision; rotating gives a different secret and the old one stops working.
        var (current, etag) = await GetWithETagAsync(admin, $"api/v1/integrations/webhooks/{id}");
        var update = new { name = "ERP", url = Url(path), events = new[] { "content.created" }, enabled = true };
        using (var missing = await admin.SendAsync(HttpMethod.Put, $"api/v1/integrations/webhooks/{id}", update, withXsrf: true))
        {
            await Browser.AssertStatusAsync(missing, HttpStatusCode.PreconditionRequired);
        }

        using (var ok = await admin.SendAsync(HttpMethod.Put, $"api/v1/integrations/webhooks/{id}", update, withXsrf: true, ifMatch: etag))
        {
            var updated = await ReadAsync(ok);
            Assert.Equal(("ERP", 2), (updated.GetProperty("name").GetString(), updated.GetProperty("revision").GetInt32()));
            Assert.Equal(["content.created"], updated.GetProperty("events").EnumerateArray().Select(e => e.GetString()!));
        }

        using (var stale = await admin.SendAsync(HttpMethod.Put, $"api/v1/integrations/webhooks/{id}", update, withXsrf: true, ifMatch: etag))
        {
            await Browser.AssertStatusAsync(stale, HttpStatusCode.PreconditionFailed);
        }

        _ = current;
        string rotated;
        using (var rotate = await admin.PostAsync($"api/v1/integrations/webhooks/{id}/rotate-secret", new { }))
        {
            Assert.Equal("no-store", rotate.Headers.CacheControl!.ToString());
            rotated = (await ReadAsync(rotate)).GetProperty("secret").GetString()!;
            Assert.NotEqual(secret, rotated);
        }

        // The ping uses the rotated secret and reports the receiver's answer without echoing its body.
        using (var ping = await admin.PostAsync($"api/v1/integrations/webhooks/{id}/test", new { }))
        {
            var result = await ReadAsync(ping);
            Assert.Equal((true, 200), (result.GetProperty("success").GetBoolean(), result.GetProperty("httpStatus").GetInt32()));
        }

        var hit = Assert.Single(Hits(path));
        Assert.Equal("ping", hit.Headers["X-Raavi-Event"]);
        Assert.True(SignatureValid(hit, rotated));
        Assert.False(SignatureValid(hit, secret));

        fx.Hooks.Status = HttpStatusCode.InternalServerError;
        using (var failing = await admin.PostAsync($"api/v1/integrations/webhooks/{id}/test", new { }))
        {
            var text = await failing.Content.ReadAsStringAsync();
            Assert.Contains("http_5xx", text, StringComparison.Ordinal);
            Assert.DoesNotContain("secret receiver detail", text, StringComparison.Ordinal);
        }

        // Members cannot see or change anything; unknown ids are 404.
        foreach (var url in new[] { "api/v1/integrations/webhooks", $"api/v1/integrations/webhooks/{id}", "api/v1/integrations/webhooks/catalog", $"api/v1/integrations/webhooks/{id}/deliveries" })
        {
            using var r = await member.GetAsync(url);
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
        }

        using (var memberCreate = await member.PostAsync("api/v1/integrations/webhooks", new { name = "x", url = Url("m"), events = new[] { "content.created" } }))
        {
            Assert.Equal(HttpStatusCode.Forbidden, memberCreate.StatusCode);
        }

        using (var unknown = await admin.GetAsync($"api/v1/integrations/webhooks/{Guid.NewGuid()}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        }

        using (var del = await admin.DeleteAsync($"api/v1/integrations/webhooks/{id}"))
        {
            Assert.True(del.IsSuccessStatusCode);
        }

        Assert.Equal(0, (await admin.GetJsonAsync("api/v1/integrations/webhooks")).GetArrayLength());
    }

    [Fact]
    public async Task Events_reach_the_matching_webhooks_signed_with_identifiers_only_and_never_cross_tenants()
    {
        fx.Hooks.Reset();
        var (tenantA, adminA, _) = await _kit.Identity.TenantWithAdminAsync();
        var (_, adminB, _) = await _kit.Identity.TenantWithAdminAsync();
        var (pathA, pathB, pathOff, pathScoped, pathOther) = (Fresh(), Fresh(), Fresh(), Fresh(), Fresh());
        var secretA = (await CreateAsync(adminA, pathA)).GetProperty("secret").GetString()!;
        await CreateAsync(adminB, pathB);
        var off = await CreateAsync(adminA, pathOff);
        var otherOnly = await CreateAsync(adminA, pathOther, events: new[] { "space.created" });
        var key = (await CreateSpaceAsync(adminA)).GetProperty("key").GetString()!;
        var spaceId = (await adminA.GetJsonAsync($"api/v1/spaces/{key}")).GetProperty("id").GetGuid();
        await CreateAsync(adminA, pathScoped, spaceId: Guid.NewGuid()); // another space: must stay silent for this space's events

        // Switch one webhook off.
        var offId = off.GetProperty("webhook").GetProperty("id").GetGuid();
        var (_, etag) = await GetWithETagAsync(adminA, $"api/v1/integrations/webhooks/{offId}");
        using (var disable = await adminA.SendAsync(HttpMethod.Put, $"api/v1/integrations/webhooks/{offId}", new { name = "off", url = Url(pathOff), events = new[] { "content.created" }, enabled = false }, withXsrf: true, ifMatch: etag))
        {
            Assert.False((await ReadAsync(disable)).GetProperty("enabled").GetBoolean());
        }

        _ = otherOnly;
        var pageId = Id(await CreatePageAsync(adminA, key, "Quarterly secret plan"));
        await _kit.DispatchOutboxAsync();

        var hit = Assert.Single(Hits(pathA), h => h.Headers["X-Raavi-Event"] == "content.created");
        Assert.True(SignatureValid(hit, secretA));
        Assert.Equal("Raavi-Webhooks/1", hit.Headers["User-Agent"]);
        var body = JsonDocument.Parse(hit.Body).RootElement;
        Assert.Equal(("content.created", tenantA.Slug), (body.GetProperty("type").GetString(), body.GetProperty("tenant").GetString()));
        Assert.Equal(pageId, body.GetProperty("data").GetProperty("contentId").GetGuid());
        Assert.Equal(spaceId, body.GetProperty("data").GetProperty("spaceId").GetGuid());
        Assert.Equal(body.GetProperty("id").GetGuid(), Guid.Parse(hit.Headers["X-Raavi-Delivery"]));
        Assert.DoesNotContain("Quarterly", hit.Body, StringComparison.Ordinal); // no titles or texts

        Assert.Empty(Hits(pathB)); // another tenant's webhook is never called for this tenant's events
        Assert.Empty(Hits(pathOff));
        Assert.Empty(Hits(pathScoped));
        Assert.DoesNotContain(Hits(pathOther), h => h.Headers["X-Raavi-Event"] != "space.created");

        // Dispatching again does not repeat a delivery.
        await _kit.DispatchOutboxAsync();
        Assert.Single(Hits(pathA), h => h.Headers["X-Raavi-Event"] == "content.created");

        var webhookId = (await adminA.GetJsonAsync("api/v1/integrations/webhooks")).EnumerateArray().First(w => w.GetProperty("name").GetString() == "ERP sync" && w.GetProperty("spaceId").ValueKind == JsonValueKind.Null).GetProperty("id").GetGuid();
        var log = (await adminA.GetJsonAsync($"api/v1/integrations/webhooks/{webhookId}/deliveries")).EnumerateArray().ToArray();
        Assert.Contains(log, d => d.GetProperty("eventType").GetString() == "content.created" && d.GetProperty("status").GetString() == "Succeeded" && d.GetProperty("httpStatus").GetInt32() == 200);
    }

    [Fact]
    public async Task Failed_deliveries_are_retried_once_delivered_never_repeated_and_hopeless_ones_switch_the_webhook_off()
    {
        fx.Hooks.Reset();
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var path = Fresh();
        var other = Fresh();
        var created = await CreateAsync(admin, path, events: new[] { "content.published" });
        var id = created.GetProperty("webhook").GetProperty("id").GetGuid();
        var good = await CreateAsync(admin, other, events: new[] { "content.published" }, name: "healthy");
        var spaceId = Guid.NewGuid();

        async Task<Exception?> DeliverAsync(Guid eventId)
        {
            await using var scope = Scope(tenant);
            var evt = new OutboxEvent(eventId, tenant.Id, "content.published", $"{{\"spaceId\":\"{spaceId}\",\"contentId\":\"{Guid.NewGuid()}\",\"actorId\":\"{Guid.NewGuid()}\",\"title\":\"x\",\"version\":1}}", DateTimeOffset.UtcNow);
            var handler = scope.ServiceProvider.GetServices<IOutboxHandler>().OfType<WebhookDispatcher>().Single();
            return await Record.ExceptionAsync(() => handler.HandleAsync(evt, CancellationToken.None));
        }

        async Task<JsonElement[]> LogAsync() => (await admin.GetJsonAsync($"api/v1/integrations/webhooks/{id}/deliveries")).EnumerateArray().ToArray();

        // A server error is retried: the handler throws so that the outbox schedules another attempt; the healthy webhook is not held back.
        var first = Guid.NewGuid();
        fx.Hooks.Status = HttpStatusCode.BadGateway;
        Assert.NotNull(await DeliverAsync(first));
        var failedRow = (await LogAsync())[0];
        Assert.Equal(("Failed", 1, "http_5xx"), (failedRow.GetProperty("status").GetString(), failedRow.GetProperty("attempts").GetInt32(), failedRow.GetProperty("error").GetString()));
        Assert.Equal(2, fx.Hooks.Received.Count(h => h.Url.AbsolutePath == "/" + path || h.Url.AbsolutePath == "/" + other)); // both were tried

        fx.Hooks.Status = HttpStatusCode.OK;
        Assert.Null(await DeliverAsync(first)); // retry succeeds
        var row = (await LogAsync()).Single();
        Assert.Equal(("Succeeded", 2), (row.GetProperty("status").GetString(), row.GetProperty("attempts").GetInt32()));
        Assert.Null(await DeliverAsync(first)); // delivered: a third call sends nothing
        Assert.Equal(2, Hits(path).Count());

        // A permanent client error is recorded and not retried; an unreachable receiver is retried.
        fx.Hooks.Status = HttpStatusCode.NotFound;
        Assert.Null(await DeliverAsync(Guid.NewGuid()));
        fx.Hooks.Status = HttpStatusCode.OK;
        fx.Hooks.Unreachable = true;
        Assert.NotNull(await DeliverAsync(Guid.NewGuid()));
        Assert.Contains((await LogAsync()), d => d.GetProperty("error").GetString() == "unreachable");
        fx.Hooks.Unreachable = false;

        // A webhook that keeps failing is switched off after 20 failures in a row and then receives nothing.
        fx.Hooks.Status = HttpStatusCode.Gone;
        for (var i = 0; i < 20; i++)
        {
            await DeliverAsync(Guid.NewGuid());
        }

        var hooks = (await admin.GetJsonAsync("api/v1/integrations/webhooks")).EnumerateArray().ToDictionary(w => w.GetProperty("name").GetString()!);
        Assert.All(hooks.Values, w => Assert.False(w.GetProperty("enabled").GetBoolean())); // both receivers answered 410 every time
        Assert.Equal("repeated_failures", hooks["ERP sync"].GetProperty("disabledReason").GetString());
        var before = fx.Hooks.Received.Count;
        fx.Hooks.Status = HttpStatusCode.OK;
        await DeliverAsync(Guid.NewGuid());
        Assert.Equal(before, fx.Hooks.Received.Count); // switched-off webhooks are not called any more

        // Switching it on again resets the counter.
        var (_, etag) = await GetWithETagAsync(admin, $"api/v1/integrations/webhooks/{id}");
        using (var enable = await admin.SendAsync(HttpMethod.Put, $"api/v1/integrations/webhooks/{id}", new { name = "ERP sync", url = Url(path), events = new[] { "content.published" }, enabled = true }, withXsrf: true, ifMatch: etag))
        {
            var again = await ReadAsync(enable);
            Assert.Equal((true, 0), (again.GetProperty("enabled").GetBoolean(), again.GetProperty("consecutiveFailures").GetInt32()));
        }

        _ = good;
    }
}
