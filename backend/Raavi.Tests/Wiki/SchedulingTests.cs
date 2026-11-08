using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Raavi.Application.Abstractions.Jobs;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Tenancy;
using Raavi.Tenancy.Application;
using Raavi.Tenancy.Persistence;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Application;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 10c: time-based triggers and reminders. Jobs are run directly inside the tenant scope (no Hangfire server in tests).</summary>
[Collection(SqlTestGroup.Name)]
public sealed class SchedulingTests(SqlServerFixture fx)
{
    private readonly WikiTestKit _kit = new(fx);

    private sealed class FixedClock(DateTimeOffset now) : IClock
    {
        public DateTimeOffset UtcNow { get; } = now;
    }

    private sealed class RecordingScheduler : ITenantJobScheduler
    {
        public List<(Type Job, Guid Tenant)> Enqueued { get; } = [];

        public string Enqueue<TJob, TArgs>(TArgs args) where TJob : ITenantJob<TArgs> => throw new NotSupportedException();

        public string Schedule<TJob, TArgs>(TArgs args, DateTimeOffset runAt) where TJob : ITenantJob<TArgs> => throw new NotSupportedException();

        public string EnqueueForTenant<TJob, TArgs>(Guid tenantId, TArgs args) where TJob : ITenantJob<TArgs>
        {
            Enqueued.Add((typeof(TJob), tenantId));
            return "job";
        }
    }

    private sealed class NoopJob : ITenantJob<string>
    {
        public Task ExecuteAsync(string args, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private AsyncServiceScope Scope(IdentityTestKit.TestTenant t) =>
        fx.Services.GetRequiredService<ITenantScopeFactory>().CreateScope(new ResolvedTenant(t.Id, t.Code, t.Slug, TenantResolutionSource.BackgroundJob, null));

    private static async Task<JsonElement[]> NotificationsAsync(Browser b) =>
        (await b.GetJsonAsync("api/v1/notifications")).GetProperty("items").EnumerateArray().ToArray();

    private static async Task<string[]> KindsAsync(Browser b) => (await NotificationsAsync(b)).Select(n => n.GetProperty("kind").GetString()!).ToArray();

    private static async Task<JsonElement> RequestAsync(Browser b, Guid page, object body)
    {
        using var r = await b.PostAsync($"api/v1/content/{page}/approvals", body);
        return await ReadAsync(r);
    }

    [Fact]
    public async Task Approval_reminders_and_overdue_notices_reach_only_the_people_still_waiting()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (a, _, aId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var (b, _, bId) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "Quarterly report"));
        var request = await RequestAsync(admin, page, new { mode = "All", approverIds = new[] { aId, bId }, dueAt = DateTimeOffset.UtcNow.AddDays(3) });
        var id = request.GetProperty("id").GetGuid();
        var due = request.GetProperty("dueAt").GetDateTimeOffset();
        await _kit.DispatchOutboxAsync(); // the "requested" notifications

        async Task RunAsync(ApprovalReminderKind kind, DateTimeOffset dueAt)
        {
            await using var scope = Scope(tenant);
            await scope.ServiceProvider.GetRequiredService<ApprovalService>().RunReminderAsync(new ApprovalReminderArgs(id, kind, dueAt), CancellationToken.None);
            await _kit.DispatchOutboxAsync();
        }

        await RunAsync(ApprovalReminderKind.Reminder, due);
        Assert.Contains("approval.reminder", await KindsAsync(a));
        Assert.Contains("approval.reminder", await KindsAsync(b));
        Assert.DoesNotContain("approval.reminder", await KindsAsync(admin));

        // A moved due date makes an old job harmless.
        await RunAsync(ApprovalReminderKind.Overdue, due.AddDays(1));
        Assert.DoesNotContain("approval.overdue", await KindsAsync(b));

        // A has decided: only B is still pending, and the requester learns that the time is up.
        using (var decide = await a.PostAsync($"api/v1/approvals/{id}/decision", new { decision = "Approved" }))
        {
            Assert.True(decide.IsSuccessStatusCode);
        }

        await RunAsync(ApprovalReminderKind.Overdue, due);
        Assert.Contains("approval.overdue", await KindsAsync(b));
        Assert.Contains("approval.overdue", await KindsAsync(admin));
        Assert.DoesNotContain("approval.overdue", await KindsAsync(a));

        // Once the request is decided, further jobs do nothing.
        using (var decide = await b.PostAsync($"api/v1/approvals/{id}/decision", new { decision = "Approved" }))
        {
            Assert.True(decide.IsSuccessStatusCode);
        }

        var before = (await NotificationsAsync(b)).Length;
        await RunAsync(ApprovalReminderKind.Reminder, due);
        Assert.Equal(before, (await NotificationsAsync(b)).Length);
    }

    [Fact]
    public async Task The_stale_sweep_runs_each_workflow_once_per_page_update()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var old = Id(await CreatePageAsync(admin, key, "Old runbook"));
        var other = Id(await CreatePageAsync(admin, key, "Meeting notes"));
        using (var create = await admin.PostAsync($"api/v1/spaces/{key}/workflows", new
        {
            name = "Flag stale runbooks",
            trigger = "content.stale",
            staleDays = 30,
            conditions = new object[] { new { field = "Title", @operator = "Contains", value = "runbook" } },
            actions = new object[] { new { type = "AddLabel", label = "stale" } },
        }))
        {
            Assert.Equal(30, (await ReadAsync(create)).GetProperty("staleDays").GetInt32());
        }

        async Task<int> SweepAsync(DateTimeOffset at)
        {
            await using var scope = Scope(tenant);
            var sp = scope.ServiceProvider;
            return await new StaleSweep(sp.GetRequiredService<WikiDbContext>(), sp.GetRequiredService<WorkflowEngine>(), new FixedClock(at)).RunAsync(CancellationToken.None);
        }

        async Task<string[]> LabelsAsync(Guid p) => (await admin.GetJsonAsync($"api/v1/content/{p}")).GetProperty("labels").EnumerateArray().Select(l => l.GetString()!).ToArray();

        // Nothing is stale yet.
        Assert.Equal(0, await SweepAsync(DateTimeOffset.UtcNow.AddDays(10)));
        Assert.Empty(await LabelsAsync(old));

        // 31 days later both pages are stale; the condition decides which one is labelled.
        Assert.Equal(3, await SweepAsync(DateTimeOffset.UtcNow.AddDays(31))); // the space home page is stale too
        Assert.Equal(["stale"], await LabelsAsync(old));
        Assert.Empty(await LabelsAsync(other));

        // A second sweep changes nothing, even much later (the pages were handled for this update).
        Assert.Equal(0, await SweepAsync(DateTimeOffset.UtcNow.AddDays(90)));

        // Editing a page re-arms it: after it has been quiet again for 30 days, it is handled again.
        var (_, etag) = await GetWithETagAsync(admin, $"api/v1/content/{old}");
        using (var edit = await UpdatePageAsync(admin, old, etag, "Old runbook", Doc("fresh content")))
        {
            Assert.True(edit.IsSuccessStatusCode);
        }

        Assert.Equal(0, await SweepAsync(DateTimeOffset.UtcNow.AddDays(10)));
        Assert.Equal(1, await SweepAsync(DateTimeOffset.UtcNow.AddDays(40)));

        var workflowId = (await admin.GetJsonAsync($"api/v1/spaces/{key}/workflows"))[0].GetProperty("id").GetGuid();
        var runs = (await admin.GetJsonAsync($"api/v1/workflows/{workflowId}/runs")).EnumerateArray().ToArray();
        Assert.Equal(["content.stale"], runs.Select(r => r.GetProperty("eventType").GetString()!).Distinct());
        Assert.Equal(4, runs.Length); // home page and meeting notes (skipped), old runbook, old runbook again
        Assert.Equal(2, runs.Count(r => r.GetProperty("status").GetString() == "Skipped"));
    }

    [Fact]
    public async Task The_fan_out_enqueues_the_job_for_every_active_tenant_and_nothing_else()
    {
        var (a, _, _) = await _kit.Identity.TenantWithAdminAsync();
        var (b, _, _) = await _kit.Identity.TenantWithAdminAsync();
        var scheduler = new RecordingScheduler();
        await using var scope = fx.UnscopedScope();
        var db = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
        await new TenantFanOut<NoopJob, string>(db, scheduler, NullLogger<TenantFanOut<NoopJob, string>>.Instance).RunAsync("go", CancellationToken.None);

        Assert.Contains((typeof(NoopJob), a.Id), scheduler.Enqueued);
        Assert.Contains((typeof(NoopJob), b.Id), scheduler.Enqueued);
        Assert.Equal(scheduler.Enqueued.Count, scheduler.Enqueued.Select(e => e.Tenant).Distinct().Count()); // once per tenant
    }
}

/// <summary>Definition rules for the time-based trigger.</summary>
public sealed class StaleTriggerDomainTests
{
    private static readonly WfActionInput Label = new(WfActionType.AddLabel, "stale", null, null);

    [Fact]
    public void The_stale_trigger_needs_a_day_count_and_other_triggers_refuse_one()
    {
        var w = Workflow.Create(Guid.NewGuid(), "Stale", true, "content.stale", 90, [], [Label], Guid.NewGuid(), DateTimeOffset.UtcNow);
        Assert.Equal(90, w.StaleDays);
        w.Update("Stale", true, "content.created", null, [], [Label], DateTimeOffset.UtcNow);
        Assert.Null(w.StaleDays);

        foreach (var (trigger, days) in new (string, int?)[] { ("content.stale", null), ("content.stale", 0), ("content.stale", 3651), ("content.created", 30) })
        {
            var ex = Assert.Throws<DomainException>(() => Workflow.Create(Guid.NewGuid(), "x", true, trigger, days, [], [Label], Guid.NewGuid(), DateTimeOffset.UtcNow));
            Assert.Equal("workflow.stale_days_invalid", ex.Code);
        }
    }
}
