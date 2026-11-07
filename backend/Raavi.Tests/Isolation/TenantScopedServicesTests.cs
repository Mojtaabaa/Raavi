using Hangfire;
using Hangfire.Common;
using Hangfire.States;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Raavi.Application.Abstractions.Caching;
using Raavi.Application.Abstractions.Jobs;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Infrastructure.Jobs;
using Raavi.Infrastructure.Tenancy;
using Raavi.Tenancy.Application;
using Raavi.Tenancy.Persistence;
using Raavi.Tests.Infrastructure;

namespace Raavi.Tests.Isolation;

/// <summary>Tenant context, cache and background job isolation (TENANT_ISOLATION.md §2 layers 8, 11 and §7).</summary>
[Collection(SqlTestGroup.Name)]
public sealed class TenantScopedServicesTests(SqlServerFixture fx)
{
    [Fact]
    public void Tenant_context_is_immutable_once_set()
    {
        var holder = new TenantContextHolder();
        Assert.False(holder.IsResolved);
        Assert.Throws<TenantNotResolvedException>(() => holder.Tenant);
        holder.Set(new ResolvedTenant(Guid.NewGuid(), "A1X", "a", TenantResolutionSource.Subdomain, "a.x"));
        Assert.Throws<InvalidOperationException>(() => holder.Set(new ResolvedTenant(Guid.NewGuid(), "B1X", "b", TenantResolutionSource.Subdomain, "b.x")));
    }

    [Fact]
    public async Task Tenant_cache_keys_never_collide_across_tenants()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("A"), SqlServerFixture.Unique("a").ToLowerInvariant());
        var b = await fx.CreateTenantAsync(SqlServerFixture.Unique("B"), SqlServerFixture.Unique("b").ToLowerInvariant());

        await using (var sa = fx.TenantScope(a))
        {
            var v = await sa.ServiceProvider.GetRequiredService<ITenantCache>().GetOrCreateAsync("space:home", _ => ValueTask.FromResult("A-secret"));
            Assert.Equal("A-secret", v);
        }

        await using (var sb = fx.TenantScope(b))
        {
            var v = await sb.ServiceProvider.GetRequiredService<ITenantCache>().GetOrCreateAsync("space:home", _ => ValueTask.FromResult("B-value"));
            Assert.Equal("B-value", v);
        }

        await using var none = fx.UnscopedScope();
        await Assert.ThrowsAsync<TenantNotResolvedException>(async () =>
            await none.ServiceProvider.GetRequiredService<ITenantCache>().GetOrCreateAsync("space:home", _ => ValueTask.FromResult("x")));
    }

    [Fact]
    public async Task Tenant_job_runs_inside_its_own_tenant_scope_only()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("A"), SqlServerFixture.Unique("a").ToLowerInvariant());
        await fx.CreateTenantAsync(SqlServerFixture.Unique("B"), SqlServerFixture.Unique("b").ToLowerInvariant());
        var runner = Runner();

        ProbeJob.Seen.Clear();
        await runner.RunAsync(new TenantJobContext(a.Id, null, "corr"), "hello", CancellationToken.None);

        var seen = Assert.Single(ProbeJob.Seen);
        Assert.Equal(a.Id, seen.TenantId);
        Assert.Equal([a.Id], seen.VisibleSettingsTenants);
    }

    [Fact]
    public async Task Tenant_job_without_envelope_or_for_inactive_tenant_does_not_run()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("A"), SqlServerFixture.Unique("a").ToLowerInvariant());
        var runner = Runner();
        ProbeJob.Seen.Clear();

        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(null!, "x", CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => runner.RunAsync(new TenantJobContext(Guid.Empty, null, "c"), "x", CancellationToken.None));
        await Assert.ThrowsAsync<TenantUnavailableException>(() => runner.RunAsync(new TenantJobContext(Guid.NewGuid(), null, "c"), "x", CancellationToken.None));

        await using (var scope = fx.UnscopedScope())
        {
            var admin = scope.ServiceProvider.GetRequiredService<TenantAdministrationService>();
            Assert.True((await admin.SuspendAsync(a.Id, "test", CancellationToken.None)).IsSuccess);
        }

        await Assert.ThrowsAsync<TenantUnavailableException>(() => runner.RunAsync(new TenantJobContext(a.Id, null, "c"), "x", CancellationToken.None));
        Assert.Empty(ProbeJob.Seen);
    }

    [Fact]
    public async Task Scheduler_captures_current_tenant_and_refuses_without_one()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("A"), SqlServerFixture.Unique("a").ToLowerInvariant());
        var client = new RecordingJobClient();

        await using (var scope = fx.TenantScope(a))
        {
            var scheduler = new HangfireTenantJobScheduler(client, scope.ServiceProvider.GetRequiredService<ITenantContext>());
            scheduler.Enqueue<ProbeJob, string>("payload");
        }

        var job = Assert.Single(client.Jobs);
        var envelope = Assert.IsType<TenantJobContext>(job.Args[0]);
        Assert.Equal(a.Id, envelope.TenantId);

        await using var none = fx.UnscopedScope();
        var unscoped = new HangfireTenantJobScheduler(client, none.ServiceProvider.GetRequiredService<ITenantContext>());
        Assert.Throws<TenantNotResolvedException>(() => unscoped.Enqueue<ProbeJob, string>("payload"));
    }

    private TenantJobRunner<ProbeJob, string> Runner() => new(
        fx.Services.GetRequiredService<ITenantScopeFactory>(),
        fx.Services.CreateScope().ServiceProvider.GetRequiredService<ITenantDirectory>(),
        NullLogger<TenantJobRunner<ProbeJob, string>>.Instance);

    /// <summary>
    /// Regression: a job enqueued through the scheduler must survive Hangfire's real serialization (what the worker does),
    /// not only the in-memory recording client. A generic <i>method</i> runner failed here with "does not contain a method
    /// with signature" so tenant jobs were enqueued but never ran.
    /// </summary>
    [Fact]
    public async Task Enqueued_tenant_jobs_survive_hangfire_serialization_and_resolve_their_runner()
    {
        var a = await fx.CreateTenantAsync(SqlServerFixture.Unique("A"), SqlServerFixture.Unique("a").ToLowerInvariant());
        var client = new RecordingJobClient();
        await using (var scope = fx.TenantScope(a))
        {
            new HangfireTenantJobScheduler(client, scope.ServiceProvider.GetRequiredService<ITenantContext>()).Enqueue<ProbeJob, string>("payload");
            new HangfireTenantJobScheduler(client, scope.ServiceProvider.GetRequiredService<ITenantContext>()).Schedule<ProbeJob, string>("later", DateTimeOffset.UtcNow.AddHours(1));
        }

        Assert.Equal(2, client.Jobs.Count);
        foreach (var queued in client.Jobs)
        {
            var restored = Hangfire.Storage.InvocationData.SerializeJob(queued).DeserializeJob(); // throws when the method cannot be resolved
            Assert.Equal(typeof(TenantJobRunner<ProbeJob, string>), restored.Type);
            Assert.Equal(nameof(TenantJobRunner<ProbeJob, string>.RunAsync), restored.Method.Name);
            Assert.Equal(a.Id, Assert.IsType<TenantJobContext>(restored.Args[0]).TenantId);
        }

        // The runner is resolvable from the container exactly as the Hangfire activator does it.
        await using var activation = fx.Services.CreateAsyncScope();
        Assert.NotNull(Microsoft.Extensions.DependencyInjection.ActivatorUtilities.GetServiceOrCreateInstance<TenantJobRunner<ProbeJob, string>>(activation.ServiceProvider));
    }


    public sealed record Observation(Guid TenantId, IReadOnlyList<Guid> VisibleSettingsTenants);

    public sealed class ProbeJob(ITenantContext tenant, TenancyDbContext db) : ITenantJob<string>
    {
        public static readonly List<Observation> Seen = [];

        public async Task ExecuteAsync(string args, CancellationToken cancellationToken)
        {
            var visible = await db.TenantSettings.IgnoreQueryFilters().Select(s => s.TenantId).ToListAsync(cancellationToken);
            Seen.Add(new Observation(tenant.Tenant.TenantId, visible));
        }
    }

    private sealed class RecordingJobClient : IBackgroundJobClient
    {
        public List<Job> Jobs { get; } = [];

        public string Create(Job job, IState state)
        {
            Jobs.Add(job);
            return Jobs.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        public bool ChangeState(string jobId, IState state, string expectedState) => true;
    }
}
