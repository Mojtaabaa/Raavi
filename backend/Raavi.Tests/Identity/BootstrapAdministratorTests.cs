using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Time;
using Raavi.Identity.Application;
using Raavi.Identity.Security;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Identity;

/// <summary>The first system administrator from installation settings: temporary password, forced new password and authenticator enrolment.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class BootstrapAdministratorTests(SqlServerFixture fx)
{
    private const string Temporary = "123@vesta"; // deliberately shorter than the password policy: it only opens the first-sign-in flow
    private const string Replacement = "Vesta-Admin-2026-Strong!";

    private BootstrapAdministratorSeeder Seeder(string email, string password = Temporary) =>
        new(
            fx.Api.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new RaaviAuthenticationOptions { Bootstrap = new() { SystemAdministrator = new() { Email = email, InitialPassword = password, DisplayName = "Admin" } } }),
            fx.Api.Services.GetRequiredService<IClock>(),
            NullLogger<BootstrapAdministratorSeeder>.Instance);

    private static string NewEmail() => $"{SqlServerFixture.Unique("boot").ToLowerInvariant()}@vesta.test";

    private async Task<Browser> SystemBrowserAsync()
    {
        var b = new Browser(fx.Api, SqlServerFixture.SystemHost);
        await b.PrimeAsync();
        return b;
    }

    [Fact]
    public async Task Settings_decide_whether_an_account_is_created_and_a_waiting_account_gets_its_temporary_password_reapplied()
    {
        Assert.Equal(BootstrapOutcome.NotConfigured, await Seeder(string.Empty).EnsureAsync(false, default));
        Assert.Equal(BootstrapOutcome.InvalidSettings, await Seeder("not-an-address").EnsureAsync(false, default));

        var email = NewEmail();
        Assert.Equal(BootstrapOutcome.Created, await Seeder(email).EnsureAsync(false, default));
        Assert.Equal(BootstrapOutcome.TemporaryPasswordReapplied, await Seeder(email, "Other-temp-1").EnsureAsync(false, default));

        // The reapplied password is the one that works now.
        using var browser = await SystemBrowserAsync();
        using var old = await browser.PostAsync("api/v1/auth/system/first-login", new { email, password = Temporary });
        await Browser.AssertStatusAsync(old, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        await browser.PostOkAsync("api/v1/auth/system/first-login", new { email, password = "Other-temp-1" });

        // In the shared test database other administrators exist, so the start-up path (only when none exists) must not create anything.
        var another = NewEmail();
        Assert.Equal(BootstrapOutcome.AdministratorExists, await Seeder(another).EnsureAsync(true, default));
        using var none = await browser.PostAsync("api/v1/auth/system/first-login", new { email = another, password = Temporary });
        await Browser.AssertStatusAsync(none, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
    }

    [Fact]
    public async Task First_sign_in_forces_a_new_password_and_an_authenticator_and_the_temporary_password_never_opens_a_session()
    {
        var email = NewEmail();
        await Seeder(email).EnsureAsync(false, default);
        using var browser = await SystemBrowserAsync();

        // The temporary password alone gives no session, only the hint to start the first sign-in.
        using var login = await browser.PostAsync("api/v1/auth/system/login", new { email, password = Temporary, code = "000000" });
        await Browser.AssertStatusAsync(login, HttpStatusCode.Unauthorized, "auth.first_login_required");
        using var me = await browser.GetAsync("api/v1/auth/me");
        Assert.Equal(HttpStatusCode.Unauthorized, me.StatusCode);

        using var wrong = await browser.PostAsync("api/v1/auth/system/first-login", new { email, password = "wrong-password" });
        await Browser.AssertStatusAsync(wrong, HttpStatusCode.Unauthorized, "auth.invalid_credentials");

        var started = await browser.PostOkAsync("api/v1/auth/system/first-login", new { email, password = Temporary });
        var url = new Uri(started.GetProperty("acceptUrl").GetString()!);
        Assert.Equal("/system/setup", url.AbsolutePath);
        var token = url.Fragment["#token=".Length..];

        var info = await browser.PostOkAsync("api/v1/auth/system/setup/inspect", new { token });
        Assert.True(info.GetProperty("passwordChangeRequired").GetBoolean());
        var begin = await browser.PostOkAsync("api/v1/auth/system/setup/begin", new { token });
        var secret = Base32.Decode(begin.GetProperty("secret").GetString()!);
        var code = TotpService.Code(secret, TotpService.TimeStep(DateTimeOffset.UtcNow));

        // The replacement must meet the policy, differ from the temporary password and the temporary one must be right.
        foreach (var (current, replacement, expected, status) in new[]
        {
            (Temporary, "short", "password.length", HttpStatusCode.BadRequest),
            (Temporary, Temporary, "password.unchanged", HttpStatusCode.BadRequest),
            (Temporary, string.Empty, "password.length", HttpStatusCode.BadRequest),
            ("not-the-temporary", Replacement, "auth.invalid_credentials", HttpStatusCode.Unauthorized),
        })
        {
            using var bad = await browser.PostAsync("api/v1/auth/system/setup/complete", new { token, displayName = "Root", password = current, code, newPassword = replacement });
            await Browser.AssertStatusAsync(bad, status, expected);
        }

        await browser.PostOkAsync("api/v1/auth/system/setup/complete", new { token, displayName = "Root", password = Temporary, code, newPassword = Replacement });
        Assert.True((await browser.GetJsonAsync("api/v1/auth/me")).GetProperty("isSystemAdministrator").GetBoolean());

        // From now on: the temporary password is dead, the new one needs a fresh authenticator code, and the first-sign-in door is closed.
        using var fresh = await SystemBrowserAsync();
        var nextCode = TotpService.Code(secret, TotpService.TimeStep(DateTimeOffset.UtcNow) + 1);
        using var oldPassword = await fresh.PostAsync("api/v1/auth/system/login", new { email, password = Temporary, code = nextCode });
        await Browser.AssertStatusAsync(oldPassword, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        using var closed = await fresh.PostAsync("api/v1/auth/system/first-login", new { email, password = Replacement });
        await Browser.AssertStatusAsync(closed, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        await fresh.PostOkAsync("api/v1/auth/system/login", new { email, password = Replacement, code = nextCode });
        Assert.True((await fresh.GetJsonAsync("api/v1/auth/me")).GetProperty("isSystemAdministrator").GetBoolean());

        // A finished account is never touched again by the seeder.
        Assert.Equal(BootstrapOutcome.AdministratorExists, await Seeder(email, "Another-temp-9").EnsureAsync(false, default));
        using var stillClosed = await fresh.PostAsync("api/v1/auth/system/first-login", new { email, password = "Another-temp-9" });
        await Browser.AssertStatusAsync(stillClosed, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
    }

    [Fact]
    public async Task Wrong_temporary_passwords_lock_the_account_like_any_sign_in()
    {
        var email = NewEmail();
        await Seeder(email).EnsureAsync(false, default);
        using var browser = await SystemBrowserAsync();
        for (var i = 0; i < 5; i++)
        {
            using var bad = await browser.PostAsync("api/v1/auth/system/first-login", new { email, password = "guess-" + i });
            await Browser.AssertStatusAsync(bad, HttpStatusCode.Unauthorized, "auth.invalid_credentials");
        }

        using var locked = await browser.PostAsync("api/v1/auth/system/first-login", new { email, password = Temporary });
        await Browser.AssertStatusAsync(locked, HttpStatusCode.Unauthorized, "auth.invalid_credentials"); // correct password, but locked out
    }
}
