using System.Text;

namespace Raavi.Application.Common;

/// <summary>
/// Folds text for searching so that spelling variants meet: Arabic and Persian letter forms (ي ى → ی, ك → ک, ة → ه, أ إ ٱ آ → ا, ؤ → و, ئ → ی,
/// ۀ → ه), digits of every script (۱٢٣ → 123), Arabic diacritics and tatweel (removed), the zero-width non-joiner (→ a space, so "می‌شود" and
/// "می شود" match), no-break spaces, and case. The SAME function is applied to what is stored for search and to what is typed, so both sides
/// always agree. The browser has a twin of it (<c>core/runtime/textNormalize.ts</c>) used for highlighting; a test keeps them equal.
/// </summary>
public static class SearchText
{
    public static string Normalize(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            switch (ch)
            {
                case 'ي' or 'ى' or 'ئ' or 'ی':
                    sb.Append('ی');
                    break;
                case 'ك' or 'ک':
                    sb.Append('ک');
                    break;
                case 'ة' or 'ۀ' or 'ە':
                    sb.Append('ه');
                    break;
                case 'أ' or 'إ' or 'ٱ' or 'آ':
                    sb.Append('ا');
                    break;
                case 'ؤ':
                    sb.Append('و');
                    break;
                case '‌' or '‍' or ' ' or ' ':
                    sb.Append(' ');
                    break;
                case 'ـ' or (>= 'ً' and <= 'ٟ') or 'ٰ' or (>= 'ۖ' and <= 'ۭ'):
                    break; // tatweel and combining marks
                case >= '٠' and <= '٩':
                    sb.Append((char)('0' + (ch - '٠')));
                    break;
                case >= '۰' and <= '۹':
                    sb.Append((char)('0' + (ch - '۰')));
                    break;
                default:
                    sb.Append(char.ToLowerInvariant(ch));
                    break;
            }
        }

        return sb.ToString();
    }

    /// <summary>The ways a title may have been typed in the database (Persian vs Arabic letter forms): used for plain SQL <c>Contains</c> where no normalised column exists.</summary>
    public static IReadOnlyList<string> Variants(string text)
    {
        var persian = text.Replace('ي', 'ی').Replace('ى', 'ی').Replace('ك', 'ک');
        var arabic = text.Replace('ی', 'ي').Replace('ک', 'ك');
        return [.. new[] { text, persian, arabic }.Distinct(StringComparer.Ordinal)];
    }
}
