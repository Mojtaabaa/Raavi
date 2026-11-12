using System.Globalization;
using System.Text.RegularExpressions;

namespace Raavi.AI;

/// <summary>EKOS-10: parse and sanitise model citation markers <c>[n]</c> against the real source list.</summary>
public static partial class CitationRefs
{
    [GeneratedRegex(@"\[(\d{1,2})\]", RegexOptions.CultureInvariant)]
    private static partial Regex Marker();

    /// <summary>Source numbers (1-based) that appear in <paramref name="text"/> and fall in 1..<paramref name="sourceCount"/>.</summary>
    public static HashSet<int> CitedIn(string? text, int sourceCount)
    {
        var set = new HashSet<int>();
        if (string.IsNullOrEmpty(text) || sourceCount <= 0)
        {
            return set;
        }

        foreach (Match m in Marker().Matches(text))
        {
            if (int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                && n is >= 1 && n <= sourceCount)
            {
                set.Add(n);
            }
        }

        return set;
    }

    /// <summary>Turn out-of-range markers into plain digits so the UI never links to a missing source.</summary>
    public static string StripInvalid(string? text, int sourceCount)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text ?? string.Empty;
        }

        return Marker().Replace(text, m =>
        {
            if (!int.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)
                || n < 1 || n > sourceCount)
            {
                return m.Groups[1].Value;
            }

            return m.Value;
        });
    }
}
