using System.Collections.Concurrent;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Http;
using Raavi.Infrastructure.Tenancy;
using Raavi.Wiki.Persistence;

namespace Raavi.Wiki.Api;

public sealed class NotificationPushOptions
{
    public const string Section = "Raavi:Notifications";

    public int PushIntervalMilliseconds { get; set; } = 2000;

    /// <summary>How often open connections are re-validated (membership, tenant status, sign-in session).</summary>
    public int RevalidateSeconds { get; set; } = 60;
}

/// <summary>Open event connections of this API node, keyed by connection id.</summary>
public sealed class EventConnections
{
    public sealed record Entry(string ConnectionId, ResolvedTenant Tenant, Guid UserId, Guid? SessionId, HubCallerContext Context);

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public void Add(Entry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        _entries[entry.ConnectionId] = entry;
    }

    public void Remove(string connectionId) => _entries.TryRemove(connectionId, out _);

    public IReadOnlyList<Entry> Snapshot() => [.. _entries.Values];

    public int Count => _entries.Count;
}

/// <summary>
/// Push channel for in-app events (<c>/hubs/events</c>). Clients receive <c>notifications</c> — a content-free "your
/// inbox changed" signal — and then fetch the inbox through the REST API, which applies the normal permission checks.
/// Tenant host only, signed-in members only; cross-site connections are refused by an Origin check (cookies are the
/// credential). Each user's connections are in the group <c>t:{tenant}:u:{user}</c>.
/// </summary>
[Authorize(Policy = RaaviPolicies.AuthenticatedUser)]
public sealed class EventsHub(EventConnections connections) : Hub
{
    public const string Path = "/hubs/events";
    public const string NotificationsMethod = "notifications";

    public static string Group(Guid tenantId, Guid userId) => $"t:{tenantId:N}:u:{userId:N}";

    public override async Task OnConnectedAsync()
    {
        var http = Context.GetHttpContext();
        var tenant = http?.RequestServices.GetService<ITenantContext>();
        var user = http?.RequestServices.GetService<ICurrentUser>();
        if (http is null || tenant is not { IsResolved: true } || user is not { IsAuthenticated: true } || !SameOrigin(http.Request))
        {
            Context.Abort();
            return;
        }

        var principal = await http.RequestServices.GetRequiredService<IPrincipalDirectory>().GetPrincipalAsync(user.UserId, cancellationToken: Context.ConnectionAborted).ConfigureAwait(false);
        if (!principal.IsActiveMember)
        {
            Context.Abort();
            return;
        }

        var sid = Guid.TryParse(Context.User?.FindFirstValue("raavi:sid"), out var s) ? s : (Guid?)null;
        connections.Add(new EventConnections.Entry(Context.ConnectionId, tenant.Tenant, user.UserId, sid, Context));
        await Groups.AddToGroupAsync(Context.ConnectionId, Group(tenant.Tenant.TenantId, user.UserId), Context.ConnectionAborted).ConfigureAwait(false);
        await Clients.Caller.SendAsync(NotificationsMethod, Context.ConnectionAborted).ConfigureAwait(false);
        await base.OnConnectedAsync().ConfigureAwait(false);
    }

    public override Task OnDisconnectedAsync(Exception? exception)
    {
        connections.Remove(Context.ConnectionId);
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>Browsers always send Origin on WebSocket and cross-origin requests; it must name the host that served the request.</summary>
    internal static bool SameOrigin(HttpRequest request)
    {
        var origin = request.Headers.Origin.ToString();
        if (origin.Length == 0)
        {
            return true; // non-browser client (no ambient cookies were sent cross-site)
        }

        return Uri.TryCreate(origin, UriKind.Absolute, out var o)
            && string.Equals(o.Authority, request.Host.Value, StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Pushes inbox changes to connected users. Every interval, for each tenant with open connections, it reads the unread
/// count and newest unread time of those users (one indexed query per tenant, in that tenant's scope) and signals the
/// users whose figures changed. This works whichever host created the notification and needs no backplane: each API
/// node serves its own connections. Connections are re-validated periodically and closed when the member, tenant or
/// sign-in session is no longer active.
/// </summary>
public sealed partial class NotificationPushService(
    EventConnections connections,
    IHubContext<EventsHub> hub,
    ITenantScopeFactory scopes,
    IConfiguration configuration,
    ILogger<NotificationPushService> logger) : BackgroundService
{
    private readonly Dictionary<(Guid Tenant, Guid User), (int Count, DateTimeOffset? Latest)> _last = [];
    private DateTimeOffset _lastRevalidation = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var options = configuration.GetSection(NotificationPushOptions.Section).Get<NotificationPushOptions>() ?? new NotificationPushOptions();
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(options, stoppingToken).ConfigureAwait(false);
            }
#pragma warning disable CA1031 // keep pushing after transient failures
            catch (Exception ex) when (ex is not OperationCanceledException)
#pragma warning restore CA1031
            {
                LogPushFailed(logger, ex);
            }

            await Task.Delay(Math.Max(250, options.PushIntervalMilliseconds), stoppingToken).ConfigureAwait(false);
        }
    }

    internal async Task TickAsync(NotificationPushOptions options, CancellationToken ct)
    {
        var open = connections.Snapshot();
        var revalidate = DateTimeOffset.UtcNow - _lastRevalidation >= TimeSpan.FromSeconds(options.RevalidateSeconds);
        if (revalidate)
        {
            _lastRevalidation = DateTimeOffset.UtcNow;
        }

        var seen = new HashSet<(Guid, Guid)>();
        foreach (var tenantGroup in open.GroupBy(e => e.Tenant.TenantId))
        {
            var scope = scopes.CreateScope(tenantGroup.First().Tenant with { ResolvedBy = TenantResolutionSource.BackgroundJob });
            await using (scope.ConfigureAwait(false))
            {
                var sp = scope.ServiceProvider;
                var entries = tenantGroup.ToList();
                if (revalidate)
                {
                    entries = await RevalidateAsync(sp, entries, ct).ConfigureAwait(false);
                }

                var users = entries.Select(e => e.UserId).Distinct().ToList();
                if (users.Count == 0)
                {
                    continue;
                }

                var db = sp.GetRequiredService<WikiDbContext>();
                var figures = await db.Notifications.AsNoTracking()
                    .Where(n => users.Contains(n.RecipientId) && n.ReadAt == null)
                    .GroupBy(n => n.RecipientId)
                    .Select(g => new { User = g.Key, Count = g.Count(), Latest = (DateTimeOffset?)g.Max(n => n.CreatedAt) })
                    .ToDictionaryAsync(x => x.User, ct).ConfigureAwait(false);
                foreach (var user in users)
                {
                    var key = (tenantGroup.Key, user);
                    seen.Add(key);
                    var now = figures.TryGetValue(user, out var f) ? (f.Count, f.Latest) : (0, null);
                    var known = _last.TryGetValue(key, out var before);
                    if (known && before == now)
                    {
                        continue;
                    }

                    // A user seen for the first time is signalled too: a notification created between the client's own
                    // refresh on connect and our first look would otherwise become the silent baseline and never be pushed.
                    _last[key] = now;
                    await hub.Clients.Group(EventsHub.Group(tenantGroup.Key, user)).SendAsync(EventsHub.NotificationsMethod, ct).ConfigureAwait(false);
                }
            }
        }

        foreach (var stale in _last.Keys.Where(k => !seen.Contains(k)).ToList())
        {
            _last.Remove(stale);
        }
    }

    private static async Task<List<EventConnections.Entry>> RevalidateAsync(IServiceProvider sp, List<EventConnections.Entry> entries, CancellationToken ct)
    {
        var tenants = sp.GetRequiredService<ITenantDirectory>();
        var tenant = await tenants.FindByIdAsync(entries[0].Tenant.TenantId, ct).ConfigureAwait(false);
        var directory = sp.GetRequiredService<IPrincipalDirectory>();
        var sessions = sp.GetService<ISessionValidator>();
        var alive = new List<EventConnections.Entry>();
        foreach (var e in entries)
        {
            var ok = tenant is { IsActive: true }
                && (await directory.GetPrincipalAsync(e.UserId, fresh: true, ct).ConfigureAwait(false)).IsActiveMember
                && (e.SessionId is not { } sid || sessions is null || await sessions.IsActiveAsync(sid, e.UserId, ct).ConfigureAwait(false));
            if (ok)
            {
                alive.Add(e);
            }
            else
            {
                e.Context.Abort();
            }
        }

        return alive;
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Notification push tick failed")]
    private static partial void LogPushFailed(ILogger logger, Exception ex);
}

public static class NotificationRealtimeExtensions
{
    /// <summary>SignalR event hub and the push service (API host only).</summary>
    public static IServiceCollection AddWikiRealtime(this IServiceCollection services)
    {
        services.AddSignalR(o =>
        {
            o.EnableDetailedErrors = false;
            o.MaximumReceiveMessageSize = 4 * 1024; // the client never sends payloads
        });
        services.AddSingleton<EventConnections>();
        services.AddHostedService<NotificationPushService>();
        return services;
    }

    public static IEndpointConventionBuilder MapWikiRealtime(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        // WebSockets only: the hub reads the tenant and user from the connection's own request scope, which long polling
        // and server-sent events replace with a fresh scope (no tenant). Live editing needs WebSockets anyway.
        return app.MapHub<EventsHub>(EventsHub.Path, o => o.Transports = Microsoft.AspNetCore.Http.Connections.HttpTransportType.WebSockets)
            .RequireHostKind(RaaviHostKind.Tenant);
    }
}
