using System.Text.RegularExpressions;
using Raavi.Domain.Common;
using Raavi.Tenancy.Resolution;

namespace Raavi.Tenancy.Domain;

/// <summary>System-level aggregate (MULTI_TENANT_ARCHITECTURE.md §1). Not tenant-owned: it <i>is</i> the tenant.</summary>
public sealed partial class Tenant : Entity
{
    public const int MaxDisplayNameLength = 200;

    /// <summary>Labels that can never be tenant slugs because they are (or may become) infrastructure host names.</summary>
    public static readonly IReadOnlySet<string> ReservedSlugs = new HashSet<string>(StringComparer.Ordinal)
    {
        "admin", "api", "app", "auth", "collab", "cdn", "docs", "help", "internal", "login", "mail", "raavi",
        "smtp", "sso", "static", "status", "support", "system", "www",
    };

    private Tenant() { }

    public string Code { get; private set; } = string.Empty;

    public string Slug { get; private set; } = string.Empty;

    public string DisplayNameFa { get; private set; } = string.Empty;

    public string DisplayNameEn { get; private set; } = string.Empty;

    public TenantStatus Status { get; private set; }

    public string? StatusReason { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? ActivatedAt { get; private set; }

    public DateTimeOffset? SuspendedAt { get; private set; }

    public DateTimeOffset? DeactivatedAt { get; private set; }

    public DateTimeOffset? SubscriptionEndsAt { get; private set; }

    /// <summary>When deletion was requested and when the data will be purged automatically (grace period, ADR-050).</summary>
    public DateTimeOffset? DeletionRequestedAt { get; private set; }

    public DateTimeOffset? DeletionScheduledFor { get; private set; }

    public string? DeletionRequestedBy { get; private set; }

    /// <summary>The status to return to when the deletion is cancelled.</summary>
    public TenantStatus? StatusBeforeDeletion { get; private set; }

    public DateTimeOffset? DeletedAt { get; private set; }

    public byte[] RowVersion { get; private set; } = [];

    public static Tenant Create(string code, string slug, string displayNameFa, string displayNameEn, DateTimeOffset now)
    {
        var normalizedCode = (code ?? string.Empty).Trim().ToUpperInvariant();
        if (!CodePattern().IsMatch(normalizedCode))
        {
            throw new DomainException("tenant.code_invalid", "Tenant code must be 3–16 characters: a letter followed by letters or digits.");
        }

        var normalizedSlug = (slug ?? string.Empty).Trim().ToLowerInvariant();
        if (!TenantHost.IsValidSlug(normalizedSlug))
        {
            throw new DomainException("tenant.slug_invalid", "Tenant slug must be a DNS label of 2–63 lower-case letters, digits or hyphens.");
        }

        if (ReservedSlugs.Contains(normalizedSlug))
        {
            throw new DomainException("tenant.slug_reserved", "This slug is reserved.");
        }

        var tenant = new Tenant
        {
            Code = normalizedCode,
            Slug = normalizedSlug,
            Status = TenantStatus.Provisioning,
            CreatedAt = now,
        };
        tenant.Rename(displayNameFa, displayNameEn);
        return tenant;
    }

    public void Rename(string displayNameFa, string displayNameEn)
    {
        DisplayNameFa = RequireName(displayNameFa, "tenant.display_name_fa_invalid");
        DisplayNameEn = RequireName(displayNameEn, "tenant.display_name_en_invalid");
    }

    /// <summary>Provisioning/Suspended/Deactivated → Active.</summary>
    public void Activate(DateTimeOffset now)
    {
        if (Status is not (TenantStatus.Provisioning or TenantStatus.Suspended or TenantStatus.Deactivated))
        {
            throw Transition("activate");
        }

        Status = TenantStatus.Active;
        StatusReason = null;
        ActivatedAt = now;
        SuspendedAt = null;
        DeactivatedAt = null;
    }

    /// <summary>Active → Suspended. Data is untouched; access stops immediately.</summary>
    public void Suspend(string reason, DateTimeOffset now)
    {
        if (Status != TenantStatus.Active)
        {
            throw Transition("suspend");
        }

        var validReason = RequireReason(reason); // validate before mutating any state
        Status = TenantStatus.Suspended;
        StatusReason = validReason;
        SuspendedAt = now;
    }

    /// <summary>Active/Suspended → Deactivated (reversible).</summary>
    public void Deactivate(string reason, DateTimeOffset now)
    {
        if (Status is not (TenantStatus.Active or TenantStatus.Suspended))
        {
            throw Transition("deactivate");
        }

        var validReason = RequireReason(reason);
        Status = TenantStatus.Deactivated;
        StatusReason = validReason;
        DeactivatedAt = now;
    }

    /// <summary>Any live status → PendingDeletion: access stops now, the data is purged when <paramref name="scheduledFor"/> passes.</summary>
    public void RequestDeletion(string reason, string requestedBy, DateTimeOffset scheduledFor, DateTimeOffset now)
    {
        if (Status is TenantStatus.PendingDeletion or TenantStatus.Deleted)
        {
            throw Transition("request deletion of");
        }

        var validReason = RequireReason(reason);
        StatusBeforeDeletion = Status;
        Status = TenantStatus.PendingDeletion;
        StatusReason = validReason;
        DeletionRequestedAt = now;
        DeletionScheduledFor = scheduledFor;
        DeletionRequestedBy = requestedBy;
    }

    /// <summary>PendingDeletion → the status before the request (nothing was purged yet).</summary>
    public void CancelDeletion()
    {
        if (Status != TenantStatus.PendingDeletion)
        {
            throw Transition("cancel the deletion of");
        }

        Status = StatusBeforeDeletion is null or TenantStatus.Provisioning ? TenantStatus.Deactivated : StatusBeforeDeletion.Value;
        StatusReason = null;
        StatusBeforeDeletion = null;
        DeletionRequestedAt = null;
        DeletionScheduledFor = null;
        DeletionRequestedBy = null;
    }

    /// <summary>PendingDeletion → Deleted (a tombstone: code and slug stay reserved, the data is gone).</summary>
    public void MarkDeleted(DateTimeOffset now)
    {
        if (Status != TenantStatus.PendingDeletion)
        {
            throw Transition("purge");
        }

        Status = TenantStatus.Deleted;
        DeletedAt = now;
    }

    private DomainException Transition(string action) =>
        new("tenant.invalid_transition", $"Cannot {action} a tenant in status {Status}.");

    private static string RequireName(string value, string code)
    {
        var v = (value ?? string.Empty).Trim();
        return v.Length is 0 or > MaxDisplayNameLength ? throw new DomainException(code, "Display name is required (max 200 characters).") : v;
    }

    private static string RequireReason(string reason)
    {
        var v = (reason ?? string.Empty).Trim();
        return v.Length is 0 or > 500 ? throw new DomainException("tenant.reason_required", "A reason (max 500 characters) is required.") : v;
    }

    [GeneratedRegex("^[A-Z][A-Z0-9]{2,15}$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
