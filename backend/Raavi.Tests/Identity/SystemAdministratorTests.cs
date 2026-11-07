using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Raavi.Identity.Security;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Identity;

/// <summary>System administrator enrollment with mandatory TOTP and MFA sign-in on the system host.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class SystemAdministratorTests(SqlServerFixture fx)
{
    private const string Password = "System-Admin-Passw0rd!";

    private async Task<(string Email, byte[] Secret, Browser Session)> EnrollAsync()
    {
        var email = $"{SqlServerFixture.Unique("sys").ToLowerInvariant()}@example.com";
        using var key = fx.Api.ForSystemAdmin();
        using var invite = await key.PostAsJsonAsync("api/v1/system/administrators/invitations", new { email, tenantAdministrator = false });
        var created = await invite.Content.ReadFromJsonAsync<JsonElement>(Browser.Json);
        Assert.True(invite.IsSuccessStatusCode, created.ToString());
        var url = new Uri(created.GetProperty("acceptUrl").GetString()!);
        Assert.Equal("/system/setup", url.AbsolutePath);
        var token = url.Fragment["#token=".Length..];

        var browser = new Browser(fx.Api, SqlServerFixture.SystemHost);
        await browser.PrimeAsync();
        var info = await browser.PostOkAsync("api/v1/auth/system/setup/inspect", new { token });
        Assert.Equal(email, info.GetProperty("email").GetString());
        var begin = await browser.PostOkAsync("api/v1/auth/system/setup/begin", new { token });
        var secret = Base32.Decode(begin.GetProperty("secret").GetString()!);
        Assert.StartsWith("otpauth://totp/Raavi:", begin.GetProperty("otpAuthUri").GetString(), StringComparison.Ordinal);

        using var badCode = await browser.PostAsync("api/v1/auth/system/setup/complete", new { token, displayName = "Root", password = Password, code = "000000" });
        await Browser.AssertStatusAsync(badCode, HttpStatusCode.BadRequest, "setup.bad_code");

        var code = TotpService.Code(secret, TotpService.TimeStep(DateTimeOffset.UtcNow));
        await browser.PostOkAsync("api/v1/auth/system/setup/complete", new { token, displayName = "Root", password = Password, code });
        return (email, secret, browser);
    }

    [Fact]
    public async Task Enrolled_administrator_manages_tenants_with_an_mfa_session()
    {
        var (_, _, session) = await EnrollAsync();
        var me = await session.GetJsonAsync("api/v1/auth/me");
        Assert.True(me.GetProperty("isSystemAdministrator").GetBoolean());
        Assert.Equal("system", me.GetProperty("hostKind").GetString());

        // Session (not API key) can use the system API — with antiforgery protection.
        using var list = await session.GetAsync("api/v1/system/tenants");
        Assert.Equal(HttpStatusCode.OK, list.StatusCode);
        using var noCsrf = await session.PostAsync("api/v1/system/tenants", new { code = "ZZZ", slug = "zzz", displayNameFa = "a", displayNameEn = "a" }, withXsrf: false);
        await Browser.AssertStatusAsync(noCsrf, HttpStatusCode.BadRequest, "csrf.invalid");
        using var create = await session.PostAsync("api/v1/system/tenants", new { code = SqlServerFixture.Unique("S"), slug = SqlServerFixture.Unique("s").ToLowerInvariant(), displayNameFa = "a", displayNameEn = "a" });
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);

        var admins = await session.GetJsonAsync("api/v1/system/administrators");
        Assert.Contains(admins.EnumerateArray(), a => a.GetProperty("mfaEnabled").GetBoolean());
    }

    [Fact]
    public async Task System_login_requires_password_and_a_fresh_totp_code()
    {
        var (email, secret, _) = await EnrollAsync();
        // Use a later time step than enrollment (replay protection rejects the enrollment step).
        var step = TotpService.TimeStep(DateTimeOffset.UtcNow) + 1;
        var code = TotpService.Code(secret, step);

        using var browser = new Browser(fx.Api, SqlServerFixture.SystemHost);
        await browser.PrimeAsync();

        using var noCode = await browser.PostAsync("api/v1/auth/system/login", new { email, password = Password, code = "123456" });
        await Browser.AssertStatusAsync(noCode, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        using var wrongPassword = await browser.PostAsync("api/v1/auth/system/login", new { email, password = "nope-nope-nope", code });
        await Browser.AssertStatusAsync(wrongPassword, HttpStatusCode.Unauthorized, "auth.invalid_credentials");

        await browser.PostOkAsync("api/v1/auth/system/login", new { email, password = Password, code });
        Assert.True((await browser.GetJsonAsync("api/v1/auth/me")).GetProperty("isSystemAdministrator").GetBoolean());

        // The same code cannot be used twice.
        using var replayBrowser = new Browser(fx.Api, SqlServerFixture.SystemHost);
        await replayBrowser.PrimeAsync();
        using var replay = await replayBrowser.PostAsync("api/v1/auth/system/login", new { email, password = Password, code });
        await Browser.AssertStatusAsync(replay, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Tenant_users_cannot_sign_in_on_the_system_host()
    {
        var kit = new IdentityTestKit(fx);
        var (_, _, email) = await kit.TenantWithAdminAsync();
        using var browser = new Browser(fx.Api, SqlServerFixture.SystemHost);
        await browser.PrimeAsync();
        using var r = await browser.PostAsync("api/v1/auth/system/login", new { email, password = IdentityTestKit.Password, code = "000000" });
        await Browser.AssertStatusAsync(r, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        Assert.Null(browser.SessionCookie);
    }

    [Fact]
    public void Totp_matches_rfc_6238_test_vector()
    {
        // RFC 6238 Appendix B, SHA-1, T = 59 s → 94287082; 6-digit truncation → 287082.
        var secret = "12345678901234567890"u8.ToArray();
        Assert.Equal("287082", TotpService.Code(secret, 59 / 30));
        Assert.Equal(1L, TotpService.Verify(secret, "287082", DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Equal(1L, TotpService.Verify(secret, "287082", DateTimeOffset.FromUnixTimeSeconds(89)));  // +1 step window
        Assert.Null(TotpService.Verify(secret, "287082", DateTimeOffset.FromUnixTimeSeconds(150)));
        Assert.Null(TotpService.Verify(secret, "28708", DateTimeOffset.FromUnixTimeSeconds(59)));
        Assert.Equal("GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ", TotpService.ToBase32(secret));
    }
}
