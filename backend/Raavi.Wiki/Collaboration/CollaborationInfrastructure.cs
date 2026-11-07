using System.Globalization;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Raavi.Application.Abstractions.Collaboration;

namespace Raavi.Wiki.Collaboration;

/// <summary>Global collaboration settings (<c>sandbox/config/collaboration.json</c>; secrets from environment variables).</summary>
public sealed class CollaborationOptions
{
    public const string Section = "Raavi:Collaboration";

    public bool Enabled { get; set; }

    /// <summary>WebSocket URL browsers connect to (e.g. <c>wss://collab.example.com</c>).</summary>
    public string? PublicUrl { get; set; }

    /// <summary>HTTP base URL the API uses to reach the collaboration server (internal network).</summary>
    public string? ServerUrl { get; set; }

    /// <summary>Shared HMAC key for service-to-service calls in both directions. Secret: <c>RAAVI_Raavi__Collaboration__ServiceKey</c>.</summary>
    public string? ServiceKey { get; set; }

    /// <summary>PEM file with the ES256 (P-256) private key that signs tickets. Development generates an ephemeral key when unset.</summary>
    public string? SigningKeyPath { get; set; }

    public int TicketLifetimeSeconds { get; set; } = 60;

    public static CollaborationOptions From(IConfiguration configuration) =>
        configuration.GetSection(Section).Get<CollaborationOptions>() ?? new CollaborationOptions();
}

/// <summary>
/// HMAC-SHA256 request signatures for internal calls (API ⇄ collaboration server):
/// <c>X-Raavi-Signature: t=&lt;unix seconds&gt;,s=&lt;hex&gt;</c> over <c>t \n METHOD \n path?query \n hex(sha256(body))</c>.
/// Requests older than 60 s are rejected (replay window).
/// </summary>
public static class ServiceSignature
{
    public const string Header = "X-Raavi-Signature";
    public static readonly TimeSpan Window = TimeSpan.FromSeconds(60);

    public static string Create(string key, string method, string pathAndQuery, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        var t = now.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
        return $"t={t},s={Compute(key, t, method, pathAndQuery, body)}";
    }

    public static bool Verify(string? key, string? header, string method, string pathAndQuery, ReadOnlySpan<byte> body, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(key) || string.IsNullOrEmpty(header))
        {
            return false;
        }

        string? t = null, s = null;
        foreach (var part in header.Split(','))
        {
            var kv = part.Split('=', 2);
            if (kv.Length != 2)
            {
                continue;
            }

            if (kv[0] == "t")
            {
                t = kv[1];
            }
            else if (kv[0] == "s")
            {
                s = kv[1];
            }
        }

        if (t is null || s is null || !long.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var unix))
        {
            return false;
        }

        if ((now - DateTimeOffset.FromUnixTimeSeconds(unix)).Duration() > Window)
        {
            return false;
        }

        var expected = Encoding.ASCII.GetBytes(Compute(key, t, method, pathAndQuery, body));
        return CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(s));
    }

    private static string Compute(string key, string t, string method, string pathAndQuery, ReadOnlySpan<byte> body)
    {
        var payload = $"{t}\n{method.ToUpperInvariant()}\n{pathAndQuery}\n{Convert.ToHexStringLower(SHA256.HashData(body))}";
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.UTF8.GetBytes(payload)));
    }
}

/// <summary>Claims of a collaboration ticket (COLLABORATION_ARCHITECTURE.md §3).</summary>
public sealed record TicketClaims(Guid UserId, Guid TenantId, string DocumentName, string Permission, Guid SessionId, string DisplayName);

/// <summary>
/// Signs short-lived, single-use collaboration tickets (JWS ES256, <c>aud=raavi-collaboration</c>). The public key is
/// published to the collaboration server through the signed internal JWKS endpoint, so no key file is shared.
/// </summary>
public sealed partial class TicketSigner : IDisposable
{
    public const string Audience = "raavi-collaboration";
    public const string Issuer = "raavi-api";
    private readonly ECDsa? _key;
    private readonly ECDsaSecurityKey? _securityKey;

    public TicketSigner(IConfiguration configuration, IHostEnvironment environment, ILogger<TicketSigner> logger)
    {
        ArgumentNullException.ThrowIfNull(environment);
        var options = CollaborationOptions.From(configuration);
        if (!options.Enabled)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(options.SigningKeyPath))
        {
            _key = ECDsa.Create();
            _key.ImportFromPem(File.ReadAllText(options.SigningKeyPath));
        }
        else if (environment.IsDevelopment())
        {
            _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            LogEphemeralKey(logger);
        }
        else
        {
            LogNoKey(logger);
            return;
        }

        _securityKey = new ECDsaSecurityKey(_key) { KeyId = Convert.ToHexStringLower(SHA256.HashData(_key.ExportSubjectPublicKeyInfo()))[..16] };
    }

    public bool IsAvailable => _securityKey is not null;

    public string Sign(TicketClaims claims, TimeSpan lifetime, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(claims);
        var key = _securityKey ?? throw new InvalidOperationException("Collaboration ticket signing is not configured.");
        var handler = new JsonWebTokenHandler { SetDefaultTimesOnTokenCreation = false };
        return handler.CreateToken(new SecurityTokenDescriptor
        {
            Issuer = Issuer,
            Audience = Audience,
            IssuedAt = now.UtcDateTime,
            NotBefore = now.UtcDateTime.AddSeconds(-5),
            Expires = now.UtcDateTime.Add(lifetime),
            SigningCredentials = new SigningCredentials(key, SecurityAlgorithms.EcdsaSha256),
            Subject = new ClaimsIdentity(
            [
                new Claim("sub", claims.UserId.ToString("D")),
                new Claim("tid", claims.TenantId.ToString("D")),
                new Claim("doc", claims.DocumentName),
                new Claim("perm", claims.Permission),
                new Claim("sid", claims.SessionId.ToString("D")),
                new Claim("name", claims.DisplayName),
                new Claim("jti", Guid.NewGuid().ToString("N")),
            ]),
        });
    }

    /// <summary>Public key as a JSON Web Key Set (no private parameters).</summary>
    public object Jwks()
    {
        var key = _securityKey ?? throw new InvalidOperationException("Collaboration ticket signing is not configured.");
        var p = _key!.ExportParameters(false);
        return new
        {
            keys = new[]
            {
                new { kty = "EC", crv = "P-256", alg = "ES256", use = "sig", kid = key.KeyId, x = Base64UrlEncoder.Encode(p.Q.X), y = Base64UrlEncoder.Encode(p.Q.Y) },
            },
        };
    }

    public void Dispose() => _key?.Dispose();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collaboration: no signing key configured; using an ephemeral development key (tickets do not survive restarts).")]
    private static partial void LogEphemeralKey(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Collaboration is enabled but Raavi:Collaboration:SigningKeyPath is not set; real-time editing is disabled.")]
    private static partial void LogNoKey(ILogger logger);
}

/// <summary>Calls from the API to the collaboration server (flush, reset, re-check). Failures are logged, never thrown.</summary>
public sealed partial class CollaborationClient(IHttpClientFactory httpClients, IConfiguration configuration, ILogger<CollaborationClient> logger) : ICollaborationRevocation
{
    public const string HttpClientName = "raavi-collaboration";

    /// <summary>Persists pending edits of the document now (before publishing).</summary>
    public Task<bool> FlushAsync(string documentName, CancellationToken ct) => PostAsync("/internal/flush", new { documentName }, ct);

    /// <summary>Closes connections and unloads the document so clients reload it from the (reset) stored state.</summary>
    public Task<bool> ResetAsync(string documentName, CancellationToken ct) => PostAsync("/internal/reset", new { documentName }, ct);

    public async Task RecheckAsync(Guid tenantId, Guid? userId, CancellationToken cancellationToken = default) =>
        await PostAsync("/internal/recheck", new { tenantId, userId }, cancellationToken).ConfigureAwait(false);

    private async Task<bool> PostAsync(string path, object body, CancellationToken ct)
    {
        var options = CollaborationOptions.From(configuration);
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.ServerUrl) || string.IsNullOrEmpty(options.ServiceKey))
        {
            return false;
        }

        try
        {
            var bytes = JsonContent.Create(body).ReadAsByteArrayAsync(ct);
            var payload = await bytes.ConfigureAwait(false);
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(options.ServerUrl), path)) { Content = new ByteArrayContent(payload) };
            request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json");
            request.Headers.TryAddWithoutValidation(ServiceSignature.Header, ServiceSignature.Create(options.ServiceKey, "POST", path, payload, DateTimeOffset.UtcNow));
            using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                LogCallFailed(logger, path, (int)response.StatusCode);
            }

            return response.IsSuccessStatusCode;
        }
#pragma warning disable CA1031 // the collaboration server being down must not break the API operation
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
#pragma warning restore CA1031
        {
            LogUnreachable(logger, ex, path);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collaboration server call {Path} returned {Status}")]
    private static partial void LogCallFailed(ILogger logger, string path, int status);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Collaboration server unreachable for {Path}")]
    private static partial void LogUnreachable(ILogger logger, Exception ex, string path);
}
