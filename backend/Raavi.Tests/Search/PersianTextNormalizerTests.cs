using Raavi.Search.Text;

namespace Raavi.Tests.Search;

public sealed class PersianTextNormalizerTests
{
    [Theory]
    [InlineData("كتاب", "کتاب")]                 // Arabic Kaf
    [InlineData("علي", "علی")]                   // Arabic Yeh
    [InlineData("موسى", "موسی")]                 // Alef Maksura
    [InlineData("مدرسة", "مدرسه")]               // Teh Marbuta
    [InlineData("۱۴۰۳", "1403")]                 // Persian digits
    [InlineData("٢٠٢٤", "2024")]                 // Arabic-Indic digits
    [InlineData("مـــدیر", "مدیر")]              // Tatweel
    [InlineData("كِتَابٌ", "کتاب")]               // Harakat
    [InlineData("می‌روم", "می روم")]         // ZWNJ is a token boundary
    [InlineData("  Raavi   راوی  ", "raavi راوی")] // mixed script, whitespace collapse, Latin lower-case
    [InlineData("", "")]
    public void Normalizes(string input, string expected)
    {
        Assert.Equal(expected, PersianTextNormalizer.Normalize(input));
    }

    [Fact]
    public void Removes_bidi_control_characters()
    {
        Assert.Equal("abc def", PersianTextNormalizer.Normalize("abc‮def‬"));
    }
}
