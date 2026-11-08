using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Jobs;
using Raavi.Application.Abstractions.Time;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Persistence;

namespace Raavi.Wiki.Application;

/// <summary>Argument of the daily sweep (a version number, so the payload is never empty).</summary>
public sealed record StaleSweepArgs(int Version = 1);

/// <summary>Tenant job: runs the stale-page sweep of one tenant. A system fan-out job enqueues it for every active tenant once a day.</summary>
public sealed class StaleSweepJob(StaleSweep sweep) : ITenantJob<StaleSweepArgs>
{
    public const string RecurringJobId = "system:stale-sweep";

    public Task ExecuteAsync(StaleSweepArgs args, CancellationToken cancellationToken) => sweep.RunAsync(cancellationToken);
}

/// <summary>
/// Time-based workflow trigger (Phase 10c, ADR-033): for every enabled <c>content.stale</c> workflow, runs it for current pages of its space that
/// have not been updated for the configured number of days. A page is handled once per update: a run recorded after the page's last update
/// means "already done", so a sweep never repeats itself and a later edit re-arms the page. Pages are taken oldest first, at most
/// <see cref="PagesPerWorkflow"/> per workflow and sweep, and never beyond the workflow's hourly run budget.
/// </summary>
public sealed class StaleSweep(WikiDbContext db, WorkflowEngine engine, IClock clock)
{
    public const int PagesPerWorkflow = 100;
    public const int MaxWorkflows = 200;

    public async Task<int> RunAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        var workflows = await db.Workflows.Where(w => w.Enabled && w.Trigger == WorkflowTriggers.Stale).OrderBy(w => w.CreatedAt).Take(MaxWorkflows).ToListAsync(ct).ConfigureAwait(false);
        var executed = 0;
        foreach (var w in workflows)
        {
            var since = now.AddHours(-1);
            var used = await db.WorkflowRuns.CountAsync(r => r.WorkflowId == w.Id && r.StartedAt >= since && r.Status != WorkflowRunStatus.Skipped, ct).ConfigureAwait(false);
            var take = Math.Min(PagesPerWorkflow, Workflow.MaxRunsPerHour - used);
            if (take <= 0 || w.StaleDays is not { } days)
            {
                continue;
            }

            var cutoff = now.AddDays(-days);
            var workflowId = w.Id;
            var pages = await db.Nodes.AsNoTracking()
                .Where(n => n.SpaceId == w.SpaceId && n.Status == ContentStatus.Current && n.UpdatedAt < cutoff
                    && !db.WorkflowRuns.Any(r => r.WorkflowId == workflowId && r.ContentId == n.Id && r.EventType == WorkflowTriggers.Stale && r.StartedAt >= n.UpdatedAt))
                .OrderBy(n => n.UpdatedAt).Take(take).ToListAsync(ct).ConfigureAwait(false);
            foreach (var n in pages)
            {
                try
                {
                    await engine.RunAsync(w, EventId(w.Id, n.Id, n.UpdatedAt), WorkflowTriggers.Stale, new WikiEvent(n.SpaceId, n.Id, Guid.Empty, n.Title, n.CurrentVersionNumber == 0 ? null : n.CurrentVersionNumber), ct).ConfigureAwait(false);
                    executed++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The failure is on the run row (status Failed); the next sweep sees a run after the last update and moves on, an administrator sees it in the history.
                    db.ChangeTracker.Clear();
                }
            }
        }

        return executed;
    }

    /// <summary>Stable id per (workflow, page, page version-of-time): the run log's uniqueness makes a repeat impossible.</summary>
    internal static Guid EventId(Guid workflowId, Guid contentId, DateTimeOffset updatedAt) =>
        new(SHA256.HashData(Encoding.UTF8.GetBytes($"stale|{workflowId:N}|{contentId:N}|{updatedAt.UtcTicks}"))[..16]);
}
