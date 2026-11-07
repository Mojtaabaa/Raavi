using Microsoft.Extensions.Options;

namespace Raavi.Tenancy.Resolution;

/// <summary>Global hosting configuration (<c>Raavi:Hosting</c> in sandbox/config/appsettings.json).</summary>
public sealed class HostingOptions
{
    public const string Section = "Raavi:Hosting";

    public string SystemHost { get; set; } = string.Empty;

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

        if (bases.Count == 0)
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

    public Uri TenantUrl(string host)
    {
        var builder = new UriBuilder(PublicScheme, host) { Port = PublicPort ?? -1 };
        return builder.Uri;
    }
}

public sealed class HostingOptionsSetup : IPostConfigureOptions<HostingOptions>
{
    public void PostConfigure(string? name, HostingOptions options) => options.Normalize();
}
