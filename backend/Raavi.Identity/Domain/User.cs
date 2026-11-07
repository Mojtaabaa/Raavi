using Raavi.Domain.Common;

namespace Raavi.Identity.Domain;

public enum UserStatus
{
    Active = 1,
    Disabled = 2,
}

/// <summary>
/// Global identity (TENANT_ISOLATION.md §1, class G). A person has one User across all tenants; tenant access comes
/// only from <see cref="TenantMember"/>. Never listed across tenants.
/// </summary>
public sealed class User : Entity
{
    public const int MaxNameLength = 200;

    private User() { }

    /// <summary>Contact / local sign-in address. For local accounts it is the login name (unique when normalized).</summary>
    public string? Email { get; private set; }

    public string? NormalizedEmail { get; private set; }

    public bool EmailVerified { get; private set; }

    public string DisplayName { get; private set; } = string.Empty;

    public UserStatus Status { get; private set; } = UserStatus.Active;

    /// <summary>ASP.NET Core Identity v3 hash (PBKDF2). Null for users who only sign in through external providers.</summary>
    public string? PasswordHash { get; private set; }

    /// <summary>Changes whenever credentials change; sessions created before are invalid.</summary>
    public string SecurityStamp { get; private set; } = Guid.NewGuid().ToString("N");

    /// <summary>TOTP secret protected with ASP.NET Core Data Protection. Never stored or logged in clear.</summary>
    public string? ProtectedTotpSecret { get; private set; }

    public long? LastTotpTimeStep { get; private set; }

    public bool MfaEnabled => ProtectedTotpSecret is not null;

    public int FailedLoginCount { get; private set; }

    public DateTimeOffset? LockoutEndsAt { get; private set; }

    public string? PreferredLocale { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastLoginAt { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static string? Normalize(string? email) => string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToUpperInvariant();

    /// <summary>Minimal structural check (one @, non-empty local part and domain with a dot). Ownership is proven by invitation acceptance.</summary>
    public static string ValidateEmail(string? email)
    {
        var e = (email ?? string.Empty).Trim();
        var at = e.IndexOf('@', StringComparison.Ordinal);
        var valid = e.Length is >= 3 and <= 256 && at > 0 && at == e.LastIndexOf('@') && e.IndexOf('.', at) > at + 1 && !e.EndsWith('.') && !e.Any(char.IsWhiteSpace);
        return valid ? e : throw new DomainException("user.email_invalid", "A valid e-mail address is required.");
    }

    public static User CreateLocal(string email, string displayName, DateTimeOffset now)
    {
        var e = ValidateEmail(email);
        var user = new User { Email = e, NormalizedEmail = Normalize(e), CreatedAt = now };
        user.Rename(displayName);
        return user;
    }

    /// <summary>
    /// User created from an external identity. The e-mail is kept as contact information; it becomes the unique sign-in
    /// address (NormalizedEmail) only when the provider asserted it as verified — an unverified address must never
    /// occupy or collide with someone else's account.
    /// </summary>
    public static User CreateExternal(string? email, bool emailVerified, string displayName, DateTimeOffset now)
    {
        string? valid = null;
        if (!string.IsNullOrWhiteSpace(email))
        {
            try
            {
                valid = ValidateEmail(email);
            }
            catch (DomainException)
            {
                valid = null;
            }
        }

        var user = new User
        {
            Email = valid,
            NormalizedEmail = emailVerified ? Normalize(valid) : null,
            EmailVerified = emailVerified && valid is not null,
            CreatedAt = now,
        };
        user.Rename(string.IsNullOrWhiteSpace(displayName) ? (valid ?? "User") : displayName);
        return user;
    }

    public void Rename(string displayName)
    {
        var d = (displayName ?? string.Empty).Trim();
        DisplayName = d.Length is 0 or > MaxNameLength ? throw new DomainException("user.display_name_invalid", "Display name is required (max 200 characters).") : d;
    }

    /// <summary>Invitation acceptance proves control of the invited address.</summary>
    public void MarkEmailVerified() => EmailVerified = true;

    public void SetPasswordHash(string hash)
    {
        PasswordHash = hash;
        RotateSecurityStamp();
    }

    public void EnableTotp(string protectedSecret, long usedTimeStep)
    {
        ProtectedTotpSecret = protectedSecret;
        LastTotpTimeStep = usedTimeStep;
        RotateSecurityStamp();
    }

    /// <summary>Records a used TOTP time step; returns false for a replay of the same or an older step.</summary>
    public bool TryConsumeTotpStep(long timeStep)
    {
        if (LastTotpTimeStep is { } last && timeStep <= last)
        {
            return false;
        }

        LastTotpTimeStep = timeStep;
        return true;
    }

    public bool IsLockedOut(DateTimeOffset now) => LockoutEndsAt is { } end && end > now;

    public void RecordFailedLogin(DateTimeOffset now, int threshold, TimeSpan lockout)
    {
        FailedLoginCount++;
        if (FailedLoginCount >= threshold)
        {
            LockoutEndsAt = now + lockout;
            FailedLoginCount = 0;
        }
    }

    public void RecordSuccessfulLogin(DateTimeOffset now)
    {
        FailedLoginCount = 0;
        LockoutEndsAt = null;
        LastLoginAt = now;
    }

    public void SetPreferredLocale(string? locale) => PreferredLocale = locale is "fa" or "en" ? locale : null;

    public void Disable()
    {
        Status = UserStatus.Disabled;
        RotateSecurityStamp();
    }

    public void Enable() => Status = UserStatus.Active;

    private void RotateSecurityStamp() => SecurityStamp = Guid.NewGuid().ToString("N");
}

/// <summary>
/// Link between a Raavi user and an identity asserted by an external provider (AUTHENTICATION_ARCHITECTURE.md §6).
/// Identity key = (AuthenticationProviderId, ExternalSubjectId). Never matched by e-mail. Used by Phase 2b providers.
/// </summary>
public sealed class ExternalIdentity : Entity
{
    private ExternalIdentity() { }

    public Guid UserId { get; private set; }

    public string AuthenticationProviderId { get; private set; } = string.Empty;

    public string ExternalSubjectId { get; private set; } = string.Empty;

    public string? ExternalUsername { get; private set; }

    public string? ExternalEmail { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? LastAuthenticatedAt { get; private set; }

    public static ExternalIdentity Link(Guid userId, string providerId, string subjectId, string? username, string? email, DateTimeOffset now) => new()
    {
        UserId = userId,
        AuthenticationProviderId = providerId,
        ExternalSubjectId = subjectId,
        ExternalUsername = username,
        ExternalEmail = email,
        CreatedAt = now,
        LastAuthenticatedAt = now,
    };

    public void Touch(string? username, string? email, DateTimeOffset now)
    {
        ExternalUsername = username;
        ExternalEmail = email;
        LastAuthenticatedAt = now;
    }
}

public enum SystemRole
{
    SystemAdministrator = 1,
}

/// <summary>System-level role assignment (class S). Only effective on the system host with an MFA session.</summary>
public sealed class SystemRoleAssignment : Entity
{
    private SystemRoleAssignment() { }

    public Guid UserId { get; private set; }

    public SystemRole Role { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    public string GrantedBy { get; private set; } = string.Empty;

    public static SystemRoleAssignment Grant(Guid userId, SystemRole role, string grantedBy, DateTimeOffset now) =>
        new() { UserId = userId, Role = role, GrantedBy = grantedBy, GrantedAt = now };
}
