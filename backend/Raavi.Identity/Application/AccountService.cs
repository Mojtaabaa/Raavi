using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Common;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Identity.Providers;
using Raavi.Identity.Security;

namespace Raavi.Identity.Application;

public sealed record LinkedIdentityDto(Guid Id, string ProviderId, LocalizedNameDto ProviderName, string? ExternalUsername, string? ExternalEmail, DateTimeOffset CreatedAt, DateTimeOffset? LastAuthenticatedAt);

public sealed record MyWorkspaceDto(string Slug, LocalizedNameDto DisplayName, Uri Url, bool IsCurrent);

/// <summary>The signed-in user's own account: linked external identities and workspaces (tenant switching).</summary>
public sealed class AccountService(
    IdentityDbContext db,
    ICurrentUser current,
    AuthenticationProviderRegistry registry,
    ITenantDirectory tenants,
    ITenantUrlResolver urls,
    ITenantContext tenant,
    SecurityEventWriter events)
{
    public async Task<IReadOnlyList<LinkedIdentityDto>> ListIdentitiesAsync(CancellationToken ct)
    {
        var rows = await db.ExternalIdentities.AsNoTracking().Where(e => e.UserId == current.UserId).OrderBy(e => e.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        return rows.Select(e =>
        {
            var def = registry.Definition(e.AuthenticationProviderId);
            return new LinkedIdentityDto(e.Id, e.AuthenticationProviderId, new LocalizedNameDto(def?.DisplayName.Fa ?? e.AuthenticationProviderId, def?.DisplayName.En ?? e.AuthenticationProviderId), e.ExternalUsername, e.ExternalEmail, e.CreatedAt, e.LastAuthenticatedAt);
        }).ToList();
    }

    /// <summary>Unlinks an identity, but never the last way to sign in (password or another identity must remain).</summary>
    public async Task<Result> UnlinkAsync(Guid identityId, CancellationToken ct)
    {
        var userId = current.UserId;
        var row = await db.ExternalIdentities.FirstOrDefaultAsync(e => e.Id == identityId && e.UserId == userId, ct).ConfigureAwait(false);
        if (row is null)
        {
            return Result.Failure(new Error("identity.not_found", "Linked account not found.", ErrorKind.NotFound));
        }

        var hasPassword = await db.Users.AnyAsync(u => u.Id == userId && u.PasswordHash != null, ct).ConfigureAwait(false);
        var others = await db.ExternalIdentities.CountAsync(e => e.UserId == userId && e.Id != identityId, ct).ConfigureAwait(false);
        if (!hasPassword && others == 0)
        {
            return Result.Failure(new Error("identity.last_sign_in_method", "You cannot remove your only way to sign in.", ErrorKind.Conflict));
        }

        db.ExternalIdentities.Remove(row);
        if (tenant.IsResolved)
        {
            events.Tenant("identity.unlinked", userId, new { provider = row.AuthenticationProviderId });
        }
        else
        {
            events.System("identity.unlinked", userId, new { provider = row.AuthenticationProviderId });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Active workspaces of the signed-in user, from the global membership index. Suspended/inactive tenants are omitted.</summary>
    public async Task<IReadOnlyList<MyWorkspaceDto>> ListWorkspacesAsync(CancellationToken ct)
    {
        var ids = await db.MembershipIndex.AsNoTracking()
            .Where(m => m.UserId == current.UserId && m.Status == MembershipStatus.Active)
            .Select(m => m.TenantId).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<MyWorkspaceDto>();
        foreach (var id in ids)
        {
            var t = await tenants.FindByIdAsync(id, ct).ConfigureAwait(false);
            var url = t is { IsActive: true } ? await urls.GetCanonicalUrlAsync(id, ct).ConfigureAwait(false) : null;
            if (t is not null && url is not null)
            {
                result.Add(new MyWorkspaceDto(t.Slug, new LocalizedNameDto(t.DisplayNameFa, t.DisplayNameEn), url, tenant.IsResolved && tenant.Tenant.TenantId == id));
            }
        }

        return result.OrderBy(w => w.DisplayName.En, StringComparer.OrdinalIgnoreCase).ToList();
    }
}
