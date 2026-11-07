using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Raavi.Domain.Common;
using Raavi.Identity;
using Raavi.Identity.Domain;
using Raavi.Identity.Providers;
using Raavi.Identity.Security;
using Raavi.Infrastructure;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Identity;

[Collection(SqlTestGroup.Name)]
public sealed class IdentityConfigurationTests(SqlServerFixture fx)
{
    private ServiceProvider Build(string environment, params (string Key, string Value)[] overrides)
    {
        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings { EnvironmentName = environment });
        builder.Configuration.AddInMemoryCollection(fx.Settings);
        builder.Configuration.AddInMemoryCollection(overrides.ToDictionary(o => o.Key, o => (string?)o.Value));
        builder.Services.AddRaaviInfrastructure(builder.Configuration);
        builder.Services.AddIdentityCore(builder.Configuration);
        return builder.Services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public void Insecure_http_cookie_mode_is_refused_outside_development(string environment)
    {
        using var sp = Build(environment, ("Raavi:Authentication:Session:AllowInsecureHttp", "true"));
        var ex = Assert.Throws<OptionsValidationException>(() => sp.GetRequiredService<IOptions<RaaviAuthenticationOptions>>().Value);
        Assert.Contains("Development", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Strict_mode_uses_host_prefixed_cookies()
    {
        using var sp = Build("Production");
        var session = sp.GetRequiredService<IOptions<RaaviAuthenticationOptions>>().Value.Session;
        Assert.False(session.AllowInsecureHttp);
        Assert.Equal("__Host-raavi", session.EffectiveCookieName);
        Assert.Equal("__Host-raavi-af", session.AntiforgeryCookieName);
    }

    [Fact]
    public void Enabled_identity_provider_of_an_unavailable_type_fails_startup()
    {
        using var sp = Build("Production",
            ("Raavi:IdentityProviders:0:Id", "local"), ("Raavi:IdentityProviders:0:Type", "Local"), ("Raavi:IdentityProviders:0:Enabled", "true"),
            ("Raavi:IdentityProviders:1:Id", "jira-eng"), ("Raavi:IdentityProviders:1:Type", "JiraDataCenter"), ("Raavi:IdentityProviders:1:Enabled", "true"));
        var ex = Assert.Throws<InvalidOperationException>(() => sp.GetRequiredService<AuthenticationProviderRegistry>());
        Assert.Contains("jira-eng", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("short", "password.length")]
    [InlineData("aaaaaaaaaaaaaaaa", "password.too_simple")]
    [InlineData("sara.ahmadi-Secret-1", "password.contains_email")]
    public void Password_policy(string password, string code)
    {
        using var sp = Build("Production");
        var ex = Assert.Throws<DomainException>(() => sp.GetRequiredService<PasswordService>().EnsureAcceptable(password, "sara.ahmadi@example.com"));
        Assert.Equal(code, ex.Code);
    }

    [Fact]
    public void Password_hash_round_trip_and_session_expiry_rules()
    {
        using var sp = Build("Production");
        var passwords = sp.GetRequiredService<PasswordService>();
        var user = User.CreateLocal("a@example.com", "A", DateTimeOffset.UtcNow);
        user.SetPasswordHash(passwords.Hash(user, "Correct-Horse-Battery-9"));
        Assert.True(passwords.Verify(user, "Correct-Horse-Battery-9").Valid);
        Assert.False(passwords.Verify(user, "correct-horse-battery-9").Valid);
        Assert.StartsWith("AQAAAA", user.PasswordHash, StringComparison.Ordinal); // ASP.NET Core Identity v3 format, never plaintext

        var now = DateTimeOffset.UtcNow;
        var s = AuthenticationSession.Issue("h", user.Id, Guid.NewGuid(), "local", false, user.SecurityStamp, now, TimeSpan.FromMinutes(60), TimeSpan.FromHours(12), null, null);
        Assert.True(s.IsValid(now.AddMinutes(59)));
        Assert.False(s.IsValid(now.AddMinutes(61)));          // idle timeout
        s.Touch(now.AddHours(11.5), TimeSpan.FromMinutes(60));
        Assert.Equal(s.AbsoluteExpiresAt, s.IdleExpiresAt);   // idle extension never passes the absolute limit
        s.Revoke(now, "test");
        Assert.False(s.IsValid(now));
    }

    [Theory]
    [InlineData("a@b.c", true)]
    [InlineData("sara@acme.test", true)]
    [InlineData("no-at-sign", false)]
    [InlineData("@example.com", false)]
    [InlineData("a@@example.com", false)]
    [InlineData("a b@example.com", false)]
    [InlineData("a@example", false)]
    public void Email_validation(string email, bool valid)
    {
        if (valid)
        {
            Assert.Equal(email, User.ValidateEmail(email));
        }
        else
        {
            Assert.Throws<DomainException>(() => User.ValidateEmail(email));
        }
    }
}
