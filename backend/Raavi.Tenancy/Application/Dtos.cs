using Raavi.Domain.Common;
using Raavi.Tenancy.Domain;

namespace Raavi.Tenancy.Application;

// Request DTOs deliberately have no TenantId: the tenant always comes from the route on the system host
// (system administrators) or from the resolved host (tenant users). See API_CONVENTIONS.md §2.

public sealed record CreateTenantRequest(string Code, string Slug, string DisplayNameFa, string DisplayNameEn, string? DefaultLocale, string? TimeZone, string? Calendar);

public sealed record ChangeStatusRequest(string? Reason);

public sealed record AddDomainRequest(string Host);

public sealed record UpdateSettingsRequest(string DefaultLocale, string TimeZone, string Calendar, long? StorageQuotaBytes);

public sealed record UpdateBrandingRequest(string? PrimaryColor, string? SecondaryColor, string? WelcomeMessageFa, string? WelcomeMessageEn);

public sealed record TenantDto(Guid Id, string Code, string Slug, string DisplayNameFa, string DisplayNameEn, TenantStatus Status, string? StatusReason, DateTimeOffset CreatedAt, DateTimeOffset? ActivatedAt, DateTimeOffset? SuspendedAt, DateTimeOffset? DeactivatedAt, string? Url = null)
{
    public static TenantDto From(Tenant t) => new(t.Id, t.Code, t.Slug, t.DisplayNameFa, t.DisplayNameEn, t.Status, t.StatusReason, t.CreatedAt, t.ActivatedAt, t.SuspendedAt, t.DeactivatedAt);
}

public sealed record TenantDomainDto(Guid Id, string Host, bool IsPrimary, TenantDomainStatus Status, string TxtRecordName, string TxtRecordValue, DateTimeOffset CreatedAt, DateTimeOffset? VerifiedAt, DateTimeOffset? LastCheckedAt, int ConsecutiveFailures);

public sealed record TenantSettingsDto(string DefaultLocale, string TimeZone, string Calendar, long? StorageQuotaBytes);

public sealed record TenantBrandingDto(string? PrimaryColor, string? SecondaryColor, string? WelcomeMessageFa, string? WelcomeMessageEn, int LogoVersion = 0);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Total, int Skip, int Take);

/// <summary>Public, anonymous bootstrap information for the SPA on a tenant host. Contains no ids or internal state.</summary>
public sealed record TenantPublicContextDto(string Slug, LocalizedText DisplayName, string DefaultLocale, string TimeZone, string Calendar, TenantBrandingDto Branding);

public sealed record LocalizedText(string Fa, string En);

public sealed record LogoFile(byte[] Data, string ContentType, int Version);

public sealed record HostContextDto(string Kind, TenantPublicContextDto? Tenant);

public sealed record DiscoveryResultDto(Uri Url);
