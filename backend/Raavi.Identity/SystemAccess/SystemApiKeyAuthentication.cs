using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Infrastructure.Http;

namespace Raavi.Identity.SystemAccess;

/// <summary>
/// Operator API keys for system administration (Phase 1 bootstrap; Phase 2 adds MFA-protected administrator sign-in).
/// Only SHA-256 hashes are configured (<c>Raavi:Security:SystemApiKeys</c>); keys are accepted only on the system host,
/// compared in constant time, and never logged.
/// </summary>
public sealed class SystemApiKeyOptions
{
    public const string Section = "Raavi:Security";

    public IList<SystemApiKeyDefinition> SystemApiKeys { get; } = [];
}

public sealed class SystemApiKeyDefinition
{
    public string Id { get; set; } = string.Empty;

    /// <summary>Lower-case hex SHA-256 of the key.</summary>
    public string Sha256 { get; set; } = string.Empty;
}

public sealed class SystemApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    IOptionsMonitor<SystemApiKeyOptions> keys)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "SystemApiKey";
    private const string Prefix = "Bearer ";

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Context.GetHostKind() != RaaviHostKind.System)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        string header = Request.Headers.Authorization.ToString();
        if (!header.StartsWith(Prefix, StringComparison.Ordinal) || header.Length <= Prefix.Length)
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var presented = SHA256.HashData(Encoding.UTF8.GetBytes(header[Prefix.Length..].Trim()));
        foreach (var key in keys.CurrentValue.SystemApiKeys)
        {
            byte[] expected;
            try
            {
                expected = Convert.FromHexString(key.Sha256);
            }
            catch (FormatException)
            {
                continue;
            }

            if (expected.Length == presented.Length && CryptographicOperations.FixedTimeEquals(expected, presented))
            {
                var identity = new ClaimsIdentity(
                    [
                        new Claim(ClaimTypes.NameIdentifier, $"apikey:{key.Id}"),
                        new Claim(RaaviPolicies.ActorTypeClaim, "system_api_key"),
                        new Claim(ClaimTypes.Role, RaaviPolicies.SystemAdministratorRole),
                    ],
                    SchemeName);
                return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName)));
            }
        }

        return Task.FromResult(AuthenticateResult.Fail("invalid_api_key"));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties) => Raavi.Identity.Security.AuthProblems.ChallengeAsync(Context);

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) => Raavi.Identity.Security.AuthProblems.ForbidAsync(Context);
}
