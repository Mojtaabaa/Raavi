using Microsoft.Extensions.Options;

namespace Raavi.Tenancy.Resolution;

/// <summary>Global hosting configuration (<c>Raavi:Hosting</c> in sandbox/config/appsettings.json).</summary>
public sealed class HostingOptions
{
    public const string Section = "Raavi:Hosting";

    public string SystemHost { get; set; } = string.Empty;

    /// <summary>How workspaces are addressed: <c>Subdomain</c> (slug.base-domain, default), <c>Path</c> (system-host/t/slug) or <c>Both</c>. Verified custom domains always work.</summary>
    public string Addressing { get; set; } = nameof(TenantAddressing.Subdomain);

    /// <summary>First path segment of path-addressed workspaces (default <c>t</c>).</summary>
    public string PathPrefix { get; set; } = "t";

    /// <summary>Code of the workspace used when a request on the platform host names none (ADR-045). Empty = the root of the system host is the system area.</summary>
    public string DefaultTenantCode { get; set; } = string.Empty;

    public TenantAddressing ParsedAddressing { get; private set; } = TenantAddressing.Subdomain;

    public bool AcceptsSubdomains => ParsedAddressing != TenantAddressing.Path;

    public bool AcceptsPaths => ParsedAddressing != TenantAddressing.Subdomain;

    public string NormalizedPathPrefix { get; private set; } = "t";

    public string NormalizedDefaultTenantCode { get; private set; } = string.Empty;

    /// <summary>First path segments that are never workspace prefixes.</summary>
    private static readonly string[] ReservedPrefixes = ["system", "api", "hubs", "health", "internal", "assets", "openapi"];

    public IList<string> TenantBaseDomains { get; } = [];

    /// <summary>Scheme used when building canonical tenant URLs (discovery redirects).</summary>
    public string PublicScheme { get; set; } = "https";

    /// <summary>Optional public port for canonical URLs (development).</summary>
    public int? PublicPort { get; set; }

    public IList<string> KnownProxies { get; } = [];

    public IList<string> KnownNetworks { get; } = [];

    /// <summary>Headers that clients sometimes use to name a tenant. They never select a tenant; a mismatch is rejected (ADR-004).</summary>
    public IList<string> TenantHintHeaders { get; } = ["X-Tenant-Id", "X-Tenant-Code", "X-Tenant"];

    public string NormalizedSystemHost { get; private set; } = string.Empty;

    public IReadOnlyList<string> NormalizedBaseDomains { get; private set; } = [];

    public void Normalize()
    {
        if (!TenantHost.TryNormalize(SystemHost, out var system))
        {
            throw new OptionsValidationException(Section, typeof(HostingOptions), [$"{Section}:SystemHost '{SystemHost}' is not a valid host name."]);
        }

        var bases = new List<string>();
        foreach (var d in TenantBaseDomains)
        {
            if (!TenantHost.TryNormalize(d, out var n))
            {
                throw new OptionsValidationException(Section, typeof(HostingOptions), [$"{Section}:TenantBaseDomains entry '{d}' is not a valid host name."]);
            }

            bases.Add(n);
        }

        if (!Enum.TryParse(Addressing, ignoreCase: true, out TenantAddressing addressing) || !Enum.IsDefined(addressing) || int.TryParse(Addressing, out _))
        {
            throw new OptionsValidationException(Section, typeof(HostingOptions), [$"{Section}:Addressing '{Addressing}' must be Subdomain, Path or Both."]);
        }

        var prefix = (PathPrefix ?? string.Empty).Trim().ToLowerInvariant();
        if (prefix.Length is 0 or > 16 || !prefix.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') || ReservedPrefixes.Contains(prefix))
        {
            throw new OptionsValidationException(Section, typeof(HostingOptions), [$"{Section}:PathPrefix '{PathPrefix}' must be 1-16 characters (a-z, 0-9, -) and not a reserved word."]);
        }

        ParsedAddressing = addressing;
        NormalizedPathPrefix = prefix;
        NormalizedDefaultTenantCode = (DefaultTenantCode ?? string.Empty).Trim().ToUpperInvariant();

        if (bases.Count == 0 && addressing != TenantAddressing.Path)
        {
            throw new OptionsValidationException(Section, typeof(HostingOptions), [$"{Section}:TenantBaseDomains must contain at least one domain."]);
        }

        NormalizedSystemHost = system;
        NormalizedBaseDomains = bases;
    }

    /// <summary>True when the host is the system host, a base domain itself, or a name under a base domain.</summary>
    public bool IsPlatformHost(string normalizedHost) =>
        normalizedHost == NormalizedSystemHost
        || NormalizedBaseDomains.Any(b => normalizedHost == b || normalizedHost.EndsWith("." + b, StringComparison.Ordinal));

    /// <summary>Address of a path-addressed workspace: <c>{scheme}://{system host}[:port]/{prefix}/{slug}/</c>.</summary>
    public Uri TenantPathUrl(string slug)
    {
        var builder = new UriBuilder(PublicScheme, NormalizedSystemHost) { Port = PublicPort ?? -1, Path = $"/{NormalizedPathPrefix}/{slug}/" };
        return builder.Uri;
    }

    public Uri TenantUrl(string host)
    {
        var builder = new UriBuilder(PublicScheme, host) { Port = PublicPort ?? -1 };
        return builder.Uri;
    }
}

public enum TenantAddressing
{
    Subdomain = 0,
    Path = 1,
    Both = 2,
}

public sealed class HostingOptionsSetup : IPostConfigureOptions<HostingOptions>
{
    public void PostConfigure(string? name, HostingOptions options) => options.Normalize();
}
