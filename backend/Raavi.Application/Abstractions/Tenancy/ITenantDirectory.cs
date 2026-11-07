using Raavi.Domain.Common;

namespace Raavi.Application.Abstractions.Tenancy;

/// <summary>Read-only, cached view of tenants used by resolution, jobs and discovery. Implemented by the Tenancy module.</summary>
public interface ITenantDirectory
{
    ValueTask<TenantSnapshot?> FindByIdAsync(Guid tenantId, CancellationToken cancellationToken = default);

    /// <summary>Only <b>verified</b>, non-removed custom domains resolve.</summary>
    ValueTask<TenantSnapshot?> FindByVerifiedDomainAsync(string normalizedHost, CancellationToken cancellationToken = default);

    ValueTask<TenantSnapshot?> FindBySlugAsync(string slug, CancellationToken cancellationToken = default);

    ValueTask<TenantSnapshot?> FindByCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Drops every cached entry of the tenant (id, slug, code, all hosts) plus the given extra hosts.</summary>
    ValueTask InvalidateAsync(Guid tenantId, IEnumerable<string>? extraHosts = null, CancellationToken cancellationToken = default);
}

public sealed record TenantSnapshot(Guid Id, string Code, string Slug, TenantStatus Status, string DisplayNameFa, string DisplayNameEn)
{
    public bool IsActive => Status == TenantStatus.Active;
}
