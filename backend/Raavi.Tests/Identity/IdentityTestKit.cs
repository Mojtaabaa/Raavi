using System.Net.Http.Json;
using System.Text.Json;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Identity;

/// <summary>Scenario helpers built only on public HTTP endpoints, exactly as an administrator and a browser would use them.</summary>
public sealed class IdentityTestKit(SqlServerFixture fx)
{
    public const string Password = "Correct-Horse-Battery-9";

    public sealed record TestTenant(Guid Id, string Code, string Slug)
    {
        public string Host => $"{Slug}.{SqlServerFixture.BaseDomain}";
    }

    public async Task<TestTenant> CreateTenantAsync()
    {
        var code = SqlServerFixture.Unique("T");
        var slug = SqlServerFixture.Unique("t").ToLowerInvariant();
        using var admin = fx.Api.ForSystemAdmin();
        using var response = await admin.PostAsJsonAsync("api/v1/system/tenants", new { code, slug, displayNameFa = "آزمون", displayNameEn = "Test" });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Browser.Json);
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        return new TestTenant(body.GetProperty("id").GetGuid(), code, slug);
    }

    /// <summary>System host (API key) invites into a tenant; returns the raw token from the link fragment.</summary>
    public async Task<string> SystemInviteAsync(TestTenant tenant, string email, bool admin)
    {
        using var client = fx.Api.ForSystemAdmin();
        using var response = await client.PostAsJsonAsync($"api/v1/system/tenants/{tenant.Id}/invitations", new { email, tenantAdministrator = admin });
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Browser.Json);
        Assert.True(response.IsSuccessStatusCode, body.ToString());
        return TokenFrom(body);
    }

    public static string TokenFrom(JsonElement invitationCreated)
    {
        var url = new Uri(invitationCreated.GetProperty("acceptUrl").GetString()!);
        Assert.StartsWith("#token=", url.Fragment, StringComparison.Ordinal);
        Assert.True(string.IsNullOrEmpty(url.Query), "tokens must never be in the query string");
        return url.Fragment["#token=".Length..];
    }

    /// <summary>Accepts as a brand-new account and returns a signed-in browser.</summary>
    public async Task<Browser> AcceptNewAsync(TestTenant tenant, string token, string displayName = "Test User")
    {
        var browser = new Browser(fx.Api, tenant.Host);
        await browser.PrimeAsync();
        await browser.PostOkAsync("api/v1/auth/invitations/accept", new { token, mode = "new", password = Password, displayName });
        return browser;
    }

    /// <summary>New tenant with a signed-in administrator.</summary>
    public async Task<(TestTenant Tenant, Browser Admin, string Email)> TenantWithAdminAsync()
    {
        var tenant = await CreateTenantAsync();
        var email = $"{SqlServerFixture.Unique("admin").ToLowerInvariant()}@example.com";
        var token = await SystemInviteAsync(tenant, email, admin: true);
        return (tenant, await AcceptNewAsync(tenant, token, "Admin"), email);
    }

    /// <summary>Tenant admin invites a member who accepts with a new account.</summary>
    public async Task<(Browser Member, string Email, Guid UserId)> AddMemberAsync(TestTenant tenant, Browser admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        var email = $"{SqlServerFixture.Unique("user").ToLowerInvariant()}@example.com";
        var created = await admin.PostOkAsync("api/v1/tenant/invitations", new { email, tenantAdministrator = false });
        var member = await AcceptNewAsync(tenant, TokenFrom(created), "Member");
        var me = await member.GetJsonAsync("api/v1/auth/me");
        return (member, email, me.GetProperty("id").GetGuid());
    }

    public async Task<Browser> LoginAsync(TestTenant tenant, string email, string password = Password)
    {
        var browser = new Browser(fx.Api, tenant.Host);
        await browser.PrimeAsync();
        await browser.PostOkAsync("api/v1/auth/login/local", new { email, password });
        return browser;
    }
}
