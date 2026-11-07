using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Time;
using Raavi.Identity.Domain;
using Raavi.Identity.Persistence;
using Raavi.Infrastructure.Caching;

namespace Raavi.Identity.Security;

/// <summary>Validated session data used by the authentication handler (cached briefly; revocation evicts it).</summary>
public sealed record SessionSnapshot(Guid SessionId, Guid UserId, Guid? TenantId, string ProviderId, bool MfaSatisfied, string DisplayName, string? Email, string? PreferredLocale, DateTimeOffset IdleExpiresAt, DateTimeOffset AbsoluteExpiresAt, DateTimeOffset LastSeenAt);

/// <summary>
/// Server-side sessions (AUTHENTICATION_ARCHITECTURE.md §8): opaque random cookie value, SHA-256 hash stored, bound to
/// one tenant or the system host, idle + absolute expiry, invalidated by credential changes (security stamp),
/// user disablement and explicit revocation.
/// </summary>
public sealed class SessionService(IdentityDbContext db, HybridCache cache, IClock clock, IOptions<RaaviAuthenticationOptions> options)
{
    private static readonly HybridCacheEntryOptions CacheOptions = new() { Expiration = TimeSpan.FromSeconds(30), LocalCacheExpiration = TimeSpan.FromSeconds(15) };
    private static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(1);

    private TimeSpan Idle => TimeSpan.FromMinutes(options.Value.Session.IdleTimeoutMinutes);

    private TimeSpan Absolute => TimeSpan.FromHours(options.Value.Session.AbsoluteTimeoutHours);

    public string CookieName => options.Value.Session.EffectiveCookieName;

    private bool Secure => !options.Value.Session.AllowInsecureHttp;

    /// <summary>Creates a session, persists it and writes the host-only cookie. Returns the session id.</summary>
    public async Task<Guid> IssueAsync(HttpContext http, User user, Guid? tenantId, string providerId, bool mfa, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(user);

        // Session fixation: a pre-existing session presented with this request never survives a sign-in.
        if (http.Request.Cookies.TryGetValue(CookieName, out var previous) && !string.IsNullOrEmpty(previous) && previous.Length <= 128)
        {
            await RevokeTokenAsync(previous, "replaced_by_login", ct).ConfigureAwait(false);
        }

        var token = OpaqueToken.Create();
        var session = AuthenticationSession.Issue(
            OpaqueToken.Hash(token), user.Id, tenantId, providerId, mfa, user.SecurityStamp, clock.UtcNow, Idle, Absolute,
            http.Connection.RemoteIpAddress?.ToString(), http.Request.Headers.UserAgent.ToString());
        db.Sessions.Add(session);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        http.Response.Cookies.Append(CookieName, token, new CookieOptions
        {
            HttpOnly = true,
            Secure = Secure,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true,
            Expires = session.AbsoluteExpiresAt,
            // No Domain attribute: host-only, never shared between tenant hosts (ADR-004).
        });
        return session.Id;
    }

    public async Task<SessionSnapshot?> ValidateAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token) || token.Length > 128)
        {
            return null;
        }

        var hash = OpaqueToken.Hash(token);
        var cached = await cache.GetOrCreateAsync(Key(hash), async c => new CachedSession(await LoadAsync(hash, c).ConfigureAwait(false)), CacheOptions, cancellationToken: ct).ConfigureAwait(false);
        var snapshot = cached.Session;
        var now = clock.UtcNow;
        if (snapshot is null || snapshot.IdleExpiresAt <= now || snapshot.AbsoluteExpiresAt <= now)
        {
            return null;
        }

        if (now - snapshot.LastSeenAt > TouchInterval)
        {
            await TouchAsync(hash, ct).ConfigureAwait(false);
        }

        return snapshot;
    }

    public async Task RevokeTokenAsync(string token, string reason, CancellationToken ct)
    {
        var hash = OpaqueToken.Hash(token);
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.TokenHash == hash, ct).ConfigureAwait(false);
        if (session is not null)
        {
            session.Revoke(clock.UtcNow, reason);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        await cache.RemoveAsync(Key(hash), ct).ConfigureAwait(false);
    }

    /// <summary>Revokes all live sessions of a user, optionally only those of one tenant.</summary>
    public async Task<int> RevokeForUserAsync(Guid userId, Guid? tenantId, bool allTenants, string reason, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var query = db.Sessions.Where(s => s.UserId == userId && s.RevokedAt == null && s.AbsoluteExpiresAt > now);
        if (!allTenants)
        {
            query = query.Where(s => s.TenantId == tenantId);
        }

        var sessions = await query.ToListAsync(ct).ConfigureAwait(false);
        foreach (var s in sessions)
        {
            s.Revoke(now, reason);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await cache.RemoveAsync(sessions.Select(s => Key(s.TokenHash)), ct).ConfigureAwait(false);
        return sessions.Count;
    }

    public void ClearCookie(HttpContext http)
    {
        ArgumentNullException.ThrowIfNull(http);
        http.Response.Cookies.Delete(CookieName, new CookieOptions { Secure = Secure, HttpOnly = true, SameSite = SameSiteMode.Lax, Path = "/" });
    }

    private async Task<SessionSnapshot?> LoadAsync(string hash, CancellationToken ct)
    {
        var now = clock.UtcNow;
        var row = await db.Sessions.AsNoTracking()
            .Where(s => s.TokenHash == hash && s.RevokedAt == null && s.IdleExpiresAt > now && s.AbsoluteExpiresAt > now)
            .Join(db.Users.AsNoTracking(), s => s.UserId, u => u.Id, (s, u) => new { s, u })
            .FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (row is null || row.u.Status != UserStatus.Active || row.u.SecurityStamp != row.s.SecurityStamp)
        {
            return null;
        }

        return new SessionSnapshot(row.s.Id, row.u.Id, row.s.TenantId, row.s.ProviderId, row.s.MfaSatisfied, row.u.DisplayName, row.u.Email, row.u.PreferredLocale, row.s.IdleExpiresAt, row.s.AbsoluteExpiresAt, row.s.LastSeenAt);
    }

    private async Task TouchAsync(string hash, CancellationToken ct)
    {
        var session = await db.Sessions.FirstOrDefaultAsync(s => s.TokenHash == hash, ct).ConfigureAwait(false);
        if (session is null)
        {
            return;
        }

        session.Touch(clock.UtcNow, Idle);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await cache.RemoveAsync(Key(hash), ct).ConfigureAwait(false);
    }

    private static string Key(string hash) => CacheKeys.System($"session:{hash}");

    public sealed record CachedSession(SessionSnapshot? Session);
}
