using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Identity.Security;
using Raavi.Infrastructure.Http;
using Raavi.Infrastructure.Tenancy;

namespace Raavi.Identity.Application;

/// <summary>
/// System-host identity: system administrator invitations, mandatory TOTP enrollment, MFA sign-in, and invitations of a
/// tenant's administrators (written through an explicit tenant scope).
/// </summary>
public sealed class SystemIdentityService(
    IdentityDbContext db,
    SessionService sessions,
    PasswordService passwords,
    TotpService totp,
    SecurityEventWriter events,
    IAuditActorAccessor actors,
    ITenantDirectory tenants,
    ITenantUrlResolver tenantUrls,
    ITenantScopeFactory tenantScopes,
    IClock clock,
    IOptions<RaaviAuthenticationOptions> options)
{
    private const string Issuer = "Raavi";
    private static readonly Error InvalidSetup = new("setup.invalid", "This setup link is invalid or has expired.", ErrorKind.NotFound);

    public static readonly Error FirstLoginRequired = new("auth.first_login_required", "This is the first sign-in of the account: choose a new password and enrol an authenticator app.", ErrorKind.Unauthorized);

    public async Task<Result<MeDto>> LoginAsync(HttpContext http, SystemLoginRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var now = clock.UtcNow;
        var normalized = User.Normalize(request.Email);
        var user = normalized is null ? null : await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct).ConfigureAwait(false);
        var isAdmin = user is not null && await db.SystemRoles.AnyAsync(r => r.UserId == user.Id && r.Role == SystemRole.SystemAdministrator, ct).ConfigureAwait(false);

        string? failure = null;
        if (user is null || !isAdmin)
        {
            passwords.SimulateVerify(request.Password);
            failure = user is null ? "unknown_user" : "not_system_admin";
        }
        else if (user.IsLockedOut(now))
        {
            failure = "locked_out";
        }
        else if (!passwords.Verify(user, request.Password).Valid)
        {
            failure = "bad_password";
        }
        else if (user.Status != UserStatus.Active)
        {
            failure = "user_disabled";
        }
        else if (user.MustChangePassword)
        {
            // Correct temporary password: no session, but the caller is told how to continue (not a failed attempt).
            events.System("system.first_login_required", user.Id);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<MeDto>.Failure(FirstLoginRequired);
        }
        else if (!user.MfaEnabled)
        {
            failure = "mfa_not_enrolled";
        }
        else
        {
            var step = TotpService.Verify(totp.Unprotect(user.ProtectedTotpSecret!), request.Code, now);
            if (step is null || !user.TryConsumeTotpStep(step.Value))
            {
                failure = step is null ? "bad_code" : "code_replayed";
            }
        }

        if (failure is not null)
        {
            if (user is not null && failure is "bad_password" or "bad_code")
            {
                var local = options.Value.Local;
                user.RecordFailedLogin(now, local.LockoutThreshold, TimeSpan.FromMinutes(local.LockoutMinutes));
            }

            events.System("system.login_failed", user?.Id, new { reason = failure });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<MeDto>.Failure(new Error("auth.invalid_credentials", "The e-mail, password or verification code is incorrect.", ErrorKind.Unauthorized));
        }

        user!.RecordSuccessfulLogin(now);
        events.System("system.login_succeeded", user.Id);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await sessions.IssueAsync(http, user, tenantId: null, "local", mfa: true, ct).ConfigureAwait(false);
        return Result<MeDto>.Success(AuthService.Me(user, "system", false, true, "local"));
    }

    /// <summary>
    /// First sign-in of a bootstrap account: verifies the temporary password (failures count towards the lockout like any sign-in) and returns a
    /// short-lived setup link for the normal enrolment page, where the new password is chosen and the authenticator enrolled. No session is issued.
    /// </summary>
    public async Task<Result<InvitationCreatedDto>> FirstLoginAsync(SystemFirstLoginRequest request, Uri systemBaseUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(systemBaseUrl);
        var now = clock.UtcNow;
        var normalized = User.Normalize(request.Email);
        var user = normalized is null ? null : await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == normalized, ct).ConfigureAwait(false);
        var isAdmin = user is not null && await db.SystemRoles.AnyAsync(r => r.UserId == user.Id && r.Role == SystemRole.SystemAdministrator, ct).ConfigureAwait(false);

        string? failure = null;
        if (user is null || !isAdmin)
        {
            passwords.SimulateVerify(request.Password);
            failure = user is null ? "unknown_user" : "not_system_admin";
        }
        else if (user.IsLockedOut(now))
        {
            failure = "locked_out";
        }
        else if (!passwords.Verify(user, request.Password).Valid)
        {
            failure = "bad_password";
        }
        else if (user.Status != UserStatus.Active || !user.MustChangePassword)
        {
            failure = "not_first_login";
        }

        if (failure is not null)
        {
            if (user is not null && failure is "bad_password")
            {
                var local = options.Value.Local;
                user.RecordFailedLogin(now, local.LockoutThreshold, TimeSpan.FromMinutes(local.LockoutMinutes));
            }

            events.System("system.first_login_failed", user?.Id, new { reason = failure });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<InvitationCreatedDto>.Failure(new Error("auth.invalid_credentials", "The e-mail or password is incorrect.", ErrorKind.Unauthorized));
        }

        var token = OpaqueToken.Create();
        var invitation = SystemInvitation.Create(user!.Email!, OpaqueToken.Hash(token), "bootstrap:" + user.Id.ToString("N"), now, TimeSpan.FromHours(1));
        db.SystemInvitations.Add(invitation);
        events.System("system.first_login_started", user.Id, new { invitationId = invitation.Id });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<InvitationCreatedDto>.Success(new InvitationCreatedDto(invitation.Id, invitation.Email, invitation.ExpiresAt, new Uri(systemBaseUrl, $"/system/setup#token={token}")));
    }

    public async Task<Result<InvitationCreatedDto>> InviteAdministratorAsync(string email, Uri systemBaseUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(systemBaseUrl);
        var token = OpaqueToken.Create();
        SystemInvitation invitation;
        try
        {
            invitation = SystemInvitation.Create(email, OpaqueToken.Hash(token), actors.Current.ActorId, clock.UtcNow, TimeSpan.FromHours(options.Value.InvitationLifetimeHours));
        }
        catch (DomainException ex)
        {
            return Result<InvitationCreatedDto>.Failure(new Error(ex.Code, ex.Message));
        }

        db.SystemInvitations.Add(invitation);
        events.System("system.admin_invited", null, new { invitationId = invitation.Id });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<InvitationCreatedDto>.Success(new InvitationCreatedDto(invitation.Id, invitation.Email, invitation.ExpiresAt, new Uri(systemBaseUrl, $"/system/setup#token={token}")));
    }

    public async Task<Result<InvitationInfoDto>> InspectSetupAsync(string token, CancellationToken ct)
    {
        var invitation = await FindSetup(token, ct).ConfigureAwait(false);
        if (invitation is null)
        {
            return Result<InvitationInfoDto>.Failure(InvalidSetup);
        }

        var mustChange = await db.Users.AnyAsync(u => u.NormalizedEmail == invitation.NormalizedEmail && u.MustChangePassword, ct).ConfigureAwait(false);
        return Result<InvitationInfoDto>.Success(new InvitationInfoDto(invitation.Email, invitation.ExpiresAt, false, mustChange));
    }

    /// <summary>Generates (or regenerates) the TOTP secret for enrollment. The secret is returned once to the invitee and stored protected.</summary>
    public async Task<Result<SystemSetupBeginDto>> BeginSetupAsync(SystemSetupBeginRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invitation = await FindSetup(request.Token, ct).ConfigureAwait(false);
        if (invitation is null)
        {
            return Result<SystemSetupBeginDto>.Failure(InvalidSetup);
        }

        var secret = TotpService.NewSecret();
        invitation.SetPendingTotp(totp.Protect(secret));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<SystemSetupBeginDto>.Success(new SystemSetupBeginDto(TotpService.ToBase32(secret), TotpService.OtpAuthUri(Issuer, invitation.Email, secret)));
    }

    /// <summary>
    /// Completes enrollment: verifies the first TOTP code, then creates the account (or, for an existing account of the
    /// invited address, requires its current password), stores the MFA secret and grants SystemAdministrator.
    /// </summary>
    public async Task<Result<MeDto>> CompleteSetupAsync(HttpContext http, SystemSetupCompleteRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var invitation = await FindSetup(request.Token, ct).ConfigureAwait(false);
        if (invitation?.PendingProtectedTotpSecret is null)
        {
            return Result<MeDto>.Failure(InvalidSetup);
        }

        var now = clock.UtcNow;
        var step = TotpService.Verify(totp.Unprotect(invitation.PendingProtectedTotpSecret), request.Code, now);
        if (step is null)
        {
            return Result<MeDto>.Failure(new Error("setup.bad_code", "The verification code is incorrect."));
        }

        var user = await db.Users.FirstOrDefaultAsync(u => u.NormalizedEmail == invitation.NormalizedEmail, ct).ConfigureAwait(false);
        try
        {
            if (user is null)
            {
                passwords.EnsureAcceptable(request.Password, invitation.Email);
                user = User.CreateLocal(invitation.Email, request.DisplayName, now);
                user.SetPasswordHash(passwords.Hash(user, request.Password));
                db.Users.Add(user);
            }
            else if (!passwords.Verify(user, request.Password).Valid)
            {
                events.System("system.setup_failed", user.Id, new { reason = "bad_password" });
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return Result<MeDto>.Failure(new Error("auth.invalid_credentials", "Enter the current password of your existing account.", ErrorKind.Unauthorized));
            }
            else if (user.MustChangePassword)
            {
                // Bootstrap account: the temporary password was just verified; the replacement must meet the policy and differ from it.
                var replacement = request.NewPassword ?? string.Empty;
                if (string.Equals(replacement, request.Password, StringComparison.Ordinal))
                {
                    throw new DomainException("password.unchanged", "Choose a password different from the temporary one.");
                }

                passwords.EnsureAcceptable(replacement, invitation.Email);
                user.SetPasswordHash(passwords.Hash(user, replacement));
            }
        }
        catch (DomainException ex)
        {
            return Result<MeDto>.Failure(new Error(ex.Code, ex.Message));
        }

        user.MarkEmailVerified();
        user.EnableTotp(invitation.PendingProtectedTotpSecret, step.Value);
        if (!await db.SystemRoles.AnyAsync(r => r.UserId == user.Id && r.Role == SystemRole.SystemAdministrator, ct).ConfigureAwait(false))
        {
            db.SystemRoles.Add(SystemRoleAssignment.Grant(user.Id, SystemRole.SystemAdministrator, invitation.InvitedBy, now));
        }

        invitation.Accept(now);
        events.System("system.admin_enrolled", user.Id, new { invitationId = invitation.Id });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Credentials changed (security stamp rotated): existing sessions of this user are invalid anyway; start a fresh MFA session.
        await sessions.IssueAsync(http, user, tenantId: null, "local", mfa: true, ct).ConfigureAwait(false);
        return Result<MeDto>.Success(AuthService.Me(user, "system", false, true, "local"));
    }

    public async Task<IReadOnlyList<SystemAdministratorDto>> ListAdministratorsAsync(CancellationToken ct) =>
        await db.SystemRoles.AsNoTracking().Where(r => r.Role == SystemRole.SystemAdministrator)
            .Join(db.Users.AsNoTracking(), r => r.UserId, u => u.Id, (r, u) => new SystemAdministratorDto(u.Id, u.DisplayName, u.Email, u.ProtectedTotpSecret != null, r.GrantedAt, u.LastLoginAt))
            .ToListAsync(ct).ConfigureAwait(false);

    /// <summary>Invites a user into a tenant from the system host (typically its first administrator).</summary>
    public async Task<Result<InvitationCreatedDto>> InviteIntoTenantAsync(Guid tenantId, CreateInvitationRequest request, CancellationToken ct)
    {
        var tenant = await tenants.FindByIdAsync(tenantId, ct).ConfigureAwait(false);
        var url = tenant is null ? null : await tenantUrls.GetCanonicalUrlAsync(tenantId, ct).ConfigureAwait(false);
        if (tenant is null || url is null)
        {
            return Result<InvitationCreatedDto>.Failure(new Error("tenant.not_found", "Tenant not found.", ErrorKind.NotFound));
        }

        var scope = tenantScopes.CreateScope(new ResolvedTenant(tenant.Id, tenant.Code, tenant.Slug, TenantResolutionSource.ServicePrincipal, null));
        await using (scope.ConfigureAwait(false))
        {
            return await scope.ServiceProvider.GetRequiredService<TenantIdentityAdminService>().InviteAsync(request, url, ct).ConfigureAwait(false);
        }
    }

    private async Task<SystemInvitation?> FindSetup(string? token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return null;
        }

        var hash = OpaqueToken.Hash(token);
        var invitation = await db.SystemInvitations.FirstOrDefaultAsync(i => i.TokenHash == hash, ct).ConfigureAwait(false);
        return invitation is not null && invitation.IsUsable(clock.UtcNow) ? invitation : null;
    }
}
