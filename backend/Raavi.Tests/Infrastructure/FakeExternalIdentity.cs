using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Raavi.Identity.Providers;

namespace Raavi.Tests.Infrastructure;

/// <summary>
/// In-process fake of an OpenID Provider (https://idp.test) and a Jira Data Center (https://jira.test).
/// Codes are issued by tests; the token endpoints verify client credentials, redirect_uri and PKCE (S256) like a real server.
/// </summary>
public sealed class FakeExternalIdentity : HttpMessageHandler
{
    public const string Issuer = "https://idp.test";
    public const string ClientId = "raavi";
    public const string ClientSecret = "test-client-secret";
    public const string JiraBase = "https://jira.test";

    private readonly RSA _key = RSA.Create(2048);
    private readonly RSA _otherKey = RSA.Create(2048);
    private readonly ConcurrentDictionary<string, CodeGrant> _codes = new();
    private readonly ConcurrentDictionary<string, JiraUser> _jiraTokens = new();

    public string JiraVersion { get; set; } = "9.12.5";

    private int _tokenRequests;

    public int TokenRequests => _tokenRequests;

    public sealed record CodeGrant(string Challenge, string RedirectUri, Func<string> Issue);

    public sealed record JiraUser(string Key, string Name, string? Email, string DisplayName, IReadOnlyList<string> Groups, bool Active = true);

    /// <summary>Issues an authorization code that the OIDC token endpoint redeems for an id_token with these claims.</summary>
    public string IssueOidcCode(string challenge, string redirectUri, string subject, string? email, bool emailVerified, string nonce, string[]? groups = null, bool wrongKey = false, string issuer = Issuer, string audience = ClientId)
    {
        var code = Guid.NewGuid().ToString("N");
        _codes[code] = new CodeGrant(challenge, redirectUri, () =>
        {
            var claims = new Dictionary<string, object> { ["sub"] = subject, ["nonce"] = nonce, ["name"] = "OIDC " + subject, ["preferred_username"] = subject };
            if (email is not null)
            {
                claims["email"] = email;
                claims["email_verified"] = emailVerified;
            }

            if (groups is { Length: > 0 })
            {
                claims["groups"] = groups;
            }

            var key = new RsaSecurityKey(wrongKey ? _otherKey : _key) { KeyId = wrongKey ? "other" : "k1" };
            var idToken = new JsonWebTokenHandler().CreateToken(new SecurityTokenDescriptor
            {
                Issuer = issuer,
                Audience = audience,
                Claims = claims,
                IssuedAt = DateTime.UtcNow,
                NotBefore = DateTime.UtcNow.AddSeconds(-5),
                Expires = DateTime.UtcNow.AddMinutes(5),
                SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.RsaSha256),
            });
            return JsonSerializer.Serialize(new { access_token = "at", token_type = "Bearer", id_token = idToken });
        });
        return code;
    }

    public string IssueJiraCode(string challenge, string redirectUri, JiraUser user)
    {
        var code = Guid.NewGuid().ToString("N");
        _codes[code] = new CodeGrant(challenge, redirectUri, () =>
        {
            var token = "jira-" + Guid.NewGuid().ToString("N");
            _jiraTokens[token] = user;
            return JsonSerializer.Serialize(new { access_token = token, token_type = "bearer", expires_in = 3600 });
        });
        return code;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var url = request.RequestUri!;
        var path = url.GetLeftPart(UriPartial.Path);
        return path switch
        {
            Issuer + "/.well-known/openid-configuration" => Json(new
            {
                issuer = Issuer,
                authorization_endpoint = Issuer + "/authorize",
                token_endpoint = Issuer + "/token",
                jwks_uri = Issuer + "/jwks",
                response_types_supported = new[] { "code" },
                subject_types_supported = new[] { "public" },
                id_token_signing_alg_values_supported = new[] { "RS256" },
            }),
            Issuer + "/jwks" => Json(new { keys = new[] { Jwk(_key, "k1") } }),
            Issuer + "/token" or JiraBase + "/rest/oauth2/latest/token" => await Token(request).ConfigureAwait(false),
            JiraBase + "/rest/api/2/serverInfo" => Json(new { version = JiraVersion, deploymentType = "DataCenter", baseUrl = JiraBase }),
            JiraBase + "/rest/api/2/myself" => Jira(request, u => new { key = u.Key, name = u.Name, emailAddress = u.Email, displayName = u.DisplayName, active = u.Active }),
            JiraBase + "/rest/api/2/user" => Jira(request, u => new { key = u.Key, groups = new { size = u.Groups.Count, items = u.Groups.Select(g => new { name = g }) } }),
            _ => new HttpResponseMessage(HttpStatusCode.NotFound),
        };
    }

    private async Task<HttpResponseMessage> Token(HttpRequestMessage request)
    {
        Interlocked.Increment(ref _tokenRequests);
        var form = (await request.Content!.ReadAsStringAsync().ConfigureAwait(false)).Split('&')
            .Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p.Length > 1 ? p[1].Replace('+', ' ') : string.Empty));
        if (form.GetValueOrDefault("grant_type") != "authorization_code"
            || form.GetValueOrDefault("client_id") != ClientId
            || form.GetValueOrDefault("client_secret") != ClientSecret
            || !_codes.TryRemove(form.GetValueOrDefault("code") ?? string.Empty, out var grant)
            || grant.RedirectUri != form.GetValueOrDefault("redirect_uri")
            || Raavi.Identity.ExternalLogin.Pkce.Challenge(form.GetValueOrDefault("code_verifier") ?? string.Empty) != grant.Challenge)
        {
            return new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = JsonContent.Create(new { error = "invalid_grant" }) };
        }

        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(grant.Issue(), Encoding.UTF8, "application/json") };
    }

    private HttpResponseMessage Jira(HttpRequestMessage request, Func<JiraUser, object> body)
    {
        var auth = request.Headers.Authorization;
        return auth?.Scheme == "Bearer" && auth.Parameter is not null && _jiraTokens.TryGetValue(auth.Parameter, out var user)
            ? Json(body(user))
            : new HttpResponseMessage(HttpStatusCode.Unauthorized);
    }

    private static HttpResponseMessage Json(object body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private static object Jwk(RSA rsa, string kid)
    {
        var p = rsa.ExportParameters(false);
        return new { kty = "RSA", use = "sig", alg = "RS256", kid, n = Base64UrlEncoder.Encode(p.Modulus), e = Base64UrlEncoder.Encode(p.Exponent) };
    }

}

/// <summary>Lets HttpClientFactory own (and dispose) a wrapper while the shared fake stays alive across handler rotations.</summary>
public sealed class SharedHandler(HttpMessageHandler inner) : HttpMessageHandler
{
    private readonly HttpMessageInvoker _invoker = new(inner, disposeHandler: false);

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => _invoker.SendAsync(request, cancellationToken);

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _invoker.Dispose();
        }

        base.Dispose(disposing);
    }
}

/// <summary>Fake directory for LDAP/AD provider tests. Records every filter and bind.</summary>
public sealed class FakeLdapDirectory : ILdapDirectoryFactory, ILdapDirectory
{
    private readonly ConcurrentDictionary<string, (string Password, LdapUser User)> _users = new(StringComparer.OrdinalIgnoreCase);

    public ConcurrentQueue<string> Filters { get; } = new();

    private int _binds;

    public int Binds => _binds;

    public ILdapDirectory Create(LdapSettings settings) => this;

    public Guid AddUser(string username, string password, string? mail, params string[] groupDns)
    {
        var guid = Guid.NewGuid();
        var attrs = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase)
        {
            ["objectGUID"] = [guid.ToByteArray()],
            ["sAMAccountName"] = [Encoding.UTF8.GetBytes(username)],
            ["displayName"] = [Encoding.UTF8.GetBytes("AD " + username)],
            ["memberOf"] = groupDns.Select(Encoding.UTF8.GetBytes).ToList(),
        };
        if (mail is not null)
        {
            attrs["mail"] = [Encoding.UTF8.GetBytes(mail)];
        }

        _users[username] = (password, new LdapUser($"CN={username},OU=Users,DC=test", attrs));
        return guid;
    }

    public Task<LdapUser?> FindUserAsync(string filter, CancellationToken ct)
    {
        Filters.Enqueue(filter);
        var match = _users.FirstOrDefault(u => filter == LdapProvider.BuildFilter("(&(objectClass=user)(sAMAccountName={0}))", u.Key));
        return Task.FromResult(match.Key is null ? null : (LdapUser?)match.Value.User);
    }

    public Task<bool> VerifyPasswordAsync(string distinguishedName, string password, CancellationToken ct)
    {
        Interlocked.Increment(ref _binds);
        var user = _users.Values.FirstOrDefault(u => u.User.DistinguishedName == distinguishedName);
        return Task.FromResult(user.User is not null && !string.IsNullOrEmpty(password) && user.Password == password);
    }
}
