namespace Raavi.Identity.Providers;

/// <summary>
/// One configured identity source (local, LDAP, Active Directory, OIDC, OAuth2, Jira Data Center, plugin).
/// Providers answer only "who is this user" — they never decide tenant membership or permissions.
/// Contract defined in docs/architecture/AUTHENTICATION_ARCHITECTURE.md §3.
/// </summary>
public interface IAuthenticationProvider
{
    /// <summary>Stable id from the global definition in sandbox/config/identity-providers.json, e.g. "jira-eng".</summary>
    string ProviderId { get; }

    AuthenticationProviderType Type { get; }

    AuthenticationFlow SupportedFlows { get; }

    /// <summary>Where the provider redirects back to: each tenant host, or one fixed callback on the system host (relay).</summary>
    CallbackMode CallbackMode { get; }

    /// <summary>Validates the global definition without contacting the remote system.</summary>
    IReadOnlyList<string> ValidateConfiguration();

    /// <summary>Contacts the remote system with the configured service credentials. Used by the admin "Test connection" action.</summary>
    ValueTask<ProviderHealth> TestConnectionAsync(CancellationToken cancellationToken);

    /// <summary>Builds the provider authorization URL. State, nonce and PKCE challenge are generated and stored by the flow, never by the provider.</summary>
    ValueTask<Uri> BeginRedirectAsync(RedirectRequest request, CancellationToken cancellationToken);

    /// <summary>Exchanges the authorization code (back channel) and returns the verified external identity.</summary>
    ValueTask<ExternalAuthenticationResult> CompleteRedirectAsync(RedirectCallback callback, CancellationToken cancellationToken);

    /// <summary>Credential-based flow (local, LDAP/AD bind). Providers that do not support it return <see cref="ExternalAuthenticationResult.NotSupported"/>.</summary>
    ValueTask<ExternalAuthenticationResult> AuthenticateWithCredentialsAsync(CredentialLogin login, CancellationToken cancellationToken);
}

public enum AuthenticationProviderType
{
    Local = 1,
    Ldap = 2,
    ActiveDirectory = 3,
    OpenIdConnect = 4,
    OAuth2 = 5,
    JiraDataCenter = 6,
    Plugin = 99,
}

[Flags]
public enum AuthenticationFlow
{
    None = 0,
    Credentials = 1,
    Redirect = 2,
}

public sealed record ProviderHealth(bool IsHealthy, string StatusCode, string? Detail);

public enum CallbackMode
{
    /// <summary>Callback on the tenant host that started the login (IdP must accept every tenant host as redirect URI).</summary>
    PerHost = 1,

    /// <summary>One fixed callback on the system host, followed by a one-time hand-off to the tenant host (AUTHENTICATION_ARCHITECTURE.md §4).</summary>
    Relay = 2,
}

/// <param name="CallbackUri">Absolute redirect URI registered at the provider.</param>
/// <param name="State">Opaque single-use value bound server-side to tenant, provider and browser.</param>
/// <param name="Nonce">OIDC nonce (ignored by plain OAuth 2.0 providers).</param>
/// <param name="CodeChallenge">PKCE S256 challenge.</param>
public sealed record RedirectRequest(Uri CallbackUri, string State, string Nonce, string CodeChallenge);

/// <param name="Parameters">Query parameters of the callback (code, error, …).</param>
public sealed record RedirectCallback(IReadOnlyDictionary<string, string> Parameters, Uri CallbackUri, string Nonce, string CodeVerifier);

public sealed record CredentialLogin(string Username, string Password, Guid? TenantId);

/// <summary>Verified identity asserted by a provider. <see cref="SubjectId"/> is the stable key; e-mail is informational unless the provider marks it verified.</summary>
public sealed record ExternalIdentity(
    string ProviderId,
    string SubjectId,
    string? Username,
    string? Email,
    bool EmailVerified,
    string? DisplayName,
    IReadOnlyCollection<string> Groups);

public sealed record ExternalAuthenticationResult(bool Succeeded, ExternalIdentity? Identity, string? FailureCode)
{
    public static ExternalAuthenticationResult NotSupported { get; } = new(false, null, "auth.flow_not_supported");

    public static ExternalAuthenticationResult Success(ExternalIdentity identity) => new(true, identity, null);

    public static ExternalAuthenticationResult Failure(string code) => new(false, null, code);
}

/// <summary>Holds every provider built from the global definitions. Tenant enablement is applied by <see cref="ITenantProviderPolicyService"/>.</summary>
public interface IAuthenticationProviderRegistry
{
    IReadOnlyCollection<IAuthenticationProvider> All { get; }

    bool TryGet(string providerId, out IAuthenticationProvider? provider);
}

/// <summary>Reads tenant-specific provider enablement and policy from SQL Server (never from sandbox files).</summary>
public interface ITenantProviderPolicyService
{
    ValueTask<IReadOnlyList<TenantProviderPolicy>> GetEnabledProvidersAsync(Guid tenantId, CancellationToken cancellationToken);
}

public enum ProvisioningMode
{
    ExistingUsersOnly = 1,
    AutomaticCreation = 2,
    AdministratorApproval = 3,
}

public sealed record TenantProviderPolicy(
    string ProviderId,
    string DisplayName,
    bool IsDefault,
    ProvisioningMode Provisioning,
    IReadOnlyCollection<string> AllowedEmailDomains,
    IReadOnlyCollection<string> AllowedExternalGroups);

/// <summary>Maps (ProviderId, SubjectId) to a Raavi user. Never links by unverified e-mail.</summary>
public interface IExternalIdentityResolver
{
    ValueTask<Guid?> FindUserAsync(string providerId, string subjectId, CancellationToken cancellationToken);
}

/// <summary>Applies the tenant's provisioning policy to a verified external identity.</summary>
public interface IUserProvisioningService
{
    ValueTask<ProvisioningDecision> ProvisionAsync(ExternalIdentity identity, Guid tenantId, CancellationToken cancellationToken);
}

public sealed record ProvisioningDecision(Guid? UserId, bool PendingApproval, string? DenialCode);

/// <summary>Answers "which tenants may this user access" — independent of authentication.</summary>
public interface ITenantMembershipService
{
    ValueTask<bool> IsActiveMemberAsync(Guid userId, Guid tenantId, CancellationToken cancellationToken);
}
