using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Raavi.Identity.Persistence;

namespace Raavi.Identity.Providers;

/// <summary>One entry of <c>Raavi:IdentityProviders</c> (sandbox/config/identity-providers.json).</summary>
public sealed class ProviderDefinition
{
    public string Id { get; set; } = string.Empty;

    public AuthenticationProviderType Type { get; set; }

    public bool Enabled { get; set; }

    public LocalizedName DisplayName { get; set; } = new();

    /// <summary>The whole definition section, for provider-specific settings.</summary>
    public IConfigurationSection? Settings { get; set; }

    public sealed class LocalizedName
    {
        public string Fa { get; set; } = string.Empty;

        public string En { get; set; } = string.Empty;
    }
}

/// <summary>
/// Builds providers from the global definitions. Fails fast when an enabled definition is invalid or its type has no
/// implementation in this build (OIDC, LDAP/AD and Jira arrive in Phase 2b) — a misconfigured identity source must
/// never be silently ignored.
/// </summary>
public sealed class AuthenticationProviderRegistry : IAuthenticationProviderRegistry
{
    private readonly Dictionary<string, IAuthenticationProvider> _providers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ProviderDefinition> _definitions = new(StringComparer.Ordinal);

    public AuthenticationProviderRegistry(IConfiguration configuration, IEnumerable<IAuthenticationProviderFactory> factories)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var byType = factories.ToDictionary(f => f.Type);
        var errors = new List<string>();

        foreach (var section in configuration.GetSection("Raavi:IdentityProviders").GetChildren())
        {
            var def = section.Get<ProviderDefinition>() ?? new ProviderDefinition();
            def.Settings = section;
            if (!def.Enabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(def.Id) || def.Id.Length > 64 || !def.Id.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            {
                errors.Add($"Identity provider '{def.Id}': id must be 1–64 lower-case letters, digits or hyphens.");
                continue;
            }

            if (_definitions.ContainsKey(def.Id))
            {
                errors.Add($"Identity provider '{def.Id}' is defined twice.");
                continue;
            }

            if (!byType.TryGetValue(def.Type, out var factory))
            {
                errors.Add($"Identity provider '{def.Id}' has type {def.Type}, which is not available in this build.");
                continue;
            }

            var provider = factory.Create(def);
            errors.AddRange(provider.ValidateConfiguration().Select(e => $"Identity provider '{def.Id}': {e}"));
            _definitions[def.Id] = def;
            _providers[def.Id] = provider;
        }

        if (errors.Count > 0)
        {
            throw new InvalidOperationException("Invalid identity provider configuration: " + string.Join(" ", errors));
        }
    }

    public IReadOnlyCollection<IAuthenticationProvider> All => _providers.Values;

    public bool TryGet(string providerId, out IAuthenticationProvider? provider) => _providers.TryGetValue(providerId, out provider);

    public ProviderDefinition? Definition(string providerId) => _definitions.GetValueOrDefault(providerId);
}

public interface IAuthenticationProviderFactory
{
    AuthenticationProviderType Type { get; }

    IAuthenticationProvider Create(ProviderDefinition definition);
}

/// <summary>Tenant enablement/policy from SQL Server. Without any rows the tenant uses the local provider only (safe default).</summary>
public sealed class TenantProviderPolicyService(IdentityDbContext db, AuthenticationProviderRegistry registry) : ITenantProviderPolicyService
{
    public const string LocalProviderId = "local";

    public async ValueTask<IReadOnlyList<TenantProviderPolicy>> GetEnabledProvidersAsync(Guid tenantId, CancellationToken cancellationToken)
    {
        if (db.CurrentTenantId != tenantId)
        {
            throw new InvalidOperationException("Provider policies can only be read inside the tenant's own scope.");
        }

        var rows = await db.ProviderPolicies.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<TenantProviderPolicy>();
        if (rows.Count == 0)
        {
            if (registry.Definition(LocalProviderId) is { } local)
            {
                result.Add(new TenantProviderPolicy(local.Id, local.DisplayName.En, true, ProvisioningMode.ExistingUsersOnly, [], []));
            }

            return result;
        }

        foreach (var row in rows.Where(r => r.Enabled).OrderBy(r => r.DisplayOrder))
        {
            // A tenant can only use providers that exist and are enabled globally.
            if (registry.Definition(row.ProviderId) is { } def)
            {
                result.Add(new TenantProviderPolicy(
                    def.Id, def.DisplayName.En, row.IsDefault, row.Provisioning,
                    Split(row.AllowedEmailDomains), Split(row.AllowedExternalGroups)));
            }
        }

        return result;
    }

    private static string[] Split(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
