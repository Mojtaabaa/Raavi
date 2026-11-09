using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Tenancy.Application;
using Raavi.Tenancy.Resolution;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Tenancy;

/// <summary>Path addressing (system-host/t/slug) and the default workspace (ADR-045) — pure parsing, option validation, and requests against the real pipeline.</summary>
public sealed class PathAddressingUnitTests
{
    [Theory]
    [InlineData("/t/dev", "dev", "/")]
    [InlineData("/t/dev/", "dev", "/")]
    [InlineData("/t/dev/api/v1/spaces", "dev", "/api/v1/spaces")]
    [InlineData("/T/Dev-1/hubs/events", "dev-1", "/hubs/events")]
    public void A_workspace_prefix_is_recognised_and_the_rest_of_the_path_is_kept(string path, string slug, string rest)
    {
        Assert.True(TenantResolutionMiddleware.TryMatchPathTenant(new PathString(path), "t", out var got, out var remainder));
        Assert.Equal(slug, got);
        Assert.Equal(rest, remainder.Value);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/t")]
    [InlineData("/t/")]
    [InlineData("/api/v1/spaces")]
    [InlineData("/tx/dev")]
    [InlineData("/t/-dev")]
    [InlineData("/t/dev-")]
    [InlineData("/t/de_v")]
    [InlineData("/t/dev%2fx")]
    [InlineData("/t/dé")]
    [InlineData("//t/dev")]
    public void Anything_else_is_not_a_workspace_prefix(string path) =>
        Assert.False(TenantResolutionMiddleware.TryMatchPathTenant(new PathString(path), "t", out _, out _));

    [Fact]
    public void A_long_slug_is_refused()
    {
        var slug = new string('a', 64);
        Assert.False(TenantResolutionMiddleware.TryMatchPathTenant(new PathString("/t/" + slug), "t", out _, out _));
    }

    [Fact]
    public void Options_validate_the_addressing_the_prefix_and_the_base_domains()
    {
        static HostingOptions Make(string addressing = "Subdomain", string prefix = "t", string[]? bases = null, string defaultCode = "")
        {
            var o = new HostingOptions { SystemHost = "raavi.example.test", Addressing = addressing, PathPrefix = prefix, DefaultTenantCode = defaultCode };
            foreach (var b in bases ?? ["raavi.example.test"])
            {
                o.TenantBaseDomains.Add(b);
            }

            o.Normalize();
            return o;
        }

        var path = Make("Path", bases: []); // no wildcard DNS needed in path mode
        Assert.True(path.AcceptsPaths);
        Assert.False(path.AcceptsSubdomains);
        Assert.Equal("https://raavi.example.test/t/dev/", path.TenantPathUrl("dev").ToString());
        Assert.True(Make("both").AcceptsPaths && Make("both").AcceptsSubdomains);
        Assert.Equal("DEV", Make(defaultCode: " dev ").NormalizedDefaultTenantCode);

        Assert.Throws<OptionsValidationException>(() => Make("Subdomain", bases: [])); // subdomains need a base domain
        Assert.Throws<OptionsValidationException>(() => Make("Sideways"));
        Assert.Throws<OptionsValidationException>(() => Make("1"));
        foreach (var bad in new[] { "", "system", "api", "Hubs", "has space", "toolongprefixtoolongprefix", "a/b" })
        {
            Assert.Throws<OptionsValidationException>(() => Make("Path", bad, []));
        }
    }
}

[Collection(SqlTestGroup.Name)]
public sealed class PathAddressingTests(SqlServerFixture fx)
{
    private HttpClient Client(string addressing, string? defaultTenantCode = null)
    {
        var factory = fx.Api.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
        {
            var settings = new Dictionary<string, string?> { ["Raavi:Hosting:Addressing"] = addressing };
            if (defaultTenantCode is not null)
            {
                settings["Raavi:Hosting:DefaultTenantCode"] = defaultTenantCode;
            }

            c.AddInMemoryCollection(settings);
        }));
        _factories.Add(factory);
        return factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri($"https://{SqlServerFixture.SystemHost}/"), AllowAutoRedirect = false, HandleCookies = false });
    }

    private readonly List<WebApplicationFactory<Program>> _factories = [];

    private static async Task<JsonElement> JsonOf(HttpResponseMessage r) => (await r.Content.ReadFromJsonAsync<JsonElement>())!;

    private async Task<TenantDto> NewTenantAsync(string prefix) => await fx.CreateTenantAsync(SqlServerFixture.Unique(prefix), SqlServerFixture.Unique(prefix).ToLowerInvariant());

    [Fact]
    public async Task A_workspace_is_reached_by_its_path_prefix_without_any_subdomain()
    {
        var tenant = await NewTenantAsync("pa");
        using var client = Client("Path");

        using var ok = await client.GetAsync($"t/{tenant.Slug}/api/v1/host/context");
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
        var body = await JsonOf(ok);
        Assert.Equal("tenant", body.GetProperty("kind").GetString());

        using var upper = await client.GetAsync($"T/{tenant.Slug.ToUpperInvariant()}/api/v1/host/context");
        Assert.Equal("tenant", (await JsonOf(upper)).GetProperty("kind").GetString());

        // Unknown workspace: the same generic 404 as for an unknown host.
        using var unknown = await client.GetAsync("t/no-such-workspace/api/v1/host/context");
        Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
        Assert.Equal("tenant.unavailable", (await JsonOf(unknown)).GetProperty("code").GetString());

        // The root of the system host is still the system area (no default workspace configured).
        using var root = await client.GetAsync("api/v1/host/context");
        Assert.Equal("system", (await JsonOf(root)).GetProperty("kind").GetString());

        // A workspace-scoped, anonymous endpoint answers for THAT workspace only.
        using var providers = await client.GetAsync($"t/{tenant.Slug}/api/v1/auth/providers");
        Assert.Equal(HttpStatusCode.OK, providers.StatusCode);
    }

    [Fact]
    public async Task Path_addressing_is_off_unless_the_installation_turns_it_on_and_hint_headers_still_cannot_switch_workspace()
    {
        var tenant = await NewTenantAsync("pb");
        var other = await NewTenantAsync("pc");

        using (var off = Client("Subdomain"))
        {
            using var r = await off.GetAsync($"t/{tenant.Slug}/api/v1/host/context");
            Assert.NotEqual("tenant", r.IsSuccessStatusCode ? (await JsonOf(r)).GetProperty("kind").GetString() : null); // not a workspace address on this installation
        }

        using var client = Client("Both");
        using var request = new HttpRequestMessage(HttpMethod.Get, $"t/{tenant.Slug}/api/v1/host/context");
        request.Headers.Add("X-Tenant-Code", other.Code);
        using var conflict = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, conflict.StatusCode);
        Assert.Equal("tenant.conflict", (await JsonOf(conflict)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task The_default_workspace_answers_for_requests_that_name_none_and_the_system_area_moves_under_system()
    {
        var tenant = await NewTenantAsync("pd");
        using var client = Client("Path", tenant.Code);

        using var root = await client.GetAsync("api/v1/host/context");
        Assert.Equal("tenant", (await JsonOf(root)).GetProperty("kind").GetString());

        using var system = await client.GetAsync("system/api/v1/host/context");
        Assert.Equal("system", (await JsonOf(system)).GetProperty("kind").GetString());

        // System API with an operator key lives under /system/api; the unprefixed system routes now belong to the default workspace and refuse it.
        using var keyed = new HttpRequestMessage(HttpMethod.Get, "system/api/v1/system/tenants");
        keyed.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SqlServerFixture.SystemApiKey);
        using var allowed = await client.SendAsync(keyed);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        using var unprefixed = new HttpRequestMessage(HttpMethod.Get, "api/v1/system/tenants");
        unprefixed.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", SqlServerFixture.SystemApiKey);
        using var refused = await client.SendAsync(unprefixed);
        Assert.False(refused.IsSuccessStatusCode, "the system API must not be reachable on the default workspace's address");

        // An explicit workspace prefix wins over the default.
        var other = await NewTenantAsync("pe");
        using var named = await client.GetAsync($"t/{other.Slug}/api/v1/host/context");
        Assert.Equal("tenant", (await JsonOf(named)).GetProperty("kind").GetString());
    }

    [Fact]
    public async Task A_default_workspace_that_does_not_exist_gives_the_generic_404_not_the_system_area()
    {
        using var client = Client("Path", "NOSUCHCODE");
        using var root = await client.GetAsync("api/v1/host/context");
        Assert.Equal(HttpStatusCode.NotFound, root.StatusCode);
        using var system = await client.GetAsync("system/api/v1/host/context");
        Assert.Equal("system", (await JsonOf(system)).GetProperty("kind").GetString()); // the way back in always exists
    }

    [Fact]
    public async Task Links_of_a_path_addressed_workspace_carry_the_prefix()
    {
        var tenant = await NewTenantAsync("pf");
        var factory = fx.Api.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Raavi:Hosting:Addressing"] = "Path" })));
        _factories.Add(factory);
        await using var scope = factory.Services.CreateAsyncScope();
        var urls = scope.ServiceProvider.GetRequiredService<ITenantUrlResolver>();

        var url = await urls.GetCanonicalUrlAsync(tenant.Id);
        Assert.Equal($"https://{SqlServerFixture.SystemHost}/t/{tenant.Slug}/", url!.ToString());
        Assert.Equal(string.Empty, urls.SystemApiPrefix);

        var withDefault = fx.Api.WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?> { ["Raavi:Hosting:Addressing"] = "Path", ["Raavi:Hosting:DefaultTenantCode"] = tenant.Code })));
        _factories.Add(withDefault);
        await using var scope2 = withDefault.Services.CreateAsyncScope();
        Assert.Equal("/system", scope2.ServiceProvider.GetRequiredService<ITenantUrlResolver>().SystemApiPrefix);
    }
}
