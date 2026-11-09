using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Identity.Providers;
using Raavi.Identity.Security;

namespace Raavi.Identity.Application;

/// <summary>
/// Tenant-host sign-in and invitation acceptance. Authentication (provider) → membership (tenant) → session.
/// Every failure returns the same generic error to the client; the precise reason is a tenant security event.
/// </summary>
public sealed class AuthService(
    IdentityDbContext db,
    AuthenticationProviderRegistry registry,
    ITenantProviderPolicyService policies,
    MembershipService membership,
    SessionService sessions,
    PasswordService passwords,
    SecurityEventWriter events,
    ITenantContext tenant,
    IClock clock,
    IOptions<RaaviAuthenticationOptions> options,
    Raavi.Identity.ExternalLogin.ExternalSignInService externalSignIn)
{
    public static readonly Error InvalidCredentials = new("auth.invalid_credentials", "The e-mail or password is incorrect, or you do not have access to this workspace.", ErrorKind.Unauthorized);
    private static readonly Error InvalidInvitation = new("invitation.invalid", "This invitation link is invalid or has expired.", ErrorKind.NotFound);

    public async Task<IReadOnlyList<ProviderSummaryDto>> ListProvidersAsync(CancellationToken ct)
    {
        var enabled = await policies.GetEnabledProvidersAsync(tenant.Tenant.TenantId, ct).ConfigureAwait(false);
        return enabled.Select(p =>
        {
            var def = registry.Definition(p.ProviderId)!;
            registry.TryGet(p.ProviderId, out var provider);
            var flow = provider!.SupportedFlows.HasFlag(AuthenticationFlow.Credentials) ? "credentials" : "redirect";
            return new ProviderSummaryDto(def.Id, def.Type.ToString(), flow, new LocalizedNameDto(def.DisplayName.Fa, def.DisplayName.En), p.IsDefault);
        }).ToList();
    }

    public Task<Result<MeDto>> LoginLocalAsync(HttpContext http, LocalLoginRequest request, CancellationToken ct) =>
        LoginWithCredentialsAsync(http, null, request, ct);

    /// <summary>
    /// Credential sign-in through a credentials provider enabled for the tenant: local (default when <paramref name="providerId"/>
    /// is null) or LDAP/AD. Wrong credentials of any kind produce the same generic error. Directory users then go through
    /// the external sign-in pipeline (identity mapping, provisioning policy, gates, group sync).
    /// </summary>
    public async Task<Result<MeDto>> LoginWithCredentialsAsync(HttpContext http, string? providerId, LocalLoginRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var tenantId = tenant.Tenant.TenantId;
        var enabled = await policies.GetEnabledProvidersAsync(tenantId, ct).ConfigureAwait(false);
        var policy = providerId is null
            ? enabled.FirstOrDefault(p => registry.Definition(p.ProviderId)?.Type == AuthenticationProviderType.Local)
            : enabled.FirstOrDefault(p => p.ProviderId == providerId);
        if (policy is null || !registry.TryGet(policy.ProviderId, out var provider) || provider is null || !provider.SupportedFlows.HasFlag(AuthenticationFlow.Credentials))
        {
            return await Fail("auth.provider_disabled", null, ct).ConfigureAwait(false);
        }

        var result = await provider.AuthenticateWithCredentialsAsync(new CredentialLogin(request.Email ?? string.Empty, request.Password ?? string.Empty, tenantId), ct).ConfigureAwait(false);
        if (result.Succeeded && provider.Type != AuthenticationProviderType.Local)
        {
            var external = await externalSignIn.SignInAsync(http, result.Identity!, ct).ConfigureAwait(false);
            // Once the password was proven, the specific outcome (pending approval, not provisioned…) may be shown to the user.
            return external;
        }

        if (!result.Succeeded)
        {
            var known = await db.Users.AsNoTracking().Where(u => u.NormalizedEmail == User.Normalize(request.Email)).Select(u => (Guid?)u.Id).FirstOrDefaultAsync(ct).ConfigureAwait(false);
            return await Fail(result.FailureCode ?? "auth.failed", known, ct).ConfigureAwait(false);
        }

        var userId = Guid.Parse(result.Identity!.SubjectId);
        var member = await membership.GetAsync(userId, ct).ConfigureAwait(false);
        if (!member.IsActiveMember)
        {
            return await Fail("auth.not_member", userId, ct).ConfigureAwait(false);
        }

        var user = await db.Users.FirstAsync(u => u.Id == userId, ct).ConfigureAwait(false);
        events.Tenant("auth.login_succeeded", userId, new { provider = policy.ProviderId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await sessions.IssueAsync(http, user, tenantId, policy.ProviderId, mfa: false, ct).ConfigureAwait(false);
        return Result<MeDto>.Success(Me(user, "tenant", member.IsTenantAdministrator, false, policy.ProviderId));
    }

    public async Task<Result<InvitationInfoDto>> InspectInvitationAsync(string token, CancellationToken ct)
    {
        var invitation = await FindInvitation(token, ct).ConfigureAwait(false);
        return invitation is null
            ? Result<InvitationInfoDto>.Failure(InvalidInvitation)
            : Result<InvitationInfoDto>.Success(new InvitationInfoDto(invitation.Email, invitation.ExpiresAt, invitation.Role == TenantRole.TenantAdministrator));
    }

    /// <summary>
    /// Mode "new": creates the account for the invited address (fails if the address already has an account).
    /// Mode "existing": the invitee proves the existing account with its password.
    /// Either way the invited e-mail is now verified, the membership becomes active and a session is issued.
    /// </summary>
    public async Task<Result<MeDto>> AcceptInvitationAsync(HttpContext http, AcceptInvitationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invitation = await FindInvitation(request.Token, ct).ConfigureAwait(false);
        if (invitation is null)
        {
            return Result<MeDto>.Failure(InvalidInvitation);
        }

        var now = clock.UtcNow;
        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == invitation.NormalizedEmail, ct).ConfigureAwait(false);
        if (request.Mode == "new")
        {
            if (user is not null)
            {
                return Result<MeDto>.Failure(new Error("invitation.account_exists", "An account already exists for this address. Sign in with it to accept.", ErrorKind.Conflict));
            }

            try
            {
                passwords.EnsureAcceptable(request.Password, invitation.Email);
                user = User.CreateLocal(invitation.Email, request.DisplayName ?? string.Empty, now);
            }
            catch (DomainException ex)
            {
                return Result<MeDto>.Failure(new Error(ex.Code, ex.Message));
            }

            user.SetPasswordHash(passwords.Hash(user, request.Password));
            user.RecordSuccessfulLogin(now);
            db.Users.Add(user);
        }
        else if (request.Mode == "existing")
        {
            if (user is null || user.IsLockedOut(now) || !passwords.Verify(user, request.Password).Valid)
            {
                if (user is null)
                {
                    passwords.SimulateVerify(request.Password);
                }
                else if (!user.IsLockedOut(now))
                {
                    var local = options.Value.Local;
                    user.RecordFailedLogin(now, local.LockoutThreshold, TimeSpan.FromMinutes(local.LockoutMinutes));
                }

                events.Tenant("invitation.accept_failed", user?.Id, new { reason = "credentials" });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Result<MeDto>.Failure(InvalidCredentials);
            }

            user.RecordSuccessfulLogin(now);
        }
        else
        {
            return Result<MeDto>.Failure(new Error("invitation.mode_invalid", "Mode must be 'new' or 'existing'."));
        }

        user.MarkEmailVerified();
        var existingMember = await db.Members.FirstOrDefaultAsync(m => m.UserId == user.Id, ct).ConfigureAwait(false);
        if (existingMember is null)
        {
            db.Members.Add(TenantMember.Join(user.Id, now));
        }
        else
        {
            existingMember.Activate();
        }

        if (invitation.Role == TenantRole.TenantAdministrator
            && !await db.TenantRoles.AnyAsync(r => r.UserId == user.Id && r.Role == TenantRole.TenantAdministrator, ct).ConfigureAwait(false))
        {
            db.TenantRoles.Add(TenantRoleAssignment.Grant(user.Id, TenantRole.TenantAdministrator, now));
        }

        invitation.Accept(now);
        events.Tenant("invitation.accepted", user.Id, new { invitationId = invitation.Id, mode = request.Mode });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await membership.InvalidateAsync(user.Id, ct).ConfigureAwait(false);

        await sessions.IssueAsync(http, user, tenant.Tenant.TenantId, TenantProviderPolicyService.LocalProviderId, mfa: false, ct).ConfigureAwait(false);
        return Result<MeDto>.Success(Me(user, "tenant", invitation.Role == TenantRole.TenantAdministrator, false, TenantProviderPolicyService.LocalProviderId));
    }

    private async Task<TenantInvitation?> FindInvitation(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return null;
        }

        var hash = OpaqueToken.Hash(token);
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.TokenHash == hash, ct).ConfigureAwait(false);
        return invitation is not null && invitation.IsUsable(clock.UtcNow) ? invitation : null;
    }

    private async Task<Result<MeDto>> Fail(string reason, Guid? userId, CancellationToken ct)
    {
        events.Tenant("auth.login_failed", userId, new { reason });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<MeDto>.Failure(InvalidCredentials);
    }

    public static MeDto Me(User user, string hostKind, bool tenantAdmin, bool systemAdmin, string provider) =>
        new(user.Id, user.DisplayName, user.Email, user.PreferredLocale, hostKind, tenantAdmin, systemAdmin, provider, user.Theme);
}
