using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Tenancy;

/// <summary>End-to-end tenancy behaviour through the real HTTP pipeline (spec §35 E2E 1–7, isolation regression).</summary>
[Collection(SqlTestGroup.Name)]
public sealed class TenancyApiTests(SqlServerFixture fx)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private static async Task<(Guid Id, string Code, string Slug)> CreateAsync(HttpClient admin)
    {
        var code = SqlServerFixture.Unique("T");
        var slug = SqlServerFixture.Unique("t").ToLowerInvariant();
        using var response = await admin.PostAsJsonAsync("api/v1/system/tenants", new { code, slug, displayNameFa = "سازمان آزمون", displayNameEn = "Test Org", defaultLocale = "en", timeZone = "Europe/Berlin", calendar = "gregorian" });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal("Active", body.GetProperty("status").GetString());
        return (body.GetProperty("id").GetGuid(), code, slug);
    }

    private HttpClient TenantClient(string slug) => fx.Api.ForHost($"{slug}.{SqlServerFixture.BaseDomain}");

    [Fact]
    public async Task System_endpoints_require_api_key_and_exist_only_on_the_system_host()
    {
        using var anonymous = fx.Api.ForHost(SqlServerFixture.SystemHost);
        using var noKey = await anonymous.GetAsync(new Uri("api/v1/system/tenants", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);

        using var wrongKey = fx.Api.ForHost(SqlServerFixture.SystemHost);
        wrongKey.DefaultRequestHeaders.Authorization = new("Bearer", "not-the-key");
        using var bad = await wrongKey.GetAsync(new Uri("api/v1/system/tenants", UriKind.Relative));
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        using var admin = fx.Api.ForSystemAdmin();
        var (_, _, slug) = await CreateAsync(admin);

        // Same key on a tenant host: the system API does not exist there.
        using var onTenant = TenantClient(slug);
        onTenant.DefaultRequestHeaders.Authorization = new("Bearer", SqlServerFixture.SystemApiKey);
        using var hidden = await onTenant.GetAsync(new Uri("api/v1/system/tenants", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
    }

    [Fact]
    public async Task Tenant_is_resolved_from_its_subdomain_and_unknown_hosts_are_rejected()
    {
        using var admin = fx.Api.ForSystemAdmin();
        var (_, _, slug) = await CreateAsync(admin);

        using var client = TenantClient(slug);
        var ctx = await client.GetFromJsonAsync<JsonElement>("api/v1/host/context", Json);
        Assert.Equal("tenant", ctx.GetProperty("kind").GetString());
        Assert.Equal(slug, ctx.GetProperty("tenant").GetProperty("slug").GetString());
        Assert.Equal("en", ctx.GetProperty("tenant").GetProperty("defaultLocale").GetString());
        Assert.Equal("سازمان آزمون", ctx.GetProperty("tenant").GetProperty("displayName").GetProperty("fa").GetString());
        Assert.Equal("gregorian", ctx.GetProperty("tenant").GetProperty("calendar").GetString());
        Assert.False(ctx.GetProperty("tenant").TryGetProperty("id", out _), "public context must not expose internal ids");

        foreach (var host in new[] { $"nope-{slug}.{SqlServerFixture.BaseDomain}", $"x.{slug}.{SqlServerFixture.BaseDomain}", SqlServerFixture.BaseDomain, "evil.example.com" })
        {
            using var other = fx.Api.ForHost(host);
            using var response = await other.GetAsync(new Uri("api/v1/host/context", UriKind.Relative));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Contains("tenant.unavailable", await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }

        using var system = fx.Api.ForHost(SqlServerFixture.SystemHost);
        var sys = await system.GetFromJsonAsync<JsonElement>("api/v1/host/context", Json);
        Assert.Equal("system", sys.GetProperty("kind").GetString());
    }

    [Fact]
    public async Task Suspension_blocks_access_immediately_and_activation_restores_it()
    {
        using var admin = fx.Api.ForSystemAdmin();
        var (id, _, slug) = await CreateAsync(admin);
        using var client = TenantClient(slug);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("api/v1/host/context", UriKind.Relative))).StatusCode);

        using (var suspend = await admin.PostAsJsonAsync($"api/v1/system/tenants/{id}/suspend", new { reason = "unpaid" }))
        {
            Assert.Equal(HttpStatusCode.OK, suspend.StatusCode);
        }

        using var blocked = await client.GetAsync(new Uri("api/v1/host/context", UriKind.Relative));
        Assert.Equal(HttpStatusCode.NotFound, blocked.StatusCode);

        // Suspending twice is an invalid transition.
        using (var again = await admin.PostAsJsonAsync($"api/v1/system/tenants/{id}/suspend", new { reason = "again" }))
        {
            Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
        }

        using (var activate = await admin.PostAsync(new Uri($"api/v1/system/tenants/{id}/activate", UriKind.Relative), null))
        {
            Assert.Equal(HttpStatusCode.OK, activate.StatusCode);
        }

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(new Uri("api/v1/host/context", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Duplicate_codes_and_slugs_and_invalid_input_are_rejected()
    {
        using var admin = fx.Api.ForSystemAdmin();
        var (_, code, slug) = await CreateAsync(admin);

        using var dupCode = await admin.PostAsJsonAsync("api/v1/system/tenants", new { code, slug = slug + "x", displayNameFa = "a", displayNameEn = "a" });
        Assert.Equal(HttpStatusCode.Conflict, dupCode.StatusCode);
        using var dupSlug = await admin.PostAsJsonAsync("api/v1/system/tenants", new { code = code + "X", slug, displayNameFa = "a", displayNameEn = "a" });
        Assert.Equal(HttpStatusCode.Conflict, dupSlug.StatusCode);
        using var reserved = await admin.PostAsJsonAsync("api/v1/system/tenants", new { code = SqlServerFixture.Unique("R"), slug = "admin", displayNameFa = "a", displayNameEn = "a" });
        Assert.Equal(HttpStatusCode.BadRequest, reserved.StatusCode);
        Assert.Contains("tenant.slug_reserved", await reserved.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        using var badTz = await admin.PostAsJsonAsync("api/v1/system/tenants", new { code = SqlServerFixture.Unique("Z"), slug = SqlServerFixture.Unique("z").ToLowerInvariant(), displayNameFa = "a", displayNameEn = "a", timeZone = "Mars/Olympus" });
        Assert.Equal(HttpStatusCode.BadRequest, badTz.StatusCode);
    }

    [Fact]
    public async Task Custom_domain_resolves_only_after_dns_verification()
    {
        using var admin = fx.Api.ForSystemAdmin();
        var (id, _, slug) = await CreateAsync(admin);
        var host = $"wiki-{slug}.example.com";

        using var add = await admin.PostAsJsonAsync($"api/v1/system/tenants/{id}/domains", new { host = host.ToUpperInvariant() + "." });
        Assert.Equal(HttpStatusCode.Created, add.StatusCode);
        var domain = await add.Content.ReadFromJsonAsync<JsonElement>(Json);
        Assert.Equal(host, domain.GetProperty("host").GetString());
        var domainId = domain.GetProperty("id").GetGuid();

        using var custom = fx.Api.ForHost(host);
        Assert.Equal(HttpStatusCode.NotFound, (await custom.GetAsync(new Uri("api/v1/host/context", UriKind.Relative))).StatusCode);

        // Wrong TXT value → still not verified.
        fx.Dns.Set(domain.GetProperty("txtRecordName").GetString()!, "raavi-verify=wrong");
        using (var fail = await admin.PostAsync(new Uri($"api/v1/system/tenants/{id}/domains/{domainId}/verify", UriKind.Relative), null))
        {
            Assert.Equal(HttpStatusCode.BadRequest, fail.StatusCode);
        }

        fx.Dns.Set(domain.GetProperty("txtRecordName").GetString()!, domain.GetProperty("txtRecordValue").GetString()!);
        using (var ok = await admin.PostAsync(new Uri($"api/v1/system/tenants/{id}/domains/{domainId}/verify", UriKind.Relative), null))
        {
            Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        }

        var ctx = await custom.GetFromJsonAsync<JsonElement>("api/v1/host/context", Json);
        Assert.Equal(slug, ctx.GetProperty("tenant").GetProperty("slug").GetString());

        // Another tenant cannot claim the same host.
        var (other, _, _) = await CreateAsync(admin);
        using var steal = await admin.PostAsJsonAsync($"api/v1/system/tenants/{other}/domains", new { host });
        Assert.Equal(HttpStatusCode.Conflict, steal.StatusCode);

        // Platform hosts cannot be custom domains.
        using var platform = await admin.PostAsJsonAsync($"api/v1/system/tenants/{id}/domains", new { host = $"x.{SqlServerFixture.BaseDomain}" });
        Assert.Equal(HttpStatusCode.BadRequest, platform.StatusCode);

        // Removal stops resolution immediately and the host is quarantined for other tenants.
        using (var remove = await admin.DeleteAsync(new Uri($"api/v1/system/tenants/{id}/domains/{domainId}", UriKind.Relative)))
        {
            Assert.Equal(HttpStatusCode.NoContent, remove.StatusCode);
        }

        Assert.Equal(HttpStatusCode.NotFound, (await custom.GetAsync(new Uri("api/v1/host/context", UriKind.Relative))).StatusCode);
        using var quarantined = await admin.PostAsJsonAsync($"api/v1/system/tenants/{other}/domains", new { host });
        Assert.Equal(HttpStatusCode.Conflict, quarantined.StatusCode);
        Assert.Contains("domain.quarantined", await quarantined.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Conflicting_tenant_hint_headers_are_rejected()
    {
        using var admin = fx.Api.ForSystemAdmin();
        var (aId, aCode, aSlug) = await CreateAsync(admin);
        var (bId, _, _) = await CreateAsync(admin);

        using var client = TenantClient(aSlug);
        using var conflicting = new HttpRequestMessage(HttpMethod.Get, "api/v1/host/context");
        conflicting.Headers.Add("X-Tenant-Id", bId.ToString());
        using var rejected = await client.SendAsync(conflicting);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Contains("tenant.conflict", await rejected.Content.ReadAsStringAsync(), StringComparison.Ordinal);

        using var matching = new HttpRequestMessage(HttpMethod.Get, "api/v1/host/context");
        matching.Headers.Add("X-Tenant-Id", aId.ToString());
        matching.Headers.Add("X-Tenant-Code", aCode.ToLowerInvariant());
        using var ok = await client.SendAsync(matching);
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Tenant_id_in_request_body_cannot_redirect_writes_to_another_tenant()
    {
        using var admin = fx.Api.ForSystemAdmin();
        var (aId, _, _) = await CreateAsync(admin);
        var (bId, _, _) = await CreateAsync(admin);

        using var update = await admin.PutAsJsonAsync($"api/v1/system/tenants/{aId}/settings", new { tenantId = bId, defaultLocale = "fa", timeZone = "Asia/Tehran", calendar = "persian", storageQuotaBytes = 1024 });
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);

        var a = await admin.GetFromJsonAsync<JsonElement>($"api/v1/system/tenants/{aId}/settings", Json);
        var b = await admin.GetFromJsonAsync<JsonElement>($"api/v1/system/tenants/{bId}/settings", Json);
        Assert.Equal("fa", a.GetProperty("defaultLocale").GetString());
        Assert.Equal(1024, a.GetProperty("storageQuotaBytes").GetInt64());
        Assert.Equal("en", b.GetProperty("defaultLocale").GetString());
        Assert.Equal(JsonValueKind.Null, b.GetProperty("storageQuotaBytes").ValueKind);
    }

    [Fact]
    public async Task Discovery_by_code_returns_canonical_url_only_for_active_tenants_on_system_host()
    {
        using var admin = fx.Api.ForSystemAdmin();
        var (id, code, slug) = await CreateAsync(admin);

        using var system = fx.Api.ForHost(SqlServerFixture.SystemHost);
        var found = await system.GetFromJsonAsync<JsonElement>($"api/v1/discovery/tenants/{code.ToLowerInvariant()}", Json);
        Assert.Equal($"{slug}.{SqlServerFixture.BaseDomain}", new Uri(found.GetProperty("url").GetString()!).Host);

        using var onTenant = TenantClient(slug);
        Assert.Equal(HttpStatusCode.NotFound, (await onTenant.GetAsync(new Uri($"api/v1/discovery/tenants/{code}", UriKind.Relative))).StatusCode);

        using (await admin.PostAsJsonAsync($"api/v1/system/tenants/{id}/deactivate", new { reason = "closed" }))
        {
        }

        Assert.Equal(HttpStatusCode.NotFound, (await system.GetAsync(new Uri($"api/v1/discovery/tenants/{code}", UriKind.Relative))).StatusCode);
    }

    [Fact]
    public async Task Branding_rejects_unsafe_values_and_is_exposed_as_plain_text()
    {
        using var admin = fx.Api.ForSystemAdmin();
        var (id, _, slug) = await CreateAsync(admin);

        using var bad = await admin.PutAsJsonAsync($"api/v1/system/tenants/{id}/branding", new { primaryColor = "red;background:url(x)" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);

        using var ok = await admin.PutAsJsonAsync($"api/v1/system/tenants/{id}/branding", new { primaryColor = "#0F766E", welcomeMessageEn = "<b>Hi</b>" });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);

        using var client = TenantClient(slug);
        var ctx = await client.GetFromJsonAsync<JsonElement>("api/v1/host/context", Json);
        var branding = ctx.GetProperty("tenant").GetProperty("branding");
        Assert.Equal("#0f766e", branding.GetProperty("primaryColor").GetString());
        Assert.Equal("<b>Hi</b>", branding.GetProperty("welcomeMessageEn").GetString()); // stored verbatim; the SPA renders it as text
    }
}
