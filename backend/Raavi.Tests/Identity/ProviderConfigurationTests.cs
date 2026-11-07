using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Raavi.Identity.ExternalLogin;
using Raavi.Identity.Providers;

namespace Raavi.Tests.Identity;

/// <summary>Configuration validation and pure helpers of the external providers.</summary>
public sealed class ProviderConfigurationTests
{
    private static IReadOnlyList<string> Validate(IAuthenticationProviderFactory factory, Dictionary<string, string?> settings)
    {
        var section = new ConfigurationBuilder().AddInMemoryCollection(settings.ToDictionary(kv => "p:" + kv.Key, kv => kv.Value)).Build().GetSection("p");
        return factory.Create(new ProviderDefinition { Id = "x", Settings = section }).ValidateConfiguration();
    }

    private static readonly IHttpClientFactory Http = new StubHttpFactory();

    [Fact]
    public void Jira_requires_https_oauth2_and_client_credentials()
    {
        var f = new JiraDataCenterProviderFactory(Http, NullLoggerFactory.Instance);
        Assert.Empty(Validate(f, new() { ["BaseUrl"] = "https://jira.company.local", ["ClientId"] = "c", ["ClientSecret"] = "s" }));
        Assert.Contains(Validate(f, new() { ["BaseUrl"] = "http://jira.company.local", ["ClientId"] = "c", ["ClientSecret"] = "s" }), e => e.Contains("https", StringComparison.Ordinal));
        Assert.Contains(Validate(f, new() { ["BaseUrl"] = "https://jira", ["Mode"] = "OAuth1", ["ClientId"] = "c", ["ClientSecret"] = "s" }), e => e.Contains("OAuth2", StringComparison.Ordinal));
        Assert.Contains(Validate(f, new() { ["BaseUrl"] = "https://jira" }), e => e.Contains("ClientSecret", StringComparison.Ordinal));
    }

    [Fact]
    public void Ldap_requires_tls_base_dn_and_placeholder()
    {
        var f = new LdapProviderFactory(AuthenticationProviderType.Ldap, new Infrastructure.FakeLdapDirectory(), NullLoggerFactory.Instance);
        Assert.Empty(Validate(f, new() { ["Server"] = "ldaps://dc:636", ["BaseDn"] = "DC=x" }));
        Assert.Empty(Validate(f, new() { ["Server"] = "ldap://dc:389", ["StartTls"] = "true", ["BaseDn"] = "DC=x" }));
        Assert.Contains(Validate(f, new() { ["Server"] = "ldap://dc:389", ["BaseDn"] = "DC=x" }), e => e.Contains("Unencrypted", StringComparison.Ordinal));
        Assert.Contains(Validate(f, new() { ["Server"] = "ldaps://dc", ["BaseDn"] = "DC=x", ["UserFilter"] = "(uid=bob)" }), e => e.Contains("{0}", StringComparison.Ordinal));
    }

    [Fact]
    public void Oidc_requires_https_authority_and_secret()
    {
        var f = new OpenIdConnectProviderFactory(Http, NullLoggerFactory.Instance);
        Assert.Empty(Validate(f, new() { ["Authority"] = "https://idp.example", ["ClientId"] = "c", ["ClientSecret"] = "s" }));
        Assert.Contains(Validate(f, new() { ["Authority"] = "http://idp.example", ["ClientId"] = "c", ["ClientSecret"] = "s" }), e => e.Contains("https", StringComparison.Ordinal));
        Assert.Contains(Validate(f, new() { ["Authority"] = "https://idp.example", ["ClientId"] = "c" }), e => e.Contains("ClientSecret", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("bob", "bob")]
    [InlineData("*", "\\2a")]
    [InlineData("a)(uid=*", "a\\29\\28uid=\\2a")]
    [InlineData("back\\slash", "back\\5cslash")]
    [InlineData("nul\0", "nul\\00")]
    [InlineData("سارا", "\\d8\\b3\\d8\\a7\\d8\\b1\\d8\\a7")]
    public void Ldap_filter_values_are_rfc4515_escaped(string input, string expected) =>
        Assert.Equal(expected, LdapProvider.EscapeFilterValue(input));

    [Theory]
    [InlineData("CN=Engineering,OU=Groups,DC=x", "Engineering")]
    [InlineData("CN=R\\, and D,OU=Groups,DC=x", "R, and D")]
    [InlineData("OU=Groups,DC=x", null)]
    public void Ldap_group_common_name(string dn, string? expected) => Assert.Equal(expected, LdapProvider.CommonName(dn));

    [Theory]
    [InlineData("9.12.5", true)]
    [InlineData("8.22.0", true)]
    [InlineData("8.21.3", false)]
    [InlineData("10.3.0-SNAPSHOT", true)]
    public void Jira_oauth2_minimum_version(string version, bool supported)
    {
        Assert.True(JiraDataCenterProvider.TryParseVersion(version, out var v));
        Assert.Equal(supported, v >= JiraDataCenterProvider.MinimumOAuth2Version);
    }

    [Fact]
    public void Pkce_s256_matches_rfc7636_example() =>
        Assert.Equal("E9Melhoa2OwvFrEMTJguCHaoeK1t8URWbuGJSstw-cM", Pkce.Challenge("dBjftJeZ4CVP-mB92K27uhbUJU1p1r_wW1gFWFOEjXk"));

    [Theory]
    [InlineData("/admin", "/admin")]
    [InlineData("//evil.com", "/")]
    [InlineData("https://evil.com", "/")]
    [InlineData("/x?u=https://evil.com", "/")]
    [InlineData(null, "/")]
    public void Return_paths(string? input, string expected) => Assert.Equal(expected, ExternalLoginFlow.SafeReturnPath(input));

    private sealed class StubHttpFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
