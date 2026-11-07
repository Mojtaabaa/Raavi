using System.Globalization;
using System.Text;

namespace Raavi.Search.Text;

/// <summary>
/// Canonical Persian/Arabic text normalization shared by query processing and indexing (ADR-013 §4).
/// The OpenSearch analyzer applies the same rules; this implementation is the reference used for queries,
/// suggestions and tests so both sides stay in agreement.
/// </summary>
public static class PersianTextNormalizer
{
    private const char Zwnj = '‌';

    /// <summary>
    /// - Arabic Yeh (ي, ى) → Persian Yeh (ی); Arabic Kaf (ك) → Persian Keheh (ک); Teh Marbuta (ة) → Heh (ه)
    /// - Arabic-Indic and Extended Arabic-Indic (Persian) digits → ASCII digits
    /// - Tashkil (harakat) and tatweel (ـ) removed
    /// - ZWNJ and other zero-width/bidi format characters → single space (tokenization boundary); runs of whitespace collapsed
    /// - Unicode NFKC, then lower-case invariant for Latin text
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var source = text.Normalize(NormalizationForm.FormKC);
        var sb = new StringBuilder(source.Length);
        var lastWasSpace = true;

        foreach (var ch in source)
        {
            var mapped = Map(ch);
            if (mapped == '\0')
            {
                continue;
            }

            if (char.IsWhiteSpace(mapped))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            sb.Append(char.ToLowerInvariant(mapped));
            lastWasSpace = false;
        }

        if (sb.Length > 0 && sb[^1] == ' ')
        {
            sb.Length--;
        }

        return sb.ToString();
    }

    private static char Map(char ch)
    {
        switch (ch)
        {
            case 'ي' or 'ى': return 'ی'; // Arabic Yeh / Alef Maksura → Persian Yeh
            case 'ك': return 'ک';             // Arabic Kaf → Keheh
            case 'ة': return 'ه';             // Teh Marbuta → Heh
            case 'ـ': return '\0';                 // Tatweel
            case Zwnj or '​' or '‍' or '‎' or '‏' or '‪' or '‫' or '‬' or '‭' or '‮' or '﻿':
                return ' ';
        }

        if (ch is >= '٠' and <= '٩')
        {
            return (char)('0' + (ch - '٠'));
        }

        if (ch is >= '۰' and <= '۹')
        {
            return (char)('0' + (ch - '۰'));
        }

        return CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark ? '\0' : ch;
    }
}
