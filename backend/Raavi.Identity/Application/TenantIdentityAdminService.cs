using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Identity.Providers;
using Raavi.Identity.Security;
using Raavi.Infrastructure.Http;

namespace Raavi.Identity.Application;

/// <summary>
/// Tenant administration of identity (tenant host, TenantAdministrator). Every query runs in the current tenant scope
/// (EF filter + RLS), so foreign ids behave as not found. Also used by system administrators through an explicit
/// tenant scope (invitations for a tenant's first administrator).
/// </summary>
public sealed class TenantIdentityAdminService(
    IdentityDbContext db,
    MembershipService membership,
    SessionService sessions,
    SecurityEventWriter events,
    AuthenticationProviderRegistry registry,
    ITenantContext tenant,
    ICurrentUser currentUser,
    IAuditActorAccessor actors,
    IClock clock,
    IOptions<RaaviAuthenticationOptions> options,
    IEnumerable<Raavi.Application.Abstractions.Collaboration.ICollaborationRevocation> collaboration)
{
    /// <summary>Live editing connections of the user are re-checked now (ADR-011 revocation), not at the next periodic check.</summary>
    private async Task RecheckCollaborationAsync(Guid userId, CancellationToken ct)
    {
        foreach (var c in collaboration)
        {
            await c.RecheckAsync(tenant.Tenant.TenantId, userId, ct).ConfigureAwait(false);
        }
    }

    private static readonly Error MemberNotFound = new("member.not_found", "Member not found.", ErrorKind.NotFound);
    private static readonly Error GroupNotFound = new("group.not_found", "Group not found.", ErrorKind.NotFound);

    // ── Members ─────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<MemberDto>> ListMembersAsync(CancellationToken ct)
    {
        var admins = await db.TenantRoles.AsNoTracking().Where(r => r.Role == TenantRole.TenantAdministrator).Select(r => r.UserId).ToListAsync(ct).ConfigureAwait(false);
        var rows = await db.Members.AsNoTracking()
            .Join(db.Users.AsNoTracking(), m => m.UserId, u => u.Id, (m, u) => new { m, u })
            .OrderBy(x => x.u.DisplayName)
            .ToListAsync(ct).ConfigureAwait(false);
        var adminSet = admins.ToHashSet();
        return rows.Select(x => new MemberDto(x.u.Id, x.u.DisplayName, x.u.Email, x.m.Status, adminSet.Contains(x.u.Id), x.m.JoinedAt, x.u.LastLoginAt)).ToList();
    }

    public async Task<Result> SetMemberStatusAsync(Guid userId, bool active, CancellationToken ct)
    {
        var member = await db.Members.FirstOrDefaultAsync(m => m.UserId == userId, ct).ConfigureAwait(false);
        if (member is null)
        {
            return Result.Failure(MemberNotFound);
        }

        if (!active)
        {
            if (IsSelf(userId))
            {
                return Result.Failure(new Error("member.cannot_suspend_self", "You cannot suspend yourself.", ErrorKind.Conflict));
            }

            if (await IsLastActiveAdmin(userId, ct).ConfigureAwait(false))
            {
                return Result.Failure(LastAdmin);
            }

            member.Suspend();
        }
        else
        {
            member.Activate();
        }

        events.Tenant(active ? "member.activated" : "member.suspended", userId);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await membership.InvalidateAsync(userId, ct).ConfigureAwait(false);
        if (!active)
        {
            await sessions.RevokeForUserAsync(userId, tenant.Tenant.TenantId, allTenants: false, "member_suspended", ct).ConfigureAwait(false);
            await RecheckCollaborationAsync(userId, ct).ConfigureAwait(false);
        }

        return Result.Success();
    }

    public async Task<Result> RemoveMemberAsync(Guid userId, CancellationToken ct)
    {
        var member = await db.Members.FirstOrDefaultAsync(m => m.UserId == userId, ct).ConfigureAwait(false);
        if (member is null)
        {
            return Result.Failure(MemberNotFound);
        }

        if (IsSelf(userId))
        {
            return Result.Failure(new Error("member.cannot_remove_self", "You cannot remove yourself.", ErrorKind.Conflict));
        }

        if (await IsLastActiveAdmin(userId, ct).ConfigureAwait(false))
        {
            return Result.Failure(LastAdmin);
        }

        db.Members.Remove(member);
        db.TenantRoles.RemoveRange(await db.TenantRoles.Where(r => r.UserId == userId).ToListAsync(ct).ConfigureAwait(false));
        db.GroupMembers.RemoveRange(await db.GroupMembers.Where(g => g.UserId == userId).ToListAsync(ct).ConfigureAwait(false));
        events.Tenant("member.removed", userId);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await membership.InvalidateAsync(userId, ct).ConfigureAwait(false);
        await sessions.RevokeForUserAsync(userId, tenant.Tenant.TenantId, allTenants: false, "member_removed", ct).ConfigureAwait(false);
        await RecheckCollaborationAsync(userId, ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> SetRolesAsync(Guid userId, SetRolesRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!await db.Members.AnyAsync(m => m.UserId == userId, ct).ConfigureAwait(false))
        {
            return Result.Failure(MemberNotFound);
        }

        var existing = await db.TenantRoles.FirstOrDefaultAsync(r => r.UserId == userId && r.Role == TenantRole.TenantAdministrator, ct).ConfigureAwait(false);
        if (request.TenantAdministrator && existing is null)
        {
            db.TenantRoles.Add(TenantRoleAssignment.Grant(userId, TenantRole.TenantAdministrator, clock.UtcNow));
            events.Tenant("role.granted", userId, new { role = "TenantAdministrator" });
        }
        else if (!request.TenantAdministrator && existing is not null)
        {
            if (await IsLastActiveAdmin(userId, ct).ConfigureAwait(false))
            {
                return Result.Failure(LastAdmin);
            }

            db.TenantRoles.Remove(existing);
            events.Tenant("role.revoked", userId, new { role = "TenantAdministrator" });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await membership.InvalidateAsync(userId, ct).ConfigureAwait(false);
        return Result.Success();
    }

    // ── Invitations ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Creates an invitation and returns its one-time link. Identical result whether or not the address already has an account.</summary>
    public async Task<Result<InvitationCreatedDto>> InviteAsync(CreateInvitationRequest request, Uri tenantBaseUrl, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(tenantBaseUrl);
        var token = OpaqueToken.Create();
        TenantInvitation invitation;
        try
        {
            invitation = TenantInvitation.Create(request.Email, OpaqueToken.Hash(token), request.TenantAdministrator ? TenantRole.TenantAdministrator : null,
                actors.Current.ActorId, clock.UtcNow, TimeSpan.FromHours(options.Value.InvitationLifetimeHours));
        }
        catch (DomainException ex)
        {
            return Result<InvitationCreatedDto>.Failure(new Error(ex.Code, ex.Message));
        }

        // Older open invitations for the same address are superseded.
        var open = await db.Invitations.Where(i => i.NormalizedEmail == invitation.NormalizedEmail && i.AcceptedAt == null && i.RevokedAt == null).ToListAsync(ct).ConfigureAwait(false);
        open.ForEach(i => i.Revoke(clock.UtcNow));

        db.Invitations.Add(invitation);
        events.Tenant("invitation.created", null, new { invitationId = invitation.Id, admin = request.TenantAdministrator });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        // Token in the URL fragment: never sent to servers, proxies or in Referer headers.
        var url = new Uri(tenantBaseUrl, $"/invitations/accept#token={token}");
        return Result<InvitationCreatedDto>.Success(new InvitationCreatedDto(invitation.Id, invitation.Email, invitation.ExpiresAt, url));
    }

    public async Task<IReadOnlyList<InvitationDto>> ListInvitationsAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var rows = await db.Invitations.AsNoTracking().OrderByDescending(i => i.CreatedAt).Take(200).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(i => new InvitationDto(i.Id, i.Email, i.Role == TenantRole.TenantAdministrator, i.CreatedAt, i.ExpiresAt,
            i.AcceptedAt is not null ? "accepted" : i.RevokedAt is not null ? "revoked" : i.ExpiresAt <= now ? "expired" : "pending")).ToList();
    }

    public async Task<Result> RevokeInvitationAsync(Guid invitationId, CancellationToken ct)
    {
        var invitation = await db.Invitations.FirstOrDefaultAsync(i => i.Id == invitationId, ct).ConfigureAwait(false);
        if (invitation is null || invitation.AcceptedAt is not null)
        {
            return Result.Failure(new Error("invitation.not_found", "Invitation not found.", ErrorKind.NotFound));
        }

        invitation.Revoke(clock.UtcNow);
        events.Tenant("invitation.revoked", null, new { invitationId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    // ── Groups ──────────────────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<GroupDto>> ListGroupsAsync(CancellationToken ct)
    {
        var groups = await db.Groups.AsNoTracking().OrderBy(g => g.Name).ToListAsync(ct).ConfigureAwait(false);
        var counts = await db.GroupMembers.AsNoTracking().GroupBy(m => m.GroupId).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(x => x.Key, x => x.Count, ct).ConfigureAwait(false);
        return groups.Select(g => new GroupDto(g.Id, g.Name, g.Description, g.Source.ToString(), counts.GetValueOrDefault(g.Id))).ToList();
    }

    public async Task<Result<GroupDto>> CreateGroupAsync(GroupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        TenantGroup group;
        try
        {
            group = TenantGroup.Create(request.Name, request.Description, clock.UtcNow);
        }
        catch (DomainException ex)
        {
            return Result<GroupDto>.Failure(new Error(ex.Code, ex.Message));
        }

        if (await db.Groups.AnyAsync(g => g.NormalizedName == group.NormalizedName, ct).ConfigureAwait(false))
        {
            return Result<GroupDto>.Failure(new Error("group.name_taken", "A group with this name already exists.", ErrorKind.Conflict));
        }

        db.Groups.Add(group);
        events.Tenant("group.created", null, new { groupId = group.Id });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<GroupDto>.Success(new GroupDto(group.Id, group.Name, group.Description, group.Source.ToString(), 0));
    }

    public async Task<Result> UpdateGroupAsync(Guid groupId, GroupRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct).ConfigureAwait(false);
        if (group is null)
        {
            return Result.Failure(GroupNotFound);
        }

        try
        {
            group.Update(request.Name, request.Description);
        }
        catch (DomainException ex)
        {
            return Result.Failure(new Error(ex.Code, ex.Message));
        }

        if (await db.Groups.AnyAsync(g => g.Id != groupId && g.NormalizedName == group.NormalizedName, ct).ConfigureAwait(false))
        {
            return Result.Failure(new Error("group.name_taken", "A group with this name already exists.", ErrorKind.Conflict));
        }

        events.Tenant("group.updated", null, new { groupId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result> DeleteGroupAsync(Guid groupId, CancellationToken ct)
    {
        var group = await db.Groups.FirstOrDefaultAsync(g => g.Id == groupId, ct).ConfigureAwait(false);
        if (group is null)
        {
            return Result.Failure(GroupNotFound);
        }

        db.Groups.Remove(group);
        events.Tenant("group.deleted", null, new { groupId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<Result<IReadOnlyList<Guid>>> ListGroupMembersAsync(Guid groupId, CancellationToken ct)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == groupId, ct).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<Guid>>.Failure(GroupNotFound);
        }

        var ids = await db.GroupMembers.AsNoTracking().Where(m => m.GroupId == groupId).Select(m => m.UserId).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<Guid>>.Success(ids);
    }

    public async Task<Result> AddGroupMemberAsync(Guid groupId, Guid userId, CancellationToken ct)
    {
        if (!await db.Groups.AnyAsync(g => g.Id == groupId, ct).ConfigureAwait(false))
        {
            return Result.Failure(GroupNotFound);
        }

        // Only members of this tenant can join its groups (a foreign user id is "not found").
        if (!await db.Members.AnyAsync(m => m.UserId == userId, ct).ConfigureAwait(false))
        {
            return Result.Failure(MemberNotFound);
        }

        if (!await db.GroupMembers.AnyAsync(m => m.GroupId == groupId && m.UserId == userId, ct).ConfigureAwait(false))
        {
            db.GroupMembers.Add(TenantGroupMember.Add(groupId, userId, clock.UtcNow));
            events.Tenant("group.member_added", userId, new { groupId });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return Result.Success();
    }

    public async Task<Result> RemoveGroupMemberAsync(Guid groupId, Guid userId, CancellationToken ct)
    {
        var row = await db.GroupMembers.FirstOrDefaultAsync(m => m.GroupId == groupId && m.UserId == userId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(MemberNotFound);
        }

        db.GroupMembers.Remove(row);
        events.Tenant("group.member_removed", userId, new { groupId });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await RecheckCollaborationAsync(userId, ct).ConfigureAwait(false);
        return Result.Success();
    }

    // ── Provider policies ───────────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<ProviderPolicyRequest>> GetProviderPoliciesAsync(CancellationToken ct)
    {
        var rows = await db.ProviderPolicies.AsNoTracking().ToDictionaryAsync(r => r.ProviderId, ct).ConfigureAwait(false);
        return registry.All.Select(p =>
        {
            if (rows.TryGetValue(p.ProviderId, out var r))
            {
                return new ProviderPolicyRequest(r.ProviderId, r.Enabled, r.IsDefault, r.DisplayOrder, r.Provisioning, Split(r.AllowedEmailDomains), Split(r.AllowedExternalGroups));
            }

            // Implicit default: local enabled when the tenant has no explicit policy rows at all.
            var implicitLocal = rows.Count == 0 && p.ProviderId == TenantProviderPolicyService.LocalProviderId;
            return new ProviderPolicyRequest(p.ProviderId, implicitLocal, implicitLocal, 0, ProvisioningMode.ExistingUsersOnly, [], []);
        }).ToList();
    }

    public async Task<Result> SetProviderPoliciesAsync(IReadOnlyList<ProviderPolicyRequest> request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Any(r => !registry.TryGet(r.ProviderId, out _)))
        {
            return Result.Failure(new Error("provider_policy.unknown_provider", "Unknown authentication provider."));
        }

        if (!request.Any(r => r.Enabled))
        {
            return Result.Failure(new Error("provider_policy.none_enabled", "At least one sign-in method must stay enabled.", ErrorKind.Conflict));
        }

        if (request.Count(r => r.Enabled && r.IsDefault) > 1)
        {
            return Result.Failure(new Error("provider_policy.multiple_defaults", "Only one default sign-in method is allowed."));
        }

        var rows = await db.ProviderPolicies.ToDictionaryAsync(r => r.ProviderId, ct).ConfigureAwait(false);
        try
        {
            foreach (var r in request)
            {
                if (!rows.TryGetValue(r.ProviderId, out var row))
                {
                    row = TenantAuthenticationProvider.Create(r.ProviderId);
                    db.ProviderPolicies.Add(row);
                }

                row.Update(r.Enabled, r.IsDefault, r.DisplayOrder, r.Provisioning, r.AllowedEmailDomains ?? [], r.AllowedExternalGroups ?? []);
            }

            // PUT replaces the whole policy set: providers not listed are disabled (never silently left enabled).
            foreach (var stale in rows.Values.Where(row => !request.Any(r => r.ProviderId == row.ProviderId)))
            {
                stale.Update(false, false, stale.DisplayOrder, stale.Provisioning, Split(stale.AllowedEmailDomains), Split(stale.AllowedExternalGroups));
            }
        }
        catch (DomainException ex)
        {
            return Result.Failure(new Error(ex.Code, ex.Message));
        }

        events.Tenant("provider_policy.updated", null, request.Select(r => new { r.ProviderId, r.Enabled, r.IsDefault, provisioning = r.Provisioning.ToString() }));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    public async Task<IReadOnlyList<SecurityEventDto>> ListSecurityEventsAsync(int take, CancellationToken ct) =>
        await db.TenantSecurityEvents.AsNoTracking().OrderByDescending(e => e.OccurredAt).Take(Math.Clamp(take, 1, 500))
            .Select(e => new SecurityEventDto(e.OccurredAt, e.EventType, e.UserId, e.ActorId, e.IpAddress, e.Details))
            .ToListAsync(ct).ConfigureAwait(false);

    private bool IsSelf(Guid userId) => currentUser.IsAuthenticated && currentUser.UserId == userId;

    private async Task<bool> IsLastActiveAdmin(Guid userId, CancellationToken ct)
    {
        if (!await db.TenantRoles.AnyAsync(r => r.UserId == userId && r.Role == TenantRole.TenantAdministrator, ct).ConfigureAwait(false))
        {
            return false;
        }

        var activeAdmins = await db.TenantRoles.Where(r => r.Role == TenantRole.TenantAdministrator)
            .Join(db.Members.Where(m => m.Status == MembershipStatus.Active), r => r.UserId, m => m.UserId, (r, m) => r.UserId)
            .CountAsync(ct).ConfigureAwait(false);
        return activeAdmins <= 1;
    }

    private static readonly Error LastAdmin = new("member.last_administrator", "A workspace must keep at least one active administrator.", ErrorKind.Conflict);

    private static string[] Split(string csv) => csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
