using Raavi.Domain.Common;

namespace Raavi.Identity.Domain;

/// <summary>
/// Server-side browser session (AUTHENTICATION_ARCHITECTURE.md §8). The cookie carries only a random opaque value;
/// only its SHA-256 hash is stored. A session is bound to exactly one host scope: one tenant (TenantId) or the system
/// host (TenantId null). Global table (class G) because it is validated before membership is known.
/// </summary>
public sealed class AuthenticationSession : Entity
{
    private AuthenticationSession() { }

    public string TokenHash { get; private set; } = string.Empty;

    public Guid UserId { get; private set; }

    /// <summary>Tenant the session was issued for; null = system host session.</summary>
    public Guid? TenantId { get; private set; }

    public string ProviderId { get; private set; } = string.Empty;

    /// <summary>True when the session was established with a second factor.</summary>
    public bool MfaSatisfied { get; private set; }

    /// <summary>User security stamp at issue time; a later credential change invalidates the session.</summary>
    public string SecurityStamp { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset LastSeenAt { get; private set; }

    public DateTimeOffset IdleExpiresAt { get; private set; }

    public DateTimeOffset AbsoluteExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public string? RevokedReason { get; private set; }

    public string? IpAddress { get; private set; }

    public string? UserAgent { get; private set; }

    public static AuthenticationSession Issue(string tokenHash, Guid userId, Guid? tenantId, string providerId, bool mfa, string securityStamp, DateTimeOffset now, TimeSpan idle, TimeSpan absolute, string? ip, string? userAgent) => new()
    {
        TokenHash = tokenHash,
        UserId = userId,
        TenantId = tenantId,
        ProviderId = providerId,
        MfaSatisfied = mfa,
        SecurityStamp = securityStamp,
        CreatedAt = now,
        LastSeenAt = now,
        IdleExpiresAt = now + idle,
        AbsoluteExpiresAt = now + absolute,
        IpAddress = ip?.Length > 64 ? ip[..64] : ip,
        UserAgent = userAgent?.Length > 256 ? userAgent[..256] : userAgent,
    };

    public bool IsValid(DateTimeOffset now) => RevokedAt is null && IdleExpiresAt > now && AbsoluteExpiresAt > now;

    public void Touch(DateTimeOffset now, TimeSpan idle)
    {
        LastSeenAt = now;
        var next = now + idle;
        IdleExpiresAt = next < AbsoluteExpiresAt ? next : AbsoluteExpiresAt;
    }

    public void Revoke(DateTimeOffset now, string reason)
    {
        if (RevokedAt is null)
        {
            RevokedAt = now;
            RevokedReason = reason.Length > 64 ? reason[..64] : reason;
        }
    }
}

/// <summary>Invitation to become a system administrator (class S). MFA enrollment is part of acceptance.</summary>
public sealed class SystemInvitation : Entity
{
    private SystemInvitation() { }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    public string TokenHash { get; private set; } = string.Empty;

    /// <summary>TOTP secret generated at enrollment start, protected with Data Protection; moved to the user on completion.</summary>
    public string? PendingProtectedTotpSecret { get; private set; }

    public string InvitedBy { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public bool IsUsable(DateTimeOffset now) => AcceptedAt is null && ExpiresAt > now;

    public static SystemInvitation Create(string email, string tokenHash, string invitedBy, DateTimeOffset now, TimeSpan lifetime) => new()
    {
        Email = User.ValidateEmail(email),
        NormalizedEmail = User.Normalize(User.ValidateEmail(email))!,
        TokenHash = tokenHash,
        InvitedBy = invitedBy,
        CreatedAt = now,
        ExpiresAt = now + lifetime,
    };

    public void SetPendingTotp(string protectedSecret) => PendingProtectedTotpSecret = protectedSecret;

    public void Accept(DateTimeOffset now) => AcceptedAt = now;
}

/// <summary>Security audit inside a tenant (logins, membership/role/group/policy changes). Tenant-owned, RLS-protected.</summary>
public sealed class TenantSecurityEvent : Entity, ITenantOwned
{
    private TenantSecurityEvent() { }

    public Guid TenantId { get; private set; }

    public DateTimeOffset OccurredAt { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public Guid? UserId { get; private set; }

    public string ActorId { get; private set; } = string.Empty;

    public string? IpAddress { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    /// <summary>Reason codes and non-sensitive identifiers only — never passwords, tokens or codes.</summary>
    public string? Details { get; private set; }

    public static TenantSecurityEvent Create(DateTimeOffset now, string type, Guid? userId, string actorId, string? ip, string correlationId, string? details) => new()
    {
        OccurredAt = now,
        EventType = type,
        UserId = userId,
        ActorId = actorId,
        IpAddress = ip,
        CorrelationId = correlationId,
        Details = details,
    };
}

/// <summary>Security audit on the system host (system admin logins, enrollment). Class S.</summary>
public sealed class SystemSecurityEvent : Entity
{
    private SystemSecurityEvent() { }

    public DateTimeOffset OccurredAt { get; private set; }

    public string EventType { get; private set; } = string.Empty;

    public Guid? UserId { get; private set; }

    public string ActorId { get; private set; } = string.Empty;

    public string? IpAddress { get; private set; }

    public string CorrelationId { get; private set; } = string.Empty;

    public string? Details { get; private set; }

    public static SystemSecurityEvent Create(DateTimeOffset now, string type, Guid? userId, string actorId, string? ip, string correlationId, string? details) => new()
    {
        OccurredAt = now,
        EventType = type,
        UserId = userId,
        ActorId = actorId,
        IpAddress = ip,
        CorrelationId = correlationId,
        Details = details,
    };
}
