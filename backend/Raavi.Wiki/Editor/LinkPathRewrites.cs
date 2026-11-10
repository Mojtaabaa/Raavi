using Microsoft.Extensions.Configuration;

namespace Raavi.Wiki.Editor;

/// <summary>
/// Rewrites configured path-prefix hrefs (e.g. <c>vianna/UI/...</c>) to absolute http(s) bases from
/// <c>Raavi:Links:PathRewrites</c>. Non-secret; used by href allow-list, HTML render and the SPA.
/// </summary>
public static class LinkPathRewrites
{
    private static volatile IReadOnlyList<Rule> _rules = [];

    public readonly record struct Rule(string Prefix, string BaseUrl);

    public static void Configure(IConfiguration configuration)
    {
        var list = new List<Rule>();
        foreach (var child in configuration.GetSection("Raavi:Links:PathRewrites").GetChildren())
        {
            var prefix = (child["Prefix"] ?? string.Empty).Trim().TrimEnd('/');
            var baseUrl = (child["BaseUrl"] ?? string.Empty).Trim().TrimEnd('/');
            if (prefix.Length == 0 || baseUrl.Length == 0)
            {
                continue;
            }

            if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            {
                continue;
            }

            list.Add(new Rule(prefix, baseUrl));
        }

        _rules = list
            .OrderByDescending(r => r.Prefix.Length)
            .ToArray();
    }

    public static IReadOnlyList<Rule> All() => _rules;

    /// <summary>True when <paramref name="href"/> is a configured path-prefix link (safe to store).</summary>
    public static bool IsConfiguredPath(string href) => TryRewrite(href, out _);

    /// <summary>
    /// Maps <c>{Prefix}/rest...</c> to <c>{BaseUrl}/rest...</c>. Returns false when no rule matches.
    /// </summary>
    public static bool TryRewrite(string href, out string absolute)
    {
        absolute = string.Empty;
        if (string.IsNullOrWhiteSpace(href) || href != href.Trim() || href.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var rule in _rules)
        {
            if (href.Length == rule.Prefix.Length && href.Equals(rule.Prefix, StringComparison.Ordinal))
            {
                absolute = rule.BaseUrl + "/";
                return true;
            }

            if (href.Length > rule.Prefix.Length
                && href.StartsWith(rule.Prefix, StringComparison.Ordinal)
                && href[rule.Prefix.Length] == '/')
            {
                absolute = rule.BaseUrl + href[rule.Prefix.Length..];
                return true;
            }
        }

        return false;
    }
}
