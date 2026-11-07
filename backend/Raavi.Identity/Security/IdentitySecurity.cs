using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Raavi.Domain.Common;
using Raavi.Identity.Domain;

namespace Raavi.Identity.Security;

/// <summary><c>Raavi:Authentication</c> (sandbox/config/authentication.json).</summary>
public sealed class RaaviAuthenticationOptions
{
    public const string Section = "Raavi:Authentication";

    public SessionSettings Session { get; set; } = new();

    public LocalSettings Local { get; set; } = new();

    public int InvitationLifetimeHours { get; set; } = 72;

    public sealed class SessionSettings
    {
        public string CookieName { get; set; } = "__Host-raavi";

        public int IdleTimeoutMinutes { get; set; } = 60;

        public int AbsoluteTimeoutHours { get; set; } = 12;

        /// <summary>
        /// Development only: plain-http dev servers (e.g. http://acme.raavi.localhost:5173) cannot store Secure cookies.
        /// When true, cookies are not Secure and lose the __Host- prefix. Startup fails if enabled outside Development.
        /// </summary>
        public bool AllowInsecureHttp { get; set; }

        public string EffectiveCookieName => AllowInsecureHttp ? "raavi-session" : CookieName;

        public string AntiforgeryCookieName => AllowInsecureHttp ? "raavi-af" : "__Host-raavi-af";
    }

    public sealed class LocalSettings
    {
        public bool Enabled { get; set; } = true;

        public int PasswordMinLength { get; set; } = 12;

        public int LockoutThreshold { get; set; } = 5;

        public int LockoutMinutes { get; set; } = 15;
    }
}

/// <summary>Random opaque tokens (sessions, invitations). Only the SHA-256 hex hash is ever persisted.</summary>
public static class OpaqueToken
{
    public static string Create() => Base64Url(RandomNumberGenerator.GetBytes(32));

    public static string Hash(string token) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(token ?? string.Empty)));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

/// <summary>Password hashing (ASP.NET Core Identity v3 format, PBKDF2-HMAC-SHA512) and policy.</summary>
public sealed class PasswordService(Microsoft.Extensions.Options.IOptions<RaaviAuthenticationOptions> options)
{
    private const int MaxLength = 128;
    private readonly PasswordHasher<User> _hasher = new();

    public void EnsureAcceptable(string password, string? email)
    {
        var min = options.Value.Local.PasswordMinLength;
        if (string.IsNullOrEmpty(password) || password.Length < min || password.Length > MaxLength)
        {
            throw new DomainException("password.length", $"Password must be {min}–{MaxLength} characters.");
        }

        var local = email?.Split('@')[0];
        if (!string.IsNullOrWhiteSpace(local) && local.Length >= 3 && password.Contains(local, StringComparison.OrdinalIgnoreCase))
        {
            throw new DomainException("password.contains_email", "Password must not contain your e-mail name.");
        }

        if (password.Distinct().Count() < 5)
        {
            throw new DomainException("password.too_simple", "Password is too simple.");
        }
    }

    public string Hash(User user, string password) => _hasher.HashPassword(user, password);

    /// <summary>Returns (valid, needsRehash).</summary>
    public (bool Valid, bool Rehash) Verify(User user, string password)
    {
        if (user.PasswordHash is null || string.IsNullOrEmpty(password) || password.Length > MaxLength)
        {
            return (false, false);
        }

        var result = _hasher.VerifyHashedPassword(user, user.PasswordHash, password);
        return (result != PasswordVerificationResult.Failed, result == PasswordVerificationResult.SuccessRehashNeeded);
    }

    /// <summary>Burns comparable CPU when the user does not exist, so timing does not reveal account existence.</summary>
    public void SimulateVerify(string password) => _ = _hasher.VerifyHashedPassword(DummyUser, DummyHash, password ?? string.Empty);

    private static readonly User DummyUser = User.CreateLocal("timing@raavi.invalid", "timing", DateTimeOffset.UnixEpoch);
    private static readonly string DummyHash = new PasswordHasher<User>().HashPassword(DummyUser, "timing-equalization-password");
}

/// <summary>RFC 6238 TOTP (SHA-1, 30 s, 6 digits, ±1 step) — compatible with common authenticator apps.</summary>
public sealed class TotpService(IDataProtectionProvider dataProtection)
{
    private const int Digits = 6;
    private const int StepSeconds = 30;
    private readonly IDataProtector _protector = dataProtection.CreateProtector("Raavi.Identity.Totp.v1");

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(20);

    public string Protect(byte[] secret) => _protector.Protect(Convert.ToBase64String(secret));

    public byte[] Unprotect(string protectedSecret) => Convert.FromBase64String(_protector.Unprotect(protectedSecret));

    public static string ToBase32(byte[] data)
    {
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            sb.Append(alphabet[(buffer << (5 - bits)) & 31]);
        }

        return sb.ToString();
    }

    public static string OtpAuthUri(string issuer, string account, byte[] secret) =>
        $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={ToBase32(secret)}&issuer={Uri.EscapeDataString(issuer)}&digits={Digits}&period={StepSeconds}";

    public static long TimeStep(DateTimeOffset now) => now.ToUnixTimeSeconds() / StepSeconds;

    public static string Code(byte[] secret, long step)
    {
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
#pragma warning disable CA5350 // HMAC-SHA1 is mandated by RFC 6238 / authenticator-app interoperability
        var hash = HMACSHA1.HashData(secret, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Returns the matched time step (for replay protection) or null.</summary>
    public static long? Verify(byte[] secret, string? code, DateTimeOffset now)
    {
        if (code is null || code.Length != Digits || !code.All(char.IsAsciiDigit))
        {
            return null;
        }

        var current = TimeStep(now);
        for (var delta = -1; delta <= 1; delta++)
        {
            var step = current + delta;
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(code)))
            {
                return step;
            }
        }

        return null;
    }
}
