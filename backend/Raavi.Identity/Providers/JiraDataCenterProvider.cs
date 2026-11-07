using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Raavi.Identity.Providers;

public enum JiraMode
{
    /// <summary>Jira Data Center ≥ 8.22 incoming OAuth 2.0 application link.</summary>
    OAuth2 = 1,

    /// <summary>OAuth 1.0a application links (older versions) — not available in this build; use OAuth2 or a shared IdP (OIDC).</summary>
    OAuth1 = 2,
}

public sealed class JiraSettings
{
    public Uri? BaseUrl { get; set; }

    public JiraMode Mode { get; set; } = JiraMode.OAuth2;

    public string ClientId { get; set; } = string.Empty;

    public string ClientSecret { get; set; } = string.Empty;

    public IList<string> Scopes { get; } = [];

    /// <summary>Jira application links usually accept one fixed redirect URI → relay through the system host by default.</summary>
    public CallbackMode CallbackMode { get; set; } = CallbackMode.Relay;

    /// <summary>Jira administrators can set arbitrary e-mail addresses; never treated as verified unless explicitly trusted.</summary>
    public bool TrustEmail { get; set; }

    /// <summary>Fetch the user's Jira groups (for tenant group gates and mirroring).</summary>
    public bool IncludeGroups { get; set; } = true;

    /// <summary>Development only: allow an http:// base URL.</summary>
    public bool AllowHttp { get; set; }
}

/// <summary>
/// Sign-in with a self-hosted Jira Data Center account (ADR-007). OAuth 2.0 authorization code + PKCE against
/// <c>/rest/oauth2/latest/authorize</c> and <c>/token</c>; identity from <c>GET /rest/api/2/myself</c>; subject = Jira user
/// <c>key</c> (stable across renames). The access token is used only for the identity calls and then discarded.
/// Passwords are never sent to Jira by Raavi.
/// </summary>
public sealed partial class JiraDataCenterProvider(string providerId, JiraSettings settings, IHttpClientFactory http, ILogger logger) : IAuthenticationProvider
{
    public static readonly Version MinimumOAuth2Version = new(8, 22);

    public string ProviderId { get; } = providerId;

    public AuthenticationProviderType Type => AuthenticationProviderType.JiraDataCenter;

    public AuthenticationFlow SupportedFlows => AuthenticationFlow.Redirect;

    public CallbackMode CallbackMode => settings.CallbackMode;

    private string Base => settings.BaseUrl!.ToString().TrimEnd('/');

    public IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = new List<string>();
        if (settings.BaseUrl is not { IsAbsoluteUri: true })
        {
            errors.Add("BaseUrl must be an absolute URL.");
        }
        else if (settings.BaseUrl.Scheme != Uri.UriSchemeHttps && !settings.AllowHttp)
        {
            errors.Add("BaseUrl must use https.");
        }

        if (settings.Mode != JiraMode.OAuth2)
        {
            errors.Add("Only Mode=OAuth2 (Jira Data Center 8.22+) is available. For older Jira, federate Jira and Raavi to the same IdP and use an OpenIdConnect provider (ADR-007 'SharedIdp').");
        }

        if (string.IsNullOrWhiteSpace(settings.ClientId) || string.IsNullOrWhiteSpace(settings.ClientSecret))
        {
            errors.Add("ClientId and ClientSecret of the incoming OAuth 2.0 application link are required.");
        }

        return errors;
    }

    /// <summary>Reads <c>/rest/api/2/serverInfo</c> and reports whether the instance can act as OAuth 2.0 provider.</summary>
    public async ValueTask<ProviderHealth> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var client = http.CreateClient(IdentityHttp.ClientName);
            var info = await client.GetFromJsonAsync<JsonElement>(new Uri($"{Base}/rest/api/2/serverInfo"), cancellationToken).ConfigureAwait(false);
            var versionText = info.TryGetProperty("version", out var v) ? v.GetString() : null;
            var deployment = info.TryGetProperty("deploymentType", out var d) ? d.GetString() : null;
            var supported = TryParseVersion(versionText, out var version) && version >= MinimumOAuth2Version;
            var detail = string.Create(CultureInfo.InvariantCulture, $"version={versionText}; deploymentType={deployment}");
            return supported
                ? new ProviderHealth(true, "ok", detail)
                : new ProviderHealth(false, "jira_version_unsupported", detail + "; OAuth 2.0 provider requires Jira Data Center 8.22+");
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException)
        {
            return new ProviderHealth(false, "jira_unreachable", null);
        }
    }

    public ValueTask<Uri> BeginRedirectAsync(RedirectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ValueTask.FromResult(OAuth.BuildUrl($"{Base}/rest/oauth2/latest/authorize", new Dictionary<string, string>
        {
            ["client_id"] = settings.ClientId,
            ["redirect_uri"] = request.CallbackUri.ToString(),
            ["response_type"] = "code",
            ["scope"] = settings.Scopes.Count > 0 ? string.Join(' ', settings.Scopes) : "READ",
            ["state"] = request.State,
            ["code_challenge"] = request.CodeChallenge,
            ["code_challenge_method"] = "S256",
        }));
    }

    public async ValueTask<ExternalAuthenticationResult> CompleteRedirectAsync(RedirectCallback callback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(callback);
        if (!callback.Parameters.TryGetValue("code", out var code) || string.IsNullOrEmpty(code))
        {
            return ExternalAuthenticationResult.Failure("auth.provider_error");
        }

        var tokens = await OAuth.ExchangeCodeAsync(http, $"{Base}/rest/oauth2/latest/token", settings.ClientId, settings.ClientSecret, code, callback.CallbackUri, callback.CodeVerifier, cancellationToken).ConfigureAwait(false);
        if (tokens is null || !tokens.Value.TryGetProperty("access_token", out var at) || at.GetString() is not { Length: > 0 } accessToken)
        {
            LogFailure(logger, ProviderId, "token_exchange");
            return ExternalAuthenticationResult.Failure("auth.provider_error");
        }

        using var client = http.CreateClient(IdentityHttp.ClientName);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        JsonElement me;
        try
        {
            me = await client.GetFromJsonAsync<JsonElement>(new Uri($"{Base}/rest/api/2/myself"), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException)
        {
            LogFailure(logger, ProviderId, "myself_failed");
            return ExternalAuthenticationResult.Failure("auth.provider_error");
        }

        var key = Str(me, "key");
        if (string.IsNullOrEmpty(key))
        {
            return ExternalAuthenticationResult.Failure("auth.provider_error");
        }

        if (me.TryGetProperty("active", out var active) && active.ValueKind == JsonValueKind.False)
        {
            return ExternalAuthenticationResult.Failure("auth.external_account_inactive");
        }

        var groups = new List<string>();
        if (settings.IncludeGroups)
        {
            try
            {
                var user = await client.GetFromJsonAsync<JsonElement>(new Uri($"{Base}/rest/api/2/user?key={Uri.EscapeDataString(key)}&expand=groups"), cancellationToken).ConfigureAwait(false);
                if (user.TryGetProperty("groups", out var g) && g.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
                {
                    groups.AddRange(items.EnumerateArray().Select(i => Str(i, "name")).OfType<string>().Take(500));
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or JsonException)
            {
                LogFailure(logger, ProviderId, "groups_failed");
            }
        }

        var email = Str(me, "emailAddress");
        return ExternalAuthenticationResult.Success(new ExternalIdentity(ProviderId, key, Str(me, "name"), email, settings.TrustEmail && email is not null, Str(me, "displayName"), groups));
    }

    public ValueTask<ExternalAuthenticationResult> AuthenticateWithCredentialsAsync(CredentialLogin login, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ExternalAuthenticationResult.NotSupported);

    public static bool TryParseVersion(string? text, out Version version)
    {
        version = new Version(0, 0);
        var numeric = new string((text ?? string.Empty).TakeWhile(c => char.IsAsciiDigit(c) || c == '.').ToArray());
        return Version.TryParse(numeric, out version!);
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    [LoggerMessage(Level = LogLevel.Warning, Message = "Jira provider {ProviderId} rejected a login: {Reason}")]
    private static partial void LogFailure(ILogger logger, string providerId, string reason);
}

public sealed class JiraDataCenterProviderFactory(IHttpClientFactory http, ILoggerFactory logs) : IAuthenticationProviderFactory
{
    public AuthenticationProviderType Type => AuthenticationProviderType.JiraDataCenter;

    public IAuthenticationProvider Create(ProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new JiraDataCenterProvider(definition.Id, definition.Settings?.Get<JiraSettings>() ?? new JiraSettings(), http, logs.CreateLogger<JiraDataCenterProvider>());
    }
}
