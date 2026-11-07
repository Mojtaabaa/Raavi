using Raavi.Domain.Common;

namespace Raavi.Identity.Domain;

public enum MembershipStatus
{
    Active = 1,
    Suspended = 2,
    PendingApproval = 3,
}

/// <summary>Answers "may this user enter this tenant". Tenant-owned, RLS-protected.</summary>
public sealed class TenantMember : Entity, ITenantOwned
{
    private TenantMember() { }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public MembershipStatus Status { get; private set; }

    public DateTimeOffset JoinedAt { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static TenantMember Join(Guid userId, DateTimeOffset now) => new() { UserId = userId, Status = MembershipStatus.Active, JoinedAt = now };

    /// <summary>Provisioned by an external provider under the AdministratorApproval policy; cannot sign in until activated.</summary>
    public static TenantMember JoinPendingApproval(Guid userId, DateTimeOffset now) => new() { UserId = userId, Status = MembershipStatus.PendingApproval, JoinedAt = now };

    public void Suspend() => Status = MembershipStatus.Suspended;

    public void Activate() => Status = MembershipStatus.Active;
}

/// <summary>Built-in tenant roles. Fine-grained space/content permissions arrive with ADR-012 in Phase 3.</summary>
public enum TenantRole
{
    TenantAdministrator = 1,
}

public sealed class TenantRoleAssignment : Entity, ITenantOwned
{
    private TenantRoleAssignment() { }

    public Guid TenantId { get; private set; }

    public Guid UserId { get; private set; }

    public TenantRole Role { get; private set; }

    public DateTimeOffset GrantedAt { get; private set; }

    public static TenantRoleAssignment Grant(Guid userId, TenantRole role, DateTimeOffset now) => new() { UserId = userId, Role = role, GrantedAt = now };
}

public enum GroupSource
{
    Local = 1,
    External = 2,
}

public sealed class TenantGroup : Entity, ITenantOwned
{
    public const int MaxNameLength = 128;

    private TenantGroup() { }

    public Guid TenantId { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string NormalizedName { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public GroupSource Source { get; private set; } = GroupSource.Local;

    /// <summary>Provider id + external group key for synchronized groups (Phase 2b).</summary>
    public string? ExternalKey { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    /// <summary>Group mirrored from an external provider (key = "{providerId}:{externalGroup}"). Membership is synchronized at each sign-in.</summary>
    public static TenantGroup CreateExternal(string name, string externalKey, DateTimeOffset now)
    {
        var g = Create(name, null, now);
        g.Source = GroupSource.External;
        g.ExternalKey = externalKey;
        return g;
    }

    public static TenantGroup Create(string name, string? description, DateTimeOffset now)
    {
        var g = new TenantGroup { CreatedAt = now };
        g.Update(name, description);
        return g;
    }

    public void Update(string name, string? description)
    {
        var n = (name ?? string.Empty).Trim();
        if (n.Length is 0 or > MaxNameLength)
        {
            throw new DomainException("group.name_invalid", "Group name is required (max 128 characters).");
        }

        var d = description?.Trim();
        if (d is { Length: > 500 })
        {
            throw new DomainException("group.description_too_long", "Description is too long.");
        }

        Name = n;
        NormalizedName = n.ToUpperInvariant();
        Description = string.IsNullOrEmpty(d) ? null : d;
    }
}

public sealed class TenantGroupMember : Entity, ITenantOwned
{
    private TenantGroupMember() { }

    public Guid TenantId { get; private set; }

    public Guid GroupId { get; private set; }

    public Guid UserId { get; private set; }

    public DateTimeOffset AddedAt { get; private set; }

    public static TenantGroupMember Add(Guid groupId, Guid userId, DateTimeOffset now) => new() { GroupId = groupId, UserId = userId, AddedAt = now };
}

/// <summary>
/// Invitation into a tenant. Token is stored only as a SHA-256 hash. The invitation never reveals whether the e-mail
/// already belongs to a Raavi user: the admin always receives the same response, and the outcome is decided only
/// when the invitee proves control (new user sets a password; existing user signs in).
/// </summary>
public sealed class TenantInvitation : Entity, ITenantOwned
{
    private TenantInvitation() { }

    public Guid TenantId { get; private set; }

    public string Email { get; private set; } = string.Empty;

    public string NormalizedEmail { get; private set; } = string.Empty;

    public string TokenHash { get; private set; } = string.Empty;

    public TenantRole? Role { get; private set; }

    public string InvitedBy { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset ExpiresAt { get; private set; }

    public DateTimeOffset? AcceptedAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    public bool IsUsable(DateTimeOffset now) => AcceptedAt is null && RevokedAt is null && ExpiresAt > now;

    public static TenantInvitation Create(string email, string tokenHash, TenantRole? role, string invitedBy, DateTimeOffset now, TimeSpan lifetime)
    {
        var valid = User.ValidateEmail(email);
        return new TenantInvitation
        {
            Email = valid,
            NormalizedEmail = User.Normalize(valid)!,
            TokenHash = tokenHash,
            Role = role,
            InvitedBy = invitedBy,
            CreatedAt = now,
            ExpiresAt = now + lifetime,
        };
    }

    public void Accept(DateTimeOffset now) => AcceptedAt = now;

    public void Revoke(DateTimeOffset now) => RevokedAt = now;
}

/// <summary>
/// Tenant enablement/policy for a globally defined provider (ADR-006). The global definition (endpoints, secrets)
/// lives in sandbox/config; this row can only enable, order and narrow it.
/// </summary>
public sealed class TenantAuthenticationProvider : Entity, ITenantOwned
{
    private TenantAuthenticationProvider() { }

    public Guid TenantId { get; private set; }

    public string ProviderId { get; private set; } = string.Empty;

    public bool Enabled { get; private set; }

    public bool IsDefault { get; private set; }

    public int DisplayOrder { get; private set; }

    public Raavi.Identity.Providers.ProvisioningMode Provisioning { get; private set; } = Raavi.Identity.Providers.ProvisioningMode.ExistingUsersOnly;

    /// <summary>Comma-separated lower-case e-mail domains; empty = no restriction.</summary>
    public string AllowedEmailDomains { get; private set; } = string.Empty;

    /// <summary>Comma-separated external group keys; empty = no restriction.</summary>
    public string AllowedExternalGroups { get; private set; } = string.Empty;

    public static TenantAuthenticationProvider Create(string providerId) => new() { ProviderId = providerId };

    public void Update(bool enabled, bool isDefault, int order, Raavi.Identity.Providers.ProvisioningMode provisioning, IEnumerable<string> domains, IEnumerable<string> groups)
    {
        Enabled = enabled;
        IsDefault = isDefault && enabled;
        DisplayOrder = order;
        Provisioning = provisioning;
        AllowedEmailDomains = string.Join(',', domains.Select(d => d.Trim().ToLowerInvariant()).Where(d => d.Length > 0).Distinct());
        AllowedExternalGroups = string.Join(',', groups.Select(g => g.Trim()).Where(g => g.Length > 0).Distinct());
        if (AllowedEmailDomains.Length > 1000 || AllowedExternalGroups.Length > 2000)
        {
            throw new DomainException("provider_policy.too_long", "Too many domain or group restrictions.");
        }
    }

    public void ClearDefault() => IsDefault = false;
}
