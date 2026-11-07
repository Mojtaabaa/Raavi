using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Identity;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Identity.Security;

namespace Raavi.Identity.Application;

/// <summary>
/// <see cref="IPrincipalDirectory"/> over the Identity tables. Tenant-owned rows (members, groups, group members) are
/// read through the tenant filter and RLS; the global Users table is only joined through the tenant's member rows, so a
/// user of another tenant can never be named or found. The principal is memoized for the scope (one request).
/// </summary>
public sealed class PrincipalDirectory(IdentityDbContext db, MembershipService memberships) : IPrincipalDirectory
{
    private readonly Dictionary<Guid, PrincipalSnapshot> _memo = [];

    public async ValueTask<PrincipalSnapshot> GetPrincipalAsync(Guid userId, bool fresh = false, CancellationToken cancellationToken = default)
    {
        if (!fresh && _memo.TryGetValue(userId, out var known))
        {
            return known;
        }

        if (fresh)
        {
            await memberships.InvalidateAsync(userId, cancellationToken).ConfigureAwait(false);
        }

        var member = await memberships.GetAsync(userId, cancellationToken).ConfigureAwait(false);
        PrincipalSnapshot snapshot;
        if (!member.IsActiveMember)
        {
            snapshot = PrincipalSnapshot.None(userId);
        }
        else
        {
            // Not cached across requests: group changes must take effect on the next request (ADR-012 revocation).
            var groups = await db.GroupMembers.AsNoTracking().Where(g => g.UserId == userId).Select(g => g.GroupId).ToListAsync(cancellationToken).ConfigureAwait(false);
            snapshot = new PrincipalSnapshot(userId, true, member.IsTenantAdministrator, groups.ToHashSet());
        }

        _memo[userId] = snapshot;
        return snapshot;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetUserNamesAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = userIds.Distinct().ToList();
        return await (from m in db.Members.AsNoTracking()
                      join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                      where ids.Contains(m.UserId)
                      select new { u.Id, u.DisplayName })
            .ToDictionaryAsync(x => x.Id, x => x.DisplayName, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<Guid, PrincipalContact>> GetContactsAsync(IReadOnlyCollection<Guid> userIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(userIds);
        if (userIds.Count == 0)
        {
            return new Dictionary<Guid, PrincipalContact>();
        }

        var ids = userIds.Distinct().ToList();
        return await (from m in db.Members.AsNoTracking()
                      join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                      where ids.Contains(m.UserId) && m.Status == MembershipStatus.Active
                      select new PrincipalContact(u.Id, u.DisplayName, u.EmailVerified ? u.Email : null, u.PreferredLocale))
            .ToDictionaryAsync(x => x.UserId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyDictionary<string, DirectoryEntry>> FindUsersByVerifiedEmailAsync(IReadOnlyCollection<string> emails, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(emails);
        var wanted = emails.Where(e => !string.IsNullOrWhiteSpace(e)).Select(e => e.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, DirectoryEntry>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return result;
        }

        var rows = await (from m in db.Members.AsNoTracking()
                          join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                          where m.Status == MembershipStatus.Active && u.EmailVerified && u.Email != null && wanted.Contains(u.Email)
                          select new { u.Id, u.DisplayName, u.Email }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var r in rows)
        {
            result[r.Email!] = new DirectoryEntry(DirectoryEntryKind.User, r.Id, r.DisplayName, r.Email);
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<string, Guid>> FindGroupsByNameAsync(IReadOnlyCollection<string> names, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(names);
        var wanted = names.Where(n => !string.IsNullOrWhiteSpace(n)).Select(n => n.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0)
        {
            return result;
        }

        var rows = await db.Groups.AsNoTracking().Where(g => wanted.Contains(g.Name)).Select(g => new { g.Id, g.Name }).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var r in rows)
        {
            result[r.Name] = r.Id;
        }

        return result;
    }

    public async Task<IReadOnlyDictionary<Guid, string>> GetGroupNamesAsync(IReadOnlyCollection<Guid> groupIds, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(groupIds);
        if (groupIds.Count == 0)
        {
            return new Dictionary<Guid, string>();
        }

        var ids = groupIds.Distinct().ToList();
        return await db.Groups.AsNoTracking().Where(g => ids.Contains(g.Id)).ToDictionaryAsync(g => g.Id, g => g.Name, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DirectoryEntry>> SearchAsync(string query, int take, CancellationToken cancellationToken = default)
    {
        var q = (query ?? string.Empty).Trim();
        take = Math.Clamp(take, 1, 50);
        if (q.Length == 0)
        {
            return [];
        }

        var users = await (from m in db.Members.AsNoTracking()
                           join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                           where m.Status == MembershipStatus.Active && (u.DisplayName.Contains(q) || (u.Email != null && u.Email.Contains(q)))
                           orderby u.DisplayName
                           select new DirectoryEntry(DirectoryEntryKind.User, u.Id, u.DisplayName, u.Email))
            .Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
        var groups = await db.Groups.AsNoTracking().Where(g => g.Name.Contains(q)).OrderBy(g => g.Name)
            .Select(g => new DirectoryEntry(DirectoryEntryKind.Group, g.Id, g.Name, g.Description))
            .Take(take).ToListAsync(cancellationToken).ConfigureAwait(false);
        return [.. groups, .. users];
    }
}
