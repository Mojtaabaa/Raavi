using Microsoft.EntityFrameworkCore;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Identity.Security;

namespace Raavi.Identity.Application;

public sealed record SystemUserDto(Guid Id, string DisplayName, string? Email, UserStatus Status, int Workspaces, bool IsSystemAdministrator, DateTimeOffset CreatedAt, DateTimeOffset? LastLoginAt);

public sealed record DeleteUsersRequest(IReadOnlyList<Guid>? Ids, bool AllWithoutWorkspace);

public sealed record DeleteUsersResult(int Deleted, int Skipped);

/// <summary>
/// User report for the installation (system host, ADR-051): every account with the number of workspaces it belongs to, and the clean-up of accounts
/// that belong to none (left over after workspaces were deleted or members removed). Only accounts with <b>no membership and no system role</b> can
/// be deleted — checked again at deletion time, whatever the caller sent.
/// </summary>
public sealed class SystemUserService(IdentityDbContext db, SecurityEventWriter events)
{
    public const int MaxBatch = 1000;

    public async Task<PagedUsers> ListAsync(string? search, bool onlyWithoutWorkspace, int skip, int take, CancellationToken ct)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 200);
        var query = db.Users.AsNoTracking().Select(u => new { User = u, Count = db.MembershipIndex.Count(m => m.UserId == u.Id) });
        if (onlyWithoutWorkspace)
        {
            query = query.Where(x => x.Count == 0);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var s = search.Trim();
            query = query.Where(x => x.User.DisplayName.Contains(s) || (x.User.Email != null && x.User.Email.Contains(s)));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var page = await query.OrderBy(x => x.User.DisplayName).ThenBy(x => x.User.Id).Skip(skip).Take(take).ToListAsync(ct).ConfigureAwait(false);
        var ids = page.Select(x => x.User.Id).ToList();
        var admins = await db.SystemRoles.AsNoTracking().Where(r => ids.Contains(r.UserId)).Select(r => r.UserId).Distinct().ToListAsync(ct).ConfigureAwait(false);
        var withoutWorkspace = await db.Users.AsNoTracking().CountAsync(u => !db.MembershipIndex.Any(m => m.UserId == u.Id) && !db.SystemRoles.Any(r => r.UserId == u.Id), ct).ConfigureAwait(false);
        var items = page.Select(x => new SystemUserDto(x.User.Id, x.User.DisplayName, x.User.Email, x.User.Status, x.Count, admins.Contains(x.User.Id), x.User.CreatedAt, x.User.LastLoginAt)).ToList();
        return new PagedUsers(items, total, skip, take, withoutWorkspace);
    }

    public async Task<Result<DeleteUsersResult>> DeleteAsync(DeleteUsersRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!request.AllWithoutWorkspace && (request.Ids is null || request.Ids.Count == 0))
        {
            return Result<DeleteUsersResult>.Failure(new Error("users.none_selected", "Select at least one account.", ErrorKind.Validation));
        }

        var candidates = db.Users.Where(u => !db.MembershipIndex.Any(m => m.UserId == u.Id) && !db.SystemRoles.Any(r => r.UserId == u.Id));
        List<Guid> ids;
        if (request.AllWithoutWorkspace)
        {
            ids = await candidates.OrderBy(u => u.CreatedAt).Select(u => u.Id).Take(MaxBatch).ToListAsync(ct).ConfigureAwait(false);
        }
        else
        {
            var asked = request.Ids!.Distinct().Take(MaxBatch).ToList();
            ids = await candidates.Where(u => asked.Contains(u.Id)).Select(u => u.Id).ToListAsync(ct).ConfigureAwait(false);
        }

        var requested = request.AllWithoutWorkspace ? ids.Count : request.Ids!.Distinct().Take(MaxBatch).Count();
        var deleted = 0;
        foreach (var id in ids)
        {
            try
            {
                await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
                await db.Sessions.Where(s => s.UserId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await db.ExternalIdentities.Where(e => e.UserId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                await db.Avatars.Where(a => a.UserId == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                var removed = await db.Users.Where(u => u.Id == id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
                if (removed > 0)
                {
                    events.System("system.user_deleted", id);
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    deleted++;
                }

                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear(); // something still refers to the account: leave it, count it as skipped
            }
            catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number == 547)
            {
                db.ChangeTracker.Clear();
            }
        }

        return Result<DeleteUsersResult>.Success(new DeleteUsersResult(deleted, requested - deleted));
    }
}

public sealed record PagedUsers(IReadOnlyList<SystemUserDto> Items, int Total, int Skip, int Take, int WithoutWorkspace);
