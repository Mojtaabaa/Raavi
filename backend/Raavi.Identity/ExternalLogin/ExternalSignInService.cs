using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Identity.Application;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Identity.Providers;
using Raavi.Identity.Security;
using DomainExternalIdentity = Raavi.Identity.Domain.ExternalIdentity;
using ExternalIdentity = Raavi.Identity.Providers.ExternalIdentity;

namespace Raavi.Identity.ExternalLogin;

/// <summary>
/// Turns a verified external identity into a tenant session (AUTHENTICATION_ARCHITECTURE.md §6–§7):
///   identity key (provider, subject) → user; never by e-mail. Unknown identities are provisioned according to the
///   tenant's policy for that provider; e-mail collisions are never merged (the user must link from their account);
///   group and domain gates apply on every sign-in; external groups listed in the policy are mirrored and synchronized.
/// </summary>
public sealed class ExternalSignInService(
    IdentityDbContext db,
    ITenantProviderPolicyService policies,
    MembershipService membership,
    SessionService sessions,
    SecurityEventWriter events,
    ITenantContext tenant,
    IClock clock)
{
    public async Task<Result<MeDto>> SignInAsync(HttpContext http, ExternalIdentity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var tenantId = tenant.Tenant.TenantId;
        var now = clock.UtcNow;
        var policy = (await policies.GetEnabledProvidersAsync(tenantId, ct).ConfigureAwait(false)).FirstOrDefault(p => p.ProviderId == identity.ProviderId);
        if (policy is null)
        {
            return await Fail("auth.provider_disabled", null, identity, ct).ConfigureAwait(false);
        }

        var gate = CheckGates(policy, identity);
        if (gate is not null)
        {
            return await Fail(gate, null, identity, ct).ConfigureAwait(false);
        }

        var link = await db.ExternalIdentities.FirstOrDefaultAsync(e => e.AuthenticationProviderId == identity.ProviderId && e.ExternalSubjectId == identity.SubjectId, ct).ConfigureAwait(false);
        User? user;
        if (link is not null)
        {
            user = await db.Users.FirstAsync(u => u.Id == link.UserId, ct).ConfigureAwait(false);
            link.Touch(identity.Username, identity.Email, now);
        }
        else
        {
            if (policy.Provisioning == ProvisioningMode.ExistingUsersOnly)
            {
                return await Fail("auth.not_provisioned", null, identity, ct).ConfigureAwait(false);
            }

            // Never attach an external identity to an existing account by e-mail — not even a verified one.
            var normalized = User.Normalize(identity.Email);
            if (normalized is not null && await db.Users.AnyAsync(u => u.NormalizedEmail == normalized, ct).ConfigureAwait(false))
            {
                return await Fail("auth.account_link_required", null, identity, ct).ConfigureAwait(false);
            }

            user = User.CreateExternal(identity.Email, identity.EmailVerified, identity.DisplayName ?? identity.Username ?? string.Empty, now);
            db.Users.Add(user);
            db.ExternalIdentities.Add(DomainExternalIdentity.Link(user.Id, identity.ProviderId, identity.SubjectId, identity.Username, identity.Email, now));
            events.Tenant("identity.provisioned", user.Id, new { provider = identity.ProviderId });
        }

        if (user.Status != UserStatus.Active)
        {
            return await Fail("auth.user_disabled", user.Id, identity, ct).ConfigureAwait(false);
        }

        var member = await db.Members.FirstOrDefaultAsync(m => m.UserId == user.Id, ct).ConfigureAwait(false);
        if (member is null)
        {
            switch (policy.Provisioning)
            {
                case ProvisioningMode.AutomaticCreation:
                    member = TenantMember.Join(user.Id, now);
                    db.Members.Add(member);
                    events.Tenant("member.provisioned", user.Id, new { provider = identity.ProviderId });
                    break;
                case ProvisioningMode.AdministratorApproval:
                    db.Members.Add(TenantMember.JoinPendingApproval(user.Id, now));
                    events.Tenant("member.pending_approval", user.Id, new { provider = identity.ProviderId });
                    return await Fail("auth.pending_approval", user.Id, identity, ct).ConfigureAwait(false);
                default:
                    return await Fail("auth.not_member", user.Id, identity, ct).ConfigureAwait(false);
            }
        }
        else if (member.Status != MembershipStatus.Active)
        {
            return await Fail(member.Status == MembershipStatus.PendingApproval ? "auth.pending_approval" : "auth.not_member", user.Id, identity, ct).ConfigureAwait(false);
        }

        await SyncGroupsAsync(user.Id, identity, policy, now, ct).ConfigureAwait(false);
        user.RecordSuccessfulLogin(now);
        events.Tenant("auth.login_succeeded", user.Id, new { provider = identity.ProviderId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await membership.InvalidateAsync(user.Id, ct).ConfigureAwait(false);

        var snapshot = await membership.GetAsync(user.Id, ct).ConfigureAwait(false);
        await sessions.IssueAsync(http, user, tenantId, identity.ProviderId, mfa: false, ct).ConfigureAwait(false);
        return Result<MeDto>.Success(AuthService.Me(user, "tenant", snapshot.IsTenantAdministrator, false, identity.ProviderId));
    }

    /// <summary>Links an external identity to the signed-in user (proven by both the session and the provider login).</summary>
    public async Task<Result> LinkAsync(Guid userId, ExternalIdentity identity, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(identity);
        var existing = await db.ExternalIdentities.FirstOrDefaultAsync(e => e.AuthenticationProviderId == identity.ProviderId && e.ExternalSubjectId == identity.SubjectId, ct).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.UserId == userId)
            {
                return Result.Success();
            }

            events.Tenant("identity.link_rejected", userId, new { provider = identity.ProviderId, reason = "in_use" });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result.Failure(new Error("auth.identity_in_use", "This external account is already linked to another Raavi account.", ErrorKind.Conflict));
        }

        db.ExternalIdentities.Add(DomainExternalIdentity.Link(userId, identity.ProviderId, identity.SubjectId, identity.Username, identity.Email, clock.UtcNow));
        events.Tenant("identity.linked", userId, new { provider = identity.ProviderId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Domain and group restrictions of the tenant policy. Domain restrictions only trust provider-verified e-mail.</summary>
    private static string? CheckGates(TenantProviderPolicy policy, ExternalIdentity identity)
    {
        if (policy.AllowedEmailDomains.Count > 0)
        {
            if (!identity.EmailVerified || identity.Email is null)
            {
                return "auth.email_not_verified";
            }

            var domain = identity.Email[(identity.Email.LastIndexOf('@') + 1)..].ToLowerInvariant();
            if (!policy.AllowedEmailDomains.Contains(domain, StringComparer.OrdinalIgnoreCase))
            {
                return "auth.domain_not_allowed";
            }
        }

        if (policy.AllowedExternalGroups.Count > 0 && !identity.Groups.Any(g => policy.AllowedExternalGroups.Contains(g, StringComparer.OrdinalIgnoreCase)))
        {
            return "auth.group_not_allowed";
        }

        return null;
    }

    /// <summary>Mirrors the policy's allowed external groups as tenant groups and synchronizes this user's membership.</summary>
    private async Task SyncGroupsAsync(Guid userId, ExternalIdentity identity, TenantProviderPolicy policy, DateTimeOffset now, CancellationToken ct)
    {
        if (policy.AllowedExternalGroups.Count == 0)
        {
            return;
        }

        var prefix = identity.ProviderId + ":";
        var mirrored = await db.Groups.Where(g => g.Source == GroupSource.External && g.ExternalKey != null && g.ExternalKey.StartsWith(prefix)).ToListAsync(ct).ConfigureAwait(false);
        var current = await db.GroupMembers.Where(m => m.UserId == userId).ToListAsync(ct).ConfigureAwait(false);
        var userGroups = identity.Groups.Where(g => policy.AllowedExternalGroups.Contains(g, StringComparer.OrdinalIgnoreCase)).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var name in policy.AllowedExternalGroups)
        {
            var key = prefix + name.ToLowerInvariant();
            var group = mirrored.FirstOrDefault(g => g.ExternalKey == key);
            if (group is null)
            {
                group = TenantGroup.CreateExternal($"{name} ({identity.ProviderId})", key, now);
                db.Groups.Add(group);
                mirrored.Add(group);
            }

            var isMember = current.Any(m => m.GroupId == group.Id);
            if (userGroups.Contains(name) && !isMember)
            {
                db.GroupMembers.Add(TenantGroupMember.Add(group.Id, userId, now));
            }
            else if (!userGroups.Contains(name) && isMember)
            {
                db.GroupMembers.RemoveRange(current.Where(m => m.GroupId == group.Id));
            }
        }
    }

    private async Task<Result<MeDto>> Fail(string reason, Guid? userId, ExternalIdentity identity, CancellationToken ct)
    {
        events.Tenant("auth.login_failed", userId, new { reason, provider = identity.ProviderId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<MeDto>.Failure(new Error(reason, "Sign-in through this provider is not possible for this workspace.", ErrorKind.Unauthorized));
    }
}
