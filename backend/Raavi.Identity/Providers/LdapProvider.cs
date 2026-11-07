using System.DirectoryServices.Protocols;
using System.Net;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Raavi.Identity.Providers;

public sealed class LdapSettings
{
    /// <summary><c>ldaps://dc01.company.local:636</c>, or <c>ldap://host:389</c> together with <see cref="StartTls"/>.</summary>
    public Uri? Server { get; set; }

    public bool StartTls { get; set; }

    public string BaseDn { get; set; } = string.Empty;

    public string BindDn { get; set; } = string.Empty;

    public string BindPassword { get; set; } = string.Empty;

    /// <summary>Search filter with one <c>{0}</c> placeholder for the RFC 4515-escaped username.</summary>
    public string UserFilter { get; set; } = "(&(objectClass=user)(sAMAccountName={0}))";

    /// <summary><c>objectGUID</c> (AD, binary) or <c>entryUUID</c> (OpenLDAP).</summary>
    public string SubjectAttribute { get; set; } = "objectGUID";

    public string UsernameAttribute { get; set; } = "sAMAccountName";

    public string EmailAttribute { get; set; } = "mail";

    public string DisplayNameAttribute { get; set; } = "displayName";

    public string GroupAttribute { get; set; } = "memberOf";

    /// <summary>Directory e-mail is administrator-managed; trust it as verified only when explicitly configured.</summary>
    public bool TrustEmail { get; set; }

    /// <summary>Development only: allow unencrypted LDAP.</summary>
    public bool AllowInsecure { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>Directory operations needed for sign-in. Abstracted so provider logic is testable without a directory server.</summary>
public interface ILdapDirectory
{
    /// <summary>Service-account bind + subtree search. Returns null when not exactly one entry matches.</summary>
    Task<LdapUser?> FindUserAsync(string filter, CancellationToken ct);

    /// <summary>Simple bind as the user. Callers must never pass an empty password (unauthenticated bind succeeds).</summary>
    Task<bool> VerifyPasswordAsync(string distinguishedName, string password, CancellationToken ct);
}

public sealed record LdapUser(string DistinguishedName, IReadOnlyDictionary<string, IReadOnlyList<byte[]>> Attributes);

public interface ILdapDirectoryFactory
{
    ILdapDirectory Create(LdapSettings settings);
}

/// <summary>
/// LDAP / Active Directory credential sign-in (AUTHENTICATION_ARCHITECTURE.md §1): LDAPS or StartTLS mandatory, search as
/// a service account with an RFC 4515-escaped filter, then bind as the found DN with the user's password.
/// Subject = objectGUID / entryUUID (stable across renames).
/// </summary>
public sealed partial class LdapProvider(string providerId, AuthenticationProviderType type, LdapSettings settings, ILdapDirectoryFactory directories, ILogger logger) : IAuthenticationProvider
{
    public string ProviderId { get; } = providerId;

    public AuthenticationProviderType Type { get; } = type;

    public AuthenticationFlow SupportedFlows => AuthenticationFlow.Credentials;

    public CallbackMode CallbackMode => CallbackMode.PerHost;

    public IReadOnlyList<string> ValidateConfiguration()
    {
        var errors = new List<string>();
        if (settings.Server is not { IsAbsoluteUri: true } || settings.Server.Scheme is not ("ldap" or "ldaps"))
        {
            errors.Add("Server must be an ldaps:// or ldap:// URL.");
        }
        else if (settings.Server.Scheme == "ldap" && !settings.StartTls && !settings.AllowInsecure)
        {
            errors.Add("Unencrypted LDAP is not allowed: use ldaps:// or StartTls=true.");
        }

        if (string.IsNullOrWhiteSpace(settings.BaseDn))
        {
            errors.Add("BaseDn is required.");
        }

        if (!settings.UserFilter.Contains("{0}", StringComparison.Ordinal))
        {
            errors.Add("UserFilter must contain the {0} placeholder.");
        }

        return errors;
    }

    public async ValueTask<ProviderHealth> TestConnectionAsync(CancellationToken cancellationToken)
    {
        try
        {
            // A search for an impossible name exercises connect + TLS + service bind without touching real accounts.
            _ = await directories.Create(settings).FindUserAsync(BuildFilter(settings.UserFilter, "raavi-connection-test-" + Guid.NewGuid().ToString("N")), cancellationToken).ConfigureAwait(false);
            return new ProviderHealth(true, "ok", null);
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException or InvalidOperationException)
        {
            return new ProviderHealth(false, "ldap_unreachable", ex.GetType().Name);
        }
    }

    public ValueTask<Uri> BeginRedirectAsync(RedirectRequest request, CancellationToken cancellationToken) =>
        throw new NotSupportedException("LDAP has no redirect flow.");

    public ValueTask<ExternalAuthenticationResult> CompleteRedirectAsync(RedirectCallback callback, CancellationToken cancellationToken) =>
        ValueTask.FromResult(ExternalAuthenticationResult.NotSupported);

    public async ValueTask<ExternalAuthenticationResult> AuthenticateWithCredentialsAsync(CredentialLogin login, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(login);
        var username = (login.Username ?? string.Empty).Trim();
        // Empty password = anonymous/unauthenticated bind, which many directories accept: always reject.
        if (username.Length is 0 or > 256 || string.IsNullOrEmpty(login.Password) || login.Password.Length > 256)
        {
            return ExternalAuthenticationResult.Failure("auth.bad_password");
        }

        try
        {
            var directory = directories.Create(settings);
            var user = await directory.FindUserAsync(BuildFilter(settings.UserFilter, username), cancellationToken).ConfigureAwait(false);
            if (user is null)
            {
                return ExternalAuthenticationResult.Failure("auth.unknown_user");
            }

            if (!await directory.VerifyPasswordAsync(user.DistinguishedName, login.Password, cancellationToken).ConfigureAwait(false))
            {
                return ExternalAuthenticationResult.Failure("auth.bad_password");
            }

            var subject = Subject(user);
            if (subject is null)
            {
                LogFailure(logger, ProviderId, "missing_subject_attribute");
                return ExternalAuthenticationResult.Failure("auth.provider_error");
            }

            var email = Text(user, settings.EmailAttribute);
            var groups = Values(user, settings.GroupAttribute).Select(CommonName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).Take(500).ToList();
            return ExternalAuthenticationResult.Success(new ExternalIdentity(
                ProviderId, subject, Text(user, settings.UsernameAttribute) ?? username, email, settings.TrustEmail && email is not null, Text(user, settings.DisplayNameAttribute), groups));
        }
        catch (Exception ex) when (ex is LdapException or DirectoryOperationException)
        {
            LogFailure(logger, ProviderId, ex.GetType().Name);
            return ExternalAuthenticationResult.Failure("auth.provider_error");
        }
    }

    /// <summary>RFC 4515 escaping of a value inserted into a search filter (prevents LDAP filter injection).</summary>
    public static string EscapeFilterValue(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var sb = new StringBuilder(value.Length);
        foreach (var b in Encoding.UTF8.GetBytes(value))
        {
            var c = (char)b;
            if (b < 0x20 || b > 0x7E || c is '*' or '(' or ')' or '\\' or '\0')
            {
                sb.Append('\\').Append(b.ToString("x2", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static string BuildFilter(string template, string username) =>
        (template ?? throw new ArgumentNullException(nameof(template))).Replace("{0}", EscapeFilterValue(username), StringComparison.Ordinal);

    /// <summary>"CN=Engineering,OU=Groups,DC=x" → "Engineering" (handles escaped commas).</summary>
    public static string? CommonName(string dn)
    {
        if (string.IsNullOrEmpty(dn) || !dn.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var sb = new StringBuilder();
        for (var i = 3; i < dn.Length; i++)
        {
            if (dn[i] == '\\' && i + 1 < dn.Length)
            {
                sb.Append(dn[++i]);
            }
            else if (dn[i] == ',')
            {
                break;
            }
            else
            {
                sb.Append(dn[i]);
            }
        }

        return sb.Length == 0 ? null : sb.ToString();
    }

    private string? Subject(LdapUser user)
    {
        if (!user.Attributes.TryGetValue(settings.SubjectAttribute, out var values) || values.Count == 0)
        {
            return null;
        }

        var raw = values[0];
        return settings.SubjectAttribute.Equals("objectGUID", StringComparison.OrdinalIgnoreCase) && raw.Length == 16
            ? new Guid(raw).ToString("D")
            : Encoding.UTF8.GetString(raw);
    }

    private static string? Text(LdapUser user, string attribute) =>
        user.Attributes.TryGetValue(attribute, out var v) && v.Count > 0 ? Encoding.UTF8.GetString(v[0]) : null;

    private static IEnumerable<string> Values(LdapUser user, string attribute) =>
        user.Attributes.TryGetValue(attribute, out var v) ? v.Select(b => Encoding.UTF8.GetString(b)) : [];

    [LoggerMessage(Level = LogLevel.Warning, Message = "LDAP provider {ProviderId} failed: {Reason}")]
    private static partial void LogFailure(ILogger logger, string providerId, string reason);
}

/// <summary>Real directory access through System.DirectoryServices.Protocols (works on Windows and on Linux with libldap).</summary>
public sealed class LdapDirectoryFactory : ILdapDirectoryFactory
{
    public ILdapDirectory Create(LdapSettings settings) => new LdapDirectory(settings);

    private sealed class LdapDirectory(LdapSettings settings) : ILdapDirectory
    {
        public Task<LdapUser?> FindUserAsync(string filter, CancellationToken ct) => Task.Run(() =>
        {
            using var connection = Connect();
            connection.Bind(new NetworkCredential(settings.BindDn, settings.BindPassword));
            var attributes = new[] { settings.SubjectAttribute, settings.UsernameAttribute, settings.EmailAttribute, settings.DisplayNameAttribute, settings.GroupAttribute };
            var response = (SearchResponse)connection.SendRequest(new SearchRequest(settings.BaseDn, filter, SearchScope.Subtree, attributes) { SizeLimit = 2 });
            if (response.Entries.Count != 1)
            {
                return null;
            }

            var entry = response.Entries[0];
            var map = new Dictionary<string, IReadOnlyList<byte[]>>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in entry.Attributes.AttributeNames)
            {
                map[name] = entry.Attributes[name].GetValues(typeof(byte[])).Cast<byte[]>().ToList();
            }

            return (LdapUser?)new LdapUser(entry.DistinguishedName, map);
        }, ct);

        public Task<bool> VerifyPasswordAsync(string distinguishedName, string password, CancellationToken ct) => Task.Run(() =>
        {
            if (string.IsNullOrEmpty(password))
            {
                return false;
            }

            try
            {
                using var connection = Connect();
                connection.Bind(new NetworkCredential(distinguishedName, password));
                return true;
            }
            catch (LdapException ex) when (ex.ErrorCode == 49) // invalid credentials
            {
                return false;
            }
        }, ct);

        private LdapConnection Connect()
        {
            var server = settings.Server!;
            var connection = new LdapConnection(new LdapDirectoryIdentifier(server.Host, server.Port > 0 ? server.Port : (server.Scheme == "ldaps" ? 636 : 389)))
            {
                AuthType = AuthType.Basic,
                Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds),
            };
            connection.SessionOptions.ProtocolVersion = 3;
            connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;
            if (server.Scheme == "ldaps")
            {
                connection.SessionOptions.SecureSocketLayer = true;
            }
            else if (settings.StartTls)
            {
                connection.SessionOptions.StartTransportLayerSecurity(null);
            }

            return connection;
        }
    }
}

public sealed class LdapProviderFactory(AuthenticationProviderType type, ILdapDirectoryFactory directories, ILoggerFactory logs) : IAuthenticationProviderFactory
{
    public AuthenticationProviderType Type { get; } = type;

    public IAuthenticationProvider Create(ProviderDefinition definition)
    {
        ArgumentNullException.ThrowIfNull(definition);
        return new LdapProvider(definition.Id, Type, definition.Settings?.Get<LdapSettings>() ?? new LdapSettings(), directories, logs.CreateLogger<LdapProvider>());
    }
}
