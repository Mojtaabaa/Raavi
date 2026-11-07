using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Common;
using Raavi.Identity.Providers;
using Raavi.Identity.Security;
using Raavi.Infrastructure.Tenancy;

namespace Raavi.Identity.ExternalLogin;

/// <summary>
/// Redirect-based sign-in and account linking (AUTHENTICATION_ARCHITECTURE.md §4):
///   begin (tenant host)  → pending login + browser-binding cookie → provider authorize URL
///   callback (tenant host, PerHost) or relay callback (system host, Relay) → back-channel code exchange
///   relay: one-time hand-off code → tenant host verifies tenant + browser binding → session.
/// The tenant can never change during the flow: it is fixed in the pending login and re-checked on the tenant host.
/// </summary>
public sealed partial class ExternalLoginFlow(
    AuthenticationProviderRegistry registry,
    ITenantProviderPolicyService policies,
    PendingLoginStore pending,
    ITenantContext tenant,
    ITenantUrlResolver urls,
    ICurrentUser currentUser,
    IServiceProvider services,
    IOptions<RaaviAuthenticationOptions> options,
    ILogger<ExternalLoginFlow> logger)
{
    private string BindingCookie => options.Value.Session.AllowInsecureHttp ? "raavi-login" : "__Host-raavi-login";

    private bool SecureCookies => !options.Value.Session.AllowInsecureHttp;

    /// <summary>Starts a sign-in (or a link for the signed-in user). Returns the provider URL the browser must navigate to.</summary>
    public async Task<Result<Uri>> BeginAsync(HttpContext http, string providerId, ExternalLoginMode mode, string? returnPath, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var tenantId = tenant.Tenant.TenantId;
        var enabled = await policies.GetEnabledProvidersAsync(tenantId, ct).ConfigureAwait(false);
        if (!enabled.Any(p => p.ProviderId == providerId) || !registry.TryGet(providerId, out var provider) || provider is null || !provider.SupportedFlows.HasFlag(AuthenticationFlow.Redirect))
        {
            return Result<Uri>.Failure(new Error("auth.provider_unavailable", "This sign-in method is not available.", ErrorKind.NotFound));
        }

        Guid? linkUser = null;
        if (mode == ExternalLoginMode.Link)
        {
            if (!currentUser.IsAuthenticated)
            {
                return Result<Uri>.Failure(new Error("auth.required", "Sign in first.", ErrorKind.Unauthorized));
            }

            linkUser = currentUser.UserId;
        }

        var tenantBase = $"{http.Request.Scheme}://{http.Request.Host}";
        var callback = provider.CallbackMode == CallbackMode.Relay
            ? new Uri(urls.SystemBaseUrl, $"/api/v1/auth/relay/{providerId}")
            : new Uri($"{tenantBase}/api/v1/auth/callback/{providerId}");

        var state = OpaqueToken.Create();
        var nonce = OpaqueToken.Create();
        var verifier = Pkce.NewVerifier();
        var browser = OpaqueToken.Create();

        http.Response.Cookies.Append(BindingCookie, browser, new CookieOptions
        {
            HttpOnly = true,
            Secure = SecureCookies,
            SameSite = SameSiteMode.Lax, // must accompany the top-level redirect back from the provider
            Path = "/",
            MaxAge = TimeSpan.FromMinutes(10),
            IsEssential = true,
        });

        await pending.SaveAsync(state, new PendingLogin(providerId, tenantId, tenantBase, callback.ToString(), nonce, verifier,
            SafeReturnPath(returnPath), mode, linkUser, OpaqueToken.Hash(browser)), ct).ConfigureAwait(false);

        var url = await provider.BeginRedirectAsync(new RedirectRequest(callback, state, nonce, Pkce.Challenge(verifier)), ct).ConfigureAwait(false);
        return Result<Uri>.Success(url);
    }

    /// <summary>Tenant-host callback (PerHost providers). Always returns a local redirect path (success or /login?error=…).</summary>
    public async Task<string> CompleteOnTenantHostAsync(HttpContext http, string providerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var login = await pending.TakeAsync(http.Request.Query["state"], ct).ConfigureAwait(false);
        var check = Validate(http, login, providerId);
        if (check is not null)
        {
            return ErrorPath(login, check);
        }

        if (!registry.TryGet(providerId, out var provider) || provider is null || provider.CallbackMode != CallbackMode.PerHost)
        {
            return ErrorPath(login, "auth.provider_unavailable");
        }

        var result = await Exchange(provider, login!, http.Request.Query, ct).ConfigureAwait(false);
        return result.Succeeded ? await FinishAsync(http, login!, result.Identity!, ct).ConfigureAwait(false) : ErrorPath(login, result.FailureCode ?? "auth.provider_error");
    }

    /// <summary>System-host relay callback: exchange the code, then hand the verified identity to the tenant host (one-time code).</summary>
    public async Task<Uri> RelayFromSystemHostAsync(HttpContext http, string providerId, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var login = await pending.TakeAsync(http.Request.Query["state"], ct).ConfigureAwait(false);
        if (login is null || login.ProviderId != providerId || !registry.TryGet(providerId, out var provider) || provider is null || provider.CallbackMode != CallbackMode.Relay)
        {
            LogRejected(logger, "relay_state_invalid", providerId);
            return new Uri(urls.SystemBaseUrl, "/system?error=auth.state_invalid");
        }

        var result = await Exchange(provider, login, http.Request.Query, ct).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return new Uri(login.TenantBaseUrl + ErrorPath(login, result.FailureCode ?? "auth.provider_error"));
        }

        var code = await pending.SaveHandoffAsync(new RelayHandoff(login, ExternalIdentityDto.From(result.Identity!)), ct).ConfigureAwait(false);
        return new Uri($"{login.TenantBaseUrl}/api/v1/auth/relay/complete?code={Uri.EscapeDataString(code)}");
    }

    /// <summary>Tenant-host end of the relay: same tenant and same browser as the one that started the login.</summary>
    public async Task<string> CompleteRelayOnTenantHostAsync(HttpContext http, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        var handoff = await pending.TakeHandoffAsync(http.Request.Query["code"], ct).ConfigureAwait(false);
        var check = Validate(http, handoff?.Login, handoff?.Login.ProviderId);
        return check is not null
            ? ErrorPath(handoff?.Login, check)
            : await FinishAsync(http, handoff!.Login, handoff.Identity.ToIdentity(), ct).ConfigureAwait(false);
    }

    private string? Validate(HttpContext http, PendingLogin? login, string? providerId)
    {
        if (login is null || login.ProviderId != providerId)
        {
            return "auth.state_invalid";
        }

        if (!tenant.IsResolved || login.TenantId != tenant.Tenant.TenantId)
        {
            LogRejected(logger, "tenant_mismatch", login.ProviderId);
            return "auth.state_invalid";
        }

        var cookie = http.Request.Cookies[BindingCookie];
        if (string.IsNullOrEmpty(cookie) || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(OpaqueToken.Hash(cookie)), Encoding.ASCII.GetBytes(login.BrowserBindingHash)))
        {
            LogRejected(logger, "browser_binding", login.ProviderId);
            return "auth.state_invalid";
        }

        http.Response.Cookies.Delete(BindingCookie, new CookieOptions { Secure = SecureCookies, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
        return null;
    }

    private static async Task<ExternalAuthenticationResult> Exchange(IAuthenticationProvider provider, PendingLogin login, IQueryCollection query, CancellationToken ct)
    {
        if (query.ContainsKey("error"))
        {
            return ExternalAuthenticationResult.Failure("auth.provider_denied");
        }

        var parameters = query.ToDictionary(q => q.Key, q => q.Value.ToString(), StringComparer.Ordinal);
        return await provider.CompleteRedirectAsync(new RedirectCallback(parameters, new Uri(login.CallbackUri), login.Nonce, login.CodeVerifier), ct).ConfigureAwait(false);
    }

    private async Task<string> FinishAsync(HttpContext http, PendingLogin login, ExternalIdentity identity, CancellationToken ct)
    {
        var signIn = services.GetRequiredService<ExternalSignInService>();
        if (login.Mode == ExternalLoginMode.Link)
        {
            // The same signed-in user must still be present on this browser.
            if (!currentUser.IsAuthenticated || currentUser.UserId != login.LinkUserId)
            {
                return ErrorPath(login, "auth.state_invalid");
            }

            var linked = await signIn.LinkAsync(login.LinkUserId!.Value, identity, ct).ConfigureAwait(false);
            return linked.IsSuccess ? "/me?linked=" + Uri.EscapeDataString(identity.ProviderId) : "/me?error=" + linked.Error!.Code;
        }

        var result = await signIn.SignInAsync(http, identity, ct).ConfigureAwait(false);
        return result.IsSuccess ? login.ReturnPath : ErrorPath(login, result.Error!.Code);
    }

    private static string ErrorPath(PendingLogin? login, string code) =>
        (login?.Mode == ExternalLoginMode.Link ? "/me?error=" : "/login?error=") + Uri.EscapeDataString(code);

    /// <summary>Only same-site relative paths (open-redirect protection).</summary>
    public static string SafeReturnPath(string? path) =>
        !string.IsNullOrEmpty(path) && path.Length <= 512 && path.StartsWith('/') && !path.StartsWith("//", StringComparison.Ordinal) && !path.Contains('\\', StringComparison.Ordinal) && !path.Contains("://", StringComparison.Ordinal)
            ? path
            : "/";

    [LoggerMessage(EventId = 4101, Level = LogLevel.Warning, Message = "SECURITY external login rejected ({Reason}) for provider {ProviderId}")]
    private static partial void LogRejected(ILogger logger, string reason, string providerId);
}
