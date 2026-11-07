using System.Globalization;
using System.Net;

namespace Raavi.Tenancy.Resolution;

/// <summary>
/// Pure host-name normalization and parsing used by tenant resolution. Implements the rules in
/// docs/architecture/MULTI_TENANT_ARCHITECTURE.md §3: lower-case, port stripped, single trailing dot removed,
/// IDN converted to its ASCII (punycode) form, strict DNS label syntax, IP literals rejected.
/// </summary>
public static class TenantHost
{
    private const int MaxHostLength = 253;
    private const int MaxLabelLength = 63;
    private static readonly IdnMapping Idn = new() { AllowUnassigned = false, UseStd3AsciiRules = true };

    /// <summary>Normalizes a raw Host header value. Returns false for anything that is not a valid DNS host name.</summary>
    public static bool TryNormalize(string? rawHost, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(rawHost))
        {
            return false;
        }

        var host = rawHost.Trim();

        // IPv6 literal ("[::1]:443") or anything with brackets is never a tenant host.
        if (host.Contains('[', StringComparison.Ordinal) || host.Contains(']', StringComparison.Ordinal))
        {
            return false;
        }

        var colon = host.IndexOf(':', StringComparison.Ordinal);
        if (colon >= 0)
        {
            var port = host[(colon + 1)..];
            if (port.Length == 0 || !int.TryParse(port, NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535)
            {
                return false;
            }

            host = host[..colon];
        }

        if (host.EndsWith('.'))
        {
            host = host[..^1];
        }

        if (host.Length == 0 || host.Contains("..", StringComparison.Ordinal))
        {
            return false;
        }

        string ascii;
        try
        {
            ascii = Idn.GetAscii(host);
        }
        catch (ArgumentException)
        {
            return false;
        }

        ascii = ascii.ToLowerInvariant();
        if (ascii.Length > MaxHostLength || IPAddress.TryParse(ascii, out _))
        {
            return false;
        }

        foreach (var label in ascii.Split('.'))
        {
            if (!IsValidLabel(label))
            {
                return false;
            }
        }

        // A host made only of digits and dots (e.g. "10.0.0") is treated as an IP-like literal and rejected.
        if (ascii.All(c => char.IsAsciiDigit(c) || c == '.'))
        {
            return false;
        }

        normalized = ascii;
        return true;
    }

    /// <summary>
    /// Extracts the tenant slug when <paramref name="normalizedHost"/> is exactly one label below <paramref name="baseDomain"/>.
    /// "acme.raavi.ir" under "raavi.ir" yields "acme"; "a.b.raavi.ir" and "raavi.ir" itself yield false.
    /// Both arguments must already be normalized.
    /// </summary>
    public static bool TryGetSubdomainSlug(string normalizedHost, string normalizedBaseDomain, out string slug)
    {
        slug = string.Empty;
        ArgumentNullException.ThrowIfNull(normalizedHost);
        ArgumentNullException.ThrowIfNull(normalizedBaseDomain);

        var suffix = "." + normalizedBaseDomain;
        if (normalizedHost.Length <= suffix.Length || !normalizedHost.EndsWith(suffix, StringComparison.Ordinal))
        {
            return false;
        }

        var candidate = normalizedHost[..^suffix.Length];
        if (candidate.Contains('.', StringComparison.Ordinal) || !IsValidLabel(candidate))
        {
            return false;
        }

        slug = candidate;
        return true;
    }

    /// <summary>Validates a tenant slug (a single DNS label, ASCII lower-case, no punycode prefix).</summary>
    public static bool IsValidSlug(string? slug) =>
        slug is not null
        && slug.Length >= 2
        && IsValidLabel(slug)
        && !slug.StartsWith("xn--", StringComparison.Ordinal)
        && slug.All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-');

    private static bool IsValidLabel(string label)
    {
        if (label.Length is 0 or > MaxLabelLength || label[0] == '-' || label[^1] == '-')
        {
            return false;
        }

        foreach (var c in label)
        {
            if (!(char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-'))
            {
                return false;
            }
        }

        return true;
    }
}
