using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Raavi.Application.Abstractions.Time;
using Raavi.Tenancy.Domain;
using Raavi.Tenancy.Persistence;

namespace Raavi.Tenancy.Application;

/// <summary>
/// Recurring <b>system</b> job (touches only the system-level TenantDomains table): verifies pending domains created
/// in the last 7 days and re-verifies verified domains once per day. Repeated failures make a domain stop resolving.
/// </summary>
public sealed partial class DomainVerificationJob(TenancyDbContext db, TenantDomainService domains, IClock clock, ILogger<DomainVerificationJob> logger)
{
    public const string RecurringJobId = "system:domain-verification";

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var now = clock.UtcNow;
        var pendingSince = now.AddDays(-7);
        var recheckBefore = now.AddHours(-23);

        var due = await db.TenantDomains
            .Where(d => d.RemovedAt == null && (
                (d.Status == TenantDomainStatus.PendingVerification && d.CreatedAt > pendingSince) ||
                (d.Status == TenantDomainStatus.Verified && (d.LastCheckedAt == null || d.LastCheckedAt < recheckBefore)) ||
                (d.Status == TenantDomainStatus.VerificationLost && (d.LastCheckedAt == null || d.LastCheckedAt < recheckBefore))))
            .OrderBy(d => d.LastCheckedAt)
            .Take(500)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        foreach (var domain in due)
        {
            await domains.CheckAsync(domain, cancellationToken).ConfigureAwait(false);
        }

        LogCompleted(logger, due.Count);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Domain verification sweep checked {Count} domains")]
    private static partial void LogCompleted(ILogger logger, int count);
}
