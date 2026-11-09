using Raavi.Identity.Domain;
using Raavi.Identity.Providers;

namespace Raavi.Identity.Application;

// No DTO carries a TenantId: the tenant is always the resolved host (or the route on the system host).

public sealed record LocalLoginRequest(string Email, string Password);

public sealed record BeginLoginRequest(string? ReturnPath);

public sealed record SystemLoginRequest(string Email, string Password, string Code);

public sealed record AcceptInvitationRequest(string Token, string Mode, string Password, string? DisplayName);

public sealed record SystemSetupBeginRequest(string Token);

/// <param name="NewPassword">Only for a bootstrap account: the replacement for the temporary password given in <paramref name="Password"/>.</param>
public sealed record SystemSetupCompleteRequest(string Token, string DisplayName, string Password, string Code, string? NewPassword = null);

public sealed record SystemFirstLoginRequest(string Email, string Password);

public sealed record CreateInvitationRequest(string Email, bool TenantAdministrator);

public sealed record SetRolesRequest(bool TenantAdministrator);

public sealed record GroupRequest(string Name, string? Description);

public sealed record PreferencesRequest(string? Locale);

public sealed record ProviderPolicyRequest(string ProviderId, bool Enabled, bool IsDefault, int DisplayOrder, ProvisioningMode Provisioning, IReadOnlyList<string>? AllowedEmailDomains, IReadOnlyList<string>? AllowedExternalGroups);

public sealed record ProviderSummaryDto(string Id, string Type, string Flow, LocalizedNameDto DisplayName, bool IsDefault);

public sealed record LocalizedNameDto(string Fa, string En);

public sealed record MeDto(Guid Id, string DisplayName, string? Email, string? PreferredLocale, string HostKind, bool IsTenantAdministrator, bool IsSystemAdministrator, string Provider);

public sealed record MemberDto(Guid UserId, string DisplayName, string? Email, MembershipStatus Status, bool IsTenantAdministrator, DateTimeOffset JoinedAt, DateTimeOffset? LastLoginAt);

public sealed record InvitationDto(Guid Id, string Email, bool TenantAdministrator, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, string Status);

/// <summary>The link is shown exactly once to the inviting administrator (no e-mail delivery yet — SMTP arrives with notifications).</summary>
public sealed record InvitationCreatedDto(Guid Id, string Email, DateTimeOffset ExpiresAt, Uri AcceptUrl);

public sealed record InvitationInfoDto(string Email, DateTimeOffset ExpiresAt, bool TenantAdministrator, bool PasswordChangeRequired = false);

public sealed record GroupDto(Guid Id, string Name, string? Description, string Source, int MemberCount);

public sealed record SecurityEventDto(DateTimeOffset OccurredAt, string EventType, Guid? UserId, string ActorId, string? IpAddress, string? Details);

public sealed record SystemSetupBeginDto(string Secret, string OtpAuthUri);

public sealed record SystemAdministratorDto(Guid UserId, string DisplayName, string? Email, bool MfaEnabled, DateTimeOffset GrantedAt, DateTimeOffset? LastLoginAt);
