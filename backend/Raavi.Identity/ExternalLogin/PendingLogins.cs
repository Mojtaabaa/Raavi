using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Caching.Distributed;
using Raavi.Identity.Providers;
using Raavi.Identity.Security;

namespace Raavi.Identity.ExternalLogin;

public enum ExternalLoginMode
{
    SignIn = 1,
    Link = 2,
}

/// <summary>
/// Server-side state of a redirect login (AUTHENTICATION_ARCHITECTURE.md §4). Keyed by the SHA-256 of the opaque
/// <c>state</c>; single-use; 10 minutes. Binds tenant, provider, browser (hash of a host-only cookie), PKCE verifier,
/// nonce, return path and — for linking — the signed-in user.
/// </summary>
public sealed record PendingLogin(
    string ProviderId,
    Guid TenantId,
    string TenantBaseUrl,
    string CallbackUri,
    string Nonce,
    string CodeVerifier,
    string ReturnPath,
    ExternalLoginMode Mode,
    Guid? LinkUserId,
    string BrowserBindingHash);

/// <summary>Verified identity carried from the system-host relay callback to the tenant host (one-time code, 60 s).</summary>
public sealed record RelayHandoff(PendingLogin Login, ExternalIdentityDto Identity);

/// <summary>Serializable copy of <see cref="ExternalIdentity"/>.</summary>
public sealed record ExternalIdentityDto(string ProviderId, string SubjectId, string? Username, string? Email, bool EmailVerified, string? DisplayName, IReadOnlyList<string> Groups)
{
    public static ExternalIdentityDto From(ExternalIdentity i) => new(i.ProviderId, i.SubjectId, i.Username, i.Email, i.EmailVerified, i.DisplayName, i.Groups.ToList());

    public ExternalIdentity ToIdentity() => new(ProviderId, SubjectId, Username, Email, EmailVerified, DisplayName, Groups);
}

public sealed class PendingLoginStore(IDistributedCache cache)
{
    private static readonly TimeSpan LoginLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HandoffLifetime = TimeSpan.FromSeconds(60);

    public async Task SaveAsync(string state, PendingLogin login, CancellationToken ct) =>
        await cache.SetStringAsync(Key("login", state), JsonSerializer.Serialize(login), new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = LoginLifetime }, ct).ConfigureAwait(false);

    /// <summary>Returns and deletes the pending login (single use).</summary>
    public Task<PendingLogin?> TakeAsync(string? state, CancellationToken ct) => Take<PendingLogin>("login", state, ct);

    public async Task<string> SaveHandoffAsync(RelayHandoff handoff, CancellationToken ct)
    {
        var code = OpaqueToken.Create();
        await cache.SetStringAsync(Key("handoff", code), JsonSerializer.Serialize(handoff), new DistributedCacheEntryOptions { AbsoluteExpirationRelativeToNow = HandoffLifetime }, ct).ConfigureAwait(false);
        return code;
    }

    public Task<RelayHandoff?> TakeHandoffAsync(string? code, CancellationToken ct) => Take<RelayHandoff>("handoff", code, ct);

    private async Task<T?> Take<T>(string kind, string? token, CancellationToken ct)
        where T : class
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return null;
        }

        var key = Key(kind, token);
        var json = await cache.GetStringAsync(key, ct).ConfigureAwait(false);
        if (json is null)
        {
            return null;
        }

        await cache.RemoveAsync(key, ct).ConfigureAwait(false);
        return JsonSerializer.Deserialize<T>(json);
    }

    private static string Key(string kind, string token) => $"sys:{kind}:{OpaqueToken.Hash(token)}";
}

/// <summary>PKCE (RFC 7636, S256).</summary>
public static class Pkce
{
    public static string NewVerifier() => OpaqueToken.Create();

    public static string Challenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
