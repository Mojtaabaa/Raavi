using System.Text.RegularExpressions;
using Raavi.Domain.Common;

namespace Raavi.Tenancy.Domain;

/// <summary>Typed, tenant-owned configuration stored in SQL Server (SYSTEM_ARCHITECTURE.md §6). RLS-protected.</summary>
public sealed class TenantSettings : Entity, ITenantOwned
{
    public static readonly IReadOnlySet<string> SupportedLocales = new HashSet<string>(StringComparer.Ordinal) { "fa", "en" };
    public static readonly IReadOnlySet<string> SupportedCalendars = new HashSet<string>(StringComparer.Ordinal) { "persian", "gregorian" };

    private TenantSettings() { }

    public Guid TenantId { get; private set; }

    public string DefaultLocale { get; private set; } = "fa";

    public string TimeZone { get; private set; } = "Asia/Tehran";

    public string Calendar { get; private set; } = "persian";

    /// <summary>Null = no tenant-specific quota (global default applies).</summary>
    public long? StorageQuotaBytes { get; private set; }

    public int SettingsVersion { get; private set; } = 1;

    public byte[] RowVersion { get; private set; } = [];

    public static TenantSettings CreateDefault(string locale, string timeZone, string calendar)
    {
        var s = new TenantSettings();
        s.Update(locale, timeZone, calendar, null);
        return s;
    }

    public void Update(string locale, string timeZone, string calendar, long? storageQuotaBytes)
    {
        if (!SupportedLocales.Contains(locale ?? string.Empty))
        {
            throw new DomainException("settings.locale_unsupported", "Unsupported locale.");
        }

        if (!SupportedCalendars.Contains(calendar ?? string.Empty))
        {
            throw new DomainException("settings.calendar_unsupported", "Unsupported calendar.");
        }

        if (string.IsNullOrWhiteSpace(timeZone) || timeZone.Length > 64 || !TimeZoneInfo.TryFindSystemTimeZoneById(timeZone, out _))
        {
            throw new DomainException("settings.timezone_invalid", "Unknown IANA time zone.");
        }

        if (storageQuotaBytes is < 0)
        {
            throw new DomainException("settings.quota_invalid", "Storage quota cannot be negative.");
        }

        DefaultLocale = locale!;
        TimeZone = timeZone;
        Calendar = calendar!;
        StorageQuotaBytes = storageQuotaBytes;
    }
}

/// <summary>Tenant branding (MULTI_TENANT_ARCHITECTURE.md §8). Plain values only — nothing here can carry script or CSS.</summary>
public sealed partial class TenantBranding : Entity, ITenantOwned
{
    public const int MaxWelcomeLength = 500;

    private TenantBranding() { }

    public Guid TenantId { get; private set; }

    public string? PrimaryColor { get; private set; }

    public string? SecondaryColor { get; private set; }

    /// <summary>Plain text, rendered as text (never HTML).</summary>
    public string? WelcomeMessageFa { get; private set; }

    public string? WelcomeMessageEn { get; private set; }

    /// <summary>Version of the logo picture (0 = no logo); part of the logo's address so it caches for good.</summary>
    public int LogoVersion { get; private set; }

    public void SetLogoVersion(int version) => LogoVersion = Math.Max(0, version);

    public byte[] RowVersion { get; private set; } = [];

    public static TenantBranding CreateEmpty() => new();

    public void Update(string? primaryColor, string? secondaryColor, string? welcomeFa, string? welcomeEn)
    {
        PrimaryColor = Color(primaryColor);
        SecondaryColor = Color(secondaryColor);
        WelcomeMessageFa = Text(welcomeFa);
        WelcomeMessageEn = Text(welcomeEn);
    }

    private static string? Color(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return HexColor().IsMatch(value) ? value.ToLowerInvariant() : throw new DomainException("branding.color_invalid", "Colors must be #rrggbb.");
    }

    private static string? Text(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var v = value.Trim();
        return v.Length > MaxWelcomeLength ? throw new DomainException("branding.welcome_too_long", "Welcome message is too long.") : v;
    }

    [GeneratedRegex("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant)]
    private static partial Regex HexColor();
}
