using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Raavi.Application.Abstractions.Jobs;
using Raavi.Domain.Common;
using Raavi.Tenancy.Persistence;

namespace Raavi.Tenancy.Application;

/// <summary>
/// Recurring <b>system</b> job that hands one tenant job to every active tenant (Phase 10c: periodic sweeps such as "stale pages").
/// It only reads the tenant list and enqueues; the work itself runs through <c>TenantJobRunner</c> inside each tenant's scope.
/// A generic <b>class</b>, not a generic method, so Hangfire can serialize the closed type (see TenantJobRunner).
/// </summary>
public sealed partial class TenantFanOut<TJob, TArgs>(TenancyDbContext db, ITenantJobScheduler scheduler, ILogger<TenantFanOut<TJob, TArgs>> logger)
    where TJob : ITenantJob<TArgs>
{
    public const int MaxTenants = 5000;

    public async Task RunAsync(TArgs args, CancellationToken cancellationToken)
    {
        var ids = await db.Tenants.AsNoTracking().Where(t => t.Status == TenantStatus.Active).OrderBy(t => t.Id).Select(t => t.Id).Take(MaxTenants).ToListAsync(cancellationToken).ConfigureAwait(false);
        foreach (var id in ids)
        {
            scheduler.EnqueueForTenant<TJob, TArgs>(id, args);
        }

        LogCompleted(logger, typeof(TJob).Name, ids.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Fan-out of {Job} enqueued for {Count} tenants")]
    private static partial void LogCompleted(ILogger logger, string job, int count);
}
