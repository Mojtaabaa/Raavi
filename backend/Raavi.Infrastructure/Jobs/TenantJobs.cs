using System.Diagnostics;
using Hangfire;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raavi.Application.Abstractions.Jobs;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Tenancy;

namespace Raavi.Infrastructure.Jobs;

/// <summary>
/// Executes every tenant job (ADR-017, TENANT_ISOLATION.md §7). Hangfire activates the runner; the runner validates the
/// envelope and the tenant's status, opens a scope bound to that tenant and only then resolves and runs the job.
/// A missing or empty envelope fails permanently; an inactive tenant throws so Hangfire retries later.
/// The runner is a generic <b>class</b> (not a generic method): Hangfire stores the closed type in the job, but it cannot
/// store the type arguments of a generic method, so such jobs could be enqueued yet never executed.
/// </summary>
public sealed partial class TenantJobRunner<TJob, TArgs>(ITenantScopeFactory scopes, ITenantDirectory directory, ILogger<TenantJobRunner<TJob, TArgs>> logger)
    where TJob : ITenantJob<TArgs>
{
    [AutomaticRetry(Attempts = 10)]
    public async Task RunAsync(TenantJobContext envelope, TArgs args, CancellationToken cancellationToken)
    {
        if (envelope is null || envelope.TenantId == Guid.Empty)
        {
            throw new InvalidOperationException($"Tenant job '{typeof(TJob).Name}' has no tenant envelope.");
        }

        var tenant = await directory.FindByIdAsync(envelope.TenantId, cancellationToken).ConfigureAwait(false);
        if (tenant is null || !tenant.IsActive)
        {
            LogTenantUnavailable(logger, typeof(TJob).Name, envelope.TenantId, tenant?.Status.ToString() ?? "missing");
            throw new TenantUnavailableException($"Tenant {envelope.TenantId} is not active.");
        }

        using var activity = new Activity("raavi.tenant_job").SetTag("raavi.tenant_id", tenant.Id).SetTag("raavi.job", typeof(TJob).Name).Start();
        using var logScope = logger.BeginScope(new Dictionary<string, object> { ["TenantId"] = tenant.Id, ["CorrelationId"] = envelope.CorrelationId });

        var scope = scopes.CreateScope(new ResolvedTenant(tenant.Id, tenant.Code, tenant.Slug, TenantResolutionSource.BackgroundJob, null));
        await using (scope.ConfigureAwait(false))
        {
            var job = ActivatorUtilities.GetServiceOrCreateInstance<TJob>(scope.ServiceProvider);
            await job.ExecuteAsync(args, cancellationToken).ConfigureAwait(false);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Tenant job {Job} deferred: tenant {TenantId} is {Status}")]
    private static partial void LogTenantUnavailable(ILogger logger, string job, Guid tenantId, string status);
}

/// <summary>Captures the current tenant into the envelope; refuses to enqueue without one.</summary>
public sealed class HangfireTenantJobScheduler(IBackgroundJobClient client, ITenantContext tenantContext) : ITenantJobScheduler
{
    public string Enqueue<TJob, TArgs>(TArgs args) where TJob : ITenantJob<TArgs>
    {
        var envelope = Envelope<TJob>();
        return client.Enqueue<TenantJobRunner<TJob, TArgs>>(r => r.RunAsync(envelope, args, CancellationToken.None));
    }

    public string Schedule<TJob, TArgs>(TArgs args, DateTimeOffset runAt) where TJob : ITenantJob<TArgs>
    {
        var envelope = Envelope<TJob>();
        return client.Schedule<TenantJobRunner<TJob, TArgs>>(r => r.RunAsync(envelope, args, CancellationToken.None), runAt);
    }

    public string EnqueueForTenant<TJob, TArgs>(Guid tenantId, TArgs args) where TJob : ITenantJob<TArgs>
    {
        if (tenantId == Guid.Empty)
        {
            throw new ArgumentException("A tenant id is required.", nameof(tenantId));
        }

        var envelope = new TenantJobContext(tenantId, null, Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"));
        return client.Enqueue<TenantJobRunner<TJob, TArgs>>(r => r.RunAsync(envelope, args, CancellationToken.None));
    }

    private TenantJobContext Envelope<TJob>()
    {
        if (!tenantContext.IsResolved)
        {
            throw new TenantNotResolvedException($"Cannot enqueue tenant job '{typeof(TJob).Name}' without a resolved tenant.");
        }

        return new TenantJobContext(tenantContext.Tenant.TenantId, null, Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N"));
    }
}
