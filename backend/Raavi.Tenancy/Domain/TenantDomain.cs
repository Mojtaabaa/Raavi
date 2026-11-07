using System.Security.Cryptography;
using Raavi.Domain.Common;

namespace Raavi.Tenancy.Domain;

public enum TenantDomainStatus
{
    PendingVerification = 1,
    Verified = 2,
    VerificationLost = 3,
}

/// <summary>
/// Custom domain mapping (MULTI_TENANT_ARCHITECTURE.md §5). System-level: resolution must look hosts up before any
/// tenant is known, so this table is not RLS-protected; tenant-facing APIs filter it explicitly by tenant.
/// Only <see cref="TenantDomainStatus.Verified"/>, non-removed domains resolve.
/// </summary>
public sealed class TenantDomain : Entity
{
    public const int FailuresBeforeLoss = 3;

    private TenantDomain() { }

    public Guid TenantId { get; private set; }

    public string Host { get; private set; } = string.Empty;

    public bool IsPrimary { get; private set; }

    public TenantDomainStatus Status { get; private set; }

    /// <summary>Public value published in DNS; not a secret.</summary>
    public string VerificationToken { get; private set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset? VerifiedAt { get; private set; }

    public DateTimeOffset? LastCheckedAt { get; private set; }

    public int ConsecutiveFailures { get; private set; }

    public DateTimeOffset? RemovedAt { get; private set; }

    public bool IsRemoved => RemovedAt is not null;

    public bool Resolves => !IsRemoved && Status == TenantDomainStatus.Verified;

    public static TenantDomain Create(Guid tenantId, string normalizedHost, DateTimeOffset now) => new()
    {
        TenantId = tenantId,
        Host = normalizedHost,
        Status = TenantDomainStatus.PendingVerification,
        VerificationToken = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(20)),
        CreatedAt = now,
    };

    public string TxtRecordName(string prefix) => $"{prefix}.{Host}";

    public string ExpectedTxtValue => $"raavi-verify={VerificationToken}";

    public void RecordVerificationSuccess(DateTimeOffset now)
    {
        EnsureNotRemoved();
        Status = TenantDomainStatus.Verified;
        VerifiedAt ??= now;
        LastCheckedAt = now;
        ConsecutiveFailures = 0;
    }

    /// <summary>A verified domain survives transient DNS failures; after <see cref="FailuresBeforeLoss"/> consecutive failures it stops resolving.</summary>
    public void RecordVerificationFailure(DateTimeOffset now)
    {
        EnsureNotRemoved();
        LastCheckedAt = now;
        ConsecutiveFailures++;
        if (Status == TenantDomainStatus.Verified && ConsecutiveFailures >= FailuresBeforeLoss)
        {
            Status = TenantDomainStatus.VerificationLost;
            IsPrimary = false;
        }
    }

    public void MakePrimary()
    {
        EnsureNotRemoved();
        if (Status != TenantDomainStatus.Verified)
        {
            throw new DomainException("domain.not_verified", "Only a verified domain can be primary.");
        }

        IsPrimary = true;
    }

    public void ClearPrimary() => IsPrimary = false;

    public void Remove(DateTimeOffset now)
    {
        EnsureNotRemoved();
        RemovedAt = now;
        IsPrimary = false;
    }

    private void EnsureNotRemoved()
    {
        if (IsRemoved)
        {
            throw new DomainException("domain.removed", "The domain has been removed.");
        }
    }
}
