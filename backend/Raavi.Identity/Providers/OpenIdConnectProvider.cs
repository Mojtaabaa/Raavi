using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Raavi.Identity.Providers;

/// <summary>Settings of an <c>OpenIdConnect</c> definition (identity-providers.json). Secrets come from env/secret store.</summary>
public sealed class OidcSettings
{
    public Uri? Authority { get; set; }

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public IList<string> Scopes { get; } = [];

    public string SubjectClaim { get; set; } = "sub";

    public string EmailClaim { get; set; } = "email";

    public string NameClaim { get; set; } = "name";

    public string UsernameClaim { get; set; } = "preferred_username";

    public string GroupsClaim { get; set; } = "groups";

    /// <summary>When false (default) the e-mail counts as verified only if the IdP asserts <c>email_verified=true</c>.</summary>
    public bool TrustEmail { get; set; }

    public CallbackMode CallbackMode { get; set; } = CallbackMode.PerHost;

    public bool RequireHttpsMetadata { get; set; } = true;
}

/// <summary>
/// OpenID Connect relying party: authorization code + PKCE (S256) + nonce, confidential client (client_secret_post),
/// id_token validated against the discovery document's JWKS (signature, issuer, audience, lifetime) and the nonce.
/// Also used for "SharedIdp" Jira integration (Jira federated to the same IdP, ADR-007).
/// </summary>
public sealed partial class OpenIdConnectProvider : IAuthenticationProvider
{
    private readonly OidcSettings _settings;
    private readonly IHttpClientFactory _http;
    private readonly ILogger _logger;
    private readonly ConfigurationManager<OpenIdConnectConfiguration>? _metadata;

    public OpenIdConnectProvider(string providerId, OidcSettings settings, IHttpClientFactory http, ILogger logger)
    {
        ProviderId = providerId;
        _settings = settings;
        _http = http;
        _logger = logger;
        if (settings.Authority is not null)
        {
            var address = new Uri(settings.Authority.ToString().TrimEnd('/') + "/.well-known/openid-configuration");
            _metadata = new ConfigurationManager<OpenIdConnectConfiguration>(
                address.ToString(),
                new OpenIdConnectConfigurationRetriever(),
                new HttpDocumentRetriever(http.CreateClient(IdentityHttp.ClientName)) { RequireHttps = settings.RequireHttpsMetadata });
        }
    }

    public string ProviderId { get; }

    public AuthenticationProviderType Type => AuthenticationProviderType.OpenIdConnect;

    public AuthenticationFlow SupportedFlows => AuthenticationFlow.Redirect;

    public CallbackMode CallbackMode => _settings.CallbackMode;

    public IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = new List<string>();
        if (_settings.Authority is not { IsAbsoluteUri: true })
        {
            errors.Add("Authority must be an absolute URL.");
        }
        else if (_settings.RequireHttpsMetadata && _settings.Authority.Scheme != Uri.UriSchemeHttps)
        {
            errors.Add("Authority must use https (set RequireHttpsMetadata=false only for development).");
        }

        if (string.IsNullOrWhiteSpace(_settings.ClientId))
        {
            errors.Add("ClientId is required.");
        }

        if (string.IsNullOrWhiteSpace(_settings.ClientSecret))
        {
            errors.Add("ClientSecret is required (set it through the secret store / environment).");
        }

        return errors;
    }

    public async ValueTask<ProviderHealth> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            var config = await _metadata!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
            return new ProviderHealth(config.SigningKeys.Count > 0, config.SigningKeys.Count > 0 ? "ok" : "no_signing_keys", config.Issuer);
        }
        catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or IOException)
        {
            return new ProviderHealth(false, "metadata_unreachable", null);
        }
    }

    public async ValueTask<Uri> BeginRedirectAsync(RedirectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var config = await _metadata!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var scopes = _settings.Scopes.Count > 0 ? _settings.Scopes : ["openid", "profile", "email"];
        return OAuth.BuildUrl(config.AuthorizationEndpoint, new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["client_id"] = _settings.ClientId,
            ["redirect_uri"] = request.CallbackUri.ToString(),
            ["scope"] = string.Join(' ', scopes),
            ["state"] = request.State,
            ["nonce"] = request.Nonce,
            ["code_challenge"] = request.CodeChallenge,
            ["code_challenge_method"] = "S256",
        });
    }

    public async ValueTask<ExternalAuthenticationResult> CompleteRedirectAsync(RedirectCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!callback.Parameters.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
        {
            return ExternalAuthenticationResult.Failure("auth.provider_error");
        }

        var config = await _metadata!.GetConfigurationAsync(cancellationToken).ConfigureAwait(false);
        var tokens = await OAuth.ExchangeCodeAsync(_http, config.TokenEndpoint, _settings.ClientId, _settings.ClientSecret, code, callback.CallbackUri, callback.CodeVerifier, cancellationToken).ConfigureAwait(false);
        if (tokens is null || !tokens.Value.TryGetProperty("id_token", out var idTokenElement) || idTokenElement.GetString() is not { } idToken)
        {
            LogFailure(_logger, ProviderId, "token_exchange");
            return ExternalAuthenticationResult.Failure("auth.provider_error");
        }

        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(idToken, new TokenValidationParameters
        {
            ValidIssuer = config.Issuer,
            ValidAudience = _settings.ClientId,
            IssuerSigningKeys = config.SigningKeys,
            ValidateIssuerSigningKey = true,
            RequireSignedTokens = true,
            RequireExpirationTime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
        }).ConfigureAwait(false);

        if (!validation.IsValid)
        {
            LogFailure(_logger, ProviderId, "id_token_invalid");
            return ExternalAuthenticationResult.Failure("auth.provider_error");
        }

        var claims = validation.ClaimsIdentity;
        if (claims.FindFirst("nonce")?.Value != callback.Nonce)
        {
            LogFailure(_logger, ProviderId, "nonce_mismatch");
            return ExternalAuthenticationResult.Failure("auth.provider_error");
        }

        var subject = claims.FindFirst(_settings.SubjectClaim)?.Value;
        if (string.IsNullOrEmpty(subject))
        {
            return ExternalAuthenticationResult.Failure("auth.provider_error");
        }

        var email = claims.FindFirst(_settings.EmailClaim)?.Value;
        var verified = email is not null && (_settings.TrustEmail || string.Equals(claims.FindFirst("email_verified")?.Value, "true", StringComparison.OrdinalIgnoreCase));
        var groups = claims.FindAll(_settings.GroupsClaim).Select(c => c.Value).Where(v => v.Length is > 0 and <= 256).Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();

        return ExternalAuthenticationResult.Success(new ExternalIdentity(
            ProviderId, subject, claims.FindFirst(_settings.UsernameClaim)?.Value, email, verified, claims.FindFirst(_settings.NameClaim)?.Value, groups));
    }

    public ValueTask<ExternalAuthenticationResult> AuthenticateWithCredentialsAsync(CredentialLogin login, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ExternalAuthenticationResult.NotSupported);

    [LoggerMessage(Level = LogLevel.Warning, Message = "OIDC provider {ProviderId} rejected a login: {Reason}")]
    private static partial void LogFailure(ILogger logger, string providerId, string reason);
}

public sealed class OpenIdConnectProviderFactory(IHttpClientFactory http, ILoggerFactory logs) : IAuthenticationProviderFactory
{
    public AuthenticationProviderType Type => AuthenticationProviderType.OpenIdConnect;

    public IAuthenticationProvider Create(ProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        var settings = definition.Settings?.Get<OidcSettings>() ?? new OidcSettings();
        return new OpenIdConnectProvider(definition.Id, settings, http, logs.CreateLogger<OpenIdConnectProvider>());
    }
}

/// <summary>Shared OAuth 2.0 helpers (URL building, authorization-code exchange with PKCE, client_secret_post).</summary>
public static class OAuth
{
    public static Uri BuildUrl(string endpoint, IReadOnlyDictionary<string, string> query)
    {
        var separator = endpoint.Contains('?', StringComparison.Ordinal) ? "&" : "?";
        return new Uri(endpoint + separator + string.Join("&", query.Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}")));
    }

    public static async Task<JsonElement?> ExchangeCodeAsync(IHttpClientFactory http, string tokenEndpoint, string clientId, string clientSecret, string code, Uri redirectUri, string codeVerifier, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        using var client = http.CreateClient(IdentityHttp.ClientName);
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri.ToString(),
            ["client_id"] = clientId,
            ["client_secret"] = clientSecret,
            ["code_verifier"] = codeVerifier,
        });
        using var response = await client.PostAsync(new Uri(tokenEndpoint), content, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        return await response.Content.ReadFromJsonAsync<JsonElement>(ct).ConfigureAwait(false);
    }
}

public static class IdentityHttp
{
    /// <summary>Named HttpClient for identity providers (timeouts, size limits; replaceable in tests).</summary>
    public const string ClientName = "raavi-identity";
}
