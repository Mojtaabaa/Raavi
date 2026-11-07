using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Authorization;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Jobs;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Http;
using Raavi.Migration.Confluence;
using Raavi.Migration.Persistence;

namespace Raavi.Migration;

public sealed record CreateMigrationRequest(string? Url, string? Token, IReadOnlyList<string>? Spaces, IReadOnlyDictionary<string, string>? KeyMap, bool? DryRun, bool? Reconvert = null, bool? MapUsersByEmail = null, IReadOnlyDictionary<string, string>? UserMap = null);

public sealed record StartMigrationRequest(string? Token);

public sealed record MigrationJobDto(
    Guid Id, string SourceUrl, string? SourceVersion, string Mode, string Status, string? Control, DateTimeOffset CreatedAt, DateTimeOffset? StartedAt, DateTimeOffset? FinishedAt,
    string? CurrentOperation, int SpacesTotal, int SpacesDone, int PagesTotal, int PagesDone, int PagesFailed, int VersionsDone, int Warnings, bool HasReport, bool CanResume);

public sealed record MigrationEventDto(DateTimeOffset At, string Level, string Code, string? SourceRef, string Message);

/// <summary>Administration of Confluence migrations for the current workspace. Callers are tenant administrators (endpoint policy).</summary>
public sealed class MigrationService(
    MigrationDbContext db,
    SourceHostPolicy hostPolicy,
    IConfluenceSourceFactory sources,
    ITenantJobScheduler scheduler,
    ICurrentUser current,
    ITenantContext tenant,
    IDataProtectionProvider protection,
    IClock clock)
{
    private static readonly Error NotFound = new("migration.not_found", "Migration not found.", ErrorKind.NotFound);

    private IDataProtector Protector => protection.CreateProtector(ConfluenceMigrationJob.TokenPurpose(tenant.Tenant.TenantId));

    public async Task<IReadOnlyList<MigrationJobDto>> ListAsync(CancellationToken ct) =>
        (await db.Jobs.AsNoTracking().OrderByDescending(j => j.CreatedAt).Take(50).ToListAsync(ct).ConfigureAwait(false)).Select(ToDto).ToList();

    public async Task<Result<MigrationJobDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct).ConfigureAwait(false);
        return job is null ? Result<MigrationJobDto>.Failure(NotFound) : Result<MigrationJobDto>.Success(ToDto(job));
    }

    /// <summary>Validates the source address (allow-list), proves the token by reading the source version, and records the job. Nothing is read or written yet.</summary>
    public async Task<Result<MigrationJobDto>> CreateAsync(CreateMigrationRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Token))
        {
            return Result<MigrationJobDto>.Failure(new Error("migration.token_required", "An access token for the source is required."));
        }

        try
        {
            var baseUrl = hostPolicy.Validate(request.Url);
            using var source = sources.Create(baseUrl, request.Token);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var info = await source.DetectAsync(timeout.Token).ConfigureAwait(false);

            var options = JsonSerializer.Serialize(new MigrationJobOptions(request.Spaces?.Where(s => !string.IsNullOrWhiteSpace(s)).Take(200).ToList(), request.KeyMap, request.Reconvert == true, request.MapUsersByEmail == true, request.UserMap), MigrationJobOptions.Json);
            var job = MigrationJob.Create((int)ConfluenceSourceKind.RestApi, baseUrl.AbsoluteUri, SourceKeys.Of(baseUrl), request.DryRun == false ? MigrationMode.Execute : MigrationMode.DryRun,
                options, Protector.Protect(request.Token), current.UserId, clock.UtcNow);
            db.Jobs.Add(job);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<MigrationJobDto>.Success(ToDto(job) with { SourceVersion = $"{info.Version}" });
        }
        catch (ConfluenceSourceException ex)
        {
            return Result<MigrationJobDto>.Failure(new Error(ex.Code, ex.Message));
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return Result<MigrationJobDto>.Failure(new Error("source.timeout", "The source did not answer in time."));
        }
    }

    public async Task<Result<MigrationJobDto>> StartAsync(Guid id, StartMigrationRequest? request, CancellationToken ct)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct).ConfigureAwait(false);
        if (job is null)
        {
            return Result<MigrationJobDto>.Failure(NotFound);
        }

        try
        {
            if (!string.IsNullOrWhiteSpace(request?.Token))
            {
                job.SetToken(Protector.Protect(request.Token));
            }

            if (job.ProtectedToken is null)
            {
                return Result<MigrationJobDto>.Failure(new Error("migration.token_required", "Provide the source access token to start this migration."));
            }

            job.Queue();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Control, MigrationControl.None), ct).ConfigureAwait(false);
            scheduler.Enqueue<ConfluenceMigrationJob, MigrationJobArgs>(new MigrationJobArgs(job.Id));
            return Result<MigrationJobDto>.Success(ToDto(job));
        }
        catch (Raavi.Domain.Common.DomainException ex)
        {
            return Result<MigrationJobDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Conflict));
        }
    }

    public Task<Result<MigrationJobDto>> PauseAsync(Guid id, CancellationToken ct) => RequestAsync(id, MigrationControl.Pause, ct);

    public Task<Result<MigrationJobDto>> CancelAsync(Guid id, CancellationToken ct) => RequestAsync(id, MigrationControl.Cancel, ct);

    private async Task<Result<MigrationJobDto>> RequestAsync(Guid id, MigrationControl control, CancellationToken ct)
    {
        var job = await db.Jobs.FirstOrDefaultAsync(j => j.Id == id, ct).ConfigureAwait(false);
        if (job is null)
        {
            return Result<MigrationJobDto>.Failure(NotFound);
        }

        if (job.IsActive)
        {
            // The runner honours it between pages.
            await db.Jobs.Where(j => j.Id == id).ExecuteUpdateAsync(s => s.SetProperty(j => j.Control, control), ct).ConfigureAwait(false);
            job = await db.Jobs.AsNoTracking().FirstAsync(j => j.Id == id, ct).ConfigureAwait(false);
        }
        else if (control == MigrationControl.Cancel && !job.IsFinished)
        {
            job.Finish(MigrationStatus.Cancelled, null, clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        else
        {
            return Result<MigrationJobDto>.Failure(new Error("migration.not_running", "This migration is not running.", ErrorKind.Conflict));
        }

        return Result<MigrationJobDto>.Success(ToDto(job));
    }

    public async Task<Result<IReadOnlyList<MigrationEventDto>>> EventsAsync(Guid id, string? level, int take, CancellationToken ct)
    {
        if (!await db.Jobs.AnyAsync(j => j.Id == id, ct).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<MigrationEventDto>>.Failure(NotFound);
        }

        var query = db.Events.AsNoTracking().Where(e => e.JobId == id);
        if (Enum.TryParse<MigrationEventLevel>(level, true, out var lv))
        {
            query = query.Where(e => e.Level == lv);
        }

        var rows = await query.OrderBy(e => e.At).Take(Math.Clamp(take, 1, 500)).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<MigrationEventDto>>.Success(rows.Select(e => new MigrationEventDto(e.At, e.Level.ToString(), e.Code, e.SourceRef, e.Message)).ToList());
    }

    public async Task<Result<JsonElement>> ReportAsync(Guid id, CancellationToken ct)
    {
        var job = await db.Jobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == id, ct).ConfigureAwait(false);
        if (job is null)
        {
            return Result<JsonElement>.Failure(NotFound);
        }

        return job.ReportJson is null
            ? Result<JsonElement>.Failure(new Error("migration.no_report", "The migration has not produced a report yet.", ErrorKind.NotFound))
            : Result<JsonElement>.Success(JsonDocument.Parse(job.ReportJson).RootElement.Clone());
    }

    private static MigrationJobDto ToDto(MigrationJob j) => new(
        j.Id, j.SourceUrl, j.SourceVersion, j.Mode.ToString(), j.Status.ToString(), j.Control == MigrationControl.None ? null : j.Control.ToString(), j.CreatedAt, j.StartedAt, j.FinishedAt,
        j.CurrentOperation, j.SpacesTotal, j.SpacesDone, j.PagesTotal, j.PagesDone, j.PagesFailed, j.VersionsDone, j.WarningCount, j.ReportJson is not null,
        j.Status is MigrationStatus.Paused or MigrationStatus.Failed or MigrationStatus.Cancelled or MigrationStatus.CompletedWithErrors);
}

public static class MigrationEndpoints
{
    public static void MapMigrationEndpoints(this RouteGroupBuilder api)
    {
        ArgumentNullException.ThrowIfNull(api);
        var g = api.MapGroup("/migrations/confluence").RequireHostKind(RaaviHostKind.Tenant).RequireAuthorization(RaaviPolicies.TenantAdministrator);

        g.MapGet("", async (MigrationService svc, CancellationToken ct) => Results.Ok(await svc.ListAsync(ct).ConfigureAwait(false)));
        g.MapPost("", async (CreateMigrationRequest body, MigrationService svc, CancellationToken ct) =>
            (await svc.CreateAsync(body, ct).ConfigureAwait(false)).ToHttp(j => Results.Created($"/api/v1/migrations/confluence/{j.Id}", j)));
        g.MapGet("/{id:guid}", async (Guid id, MigrationService svc, CancellationToken ct) => (await svc.GetAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/{id:guid}/start", async (Guid id, StartMigrationRequest? body, MigrationService svc, CancellationToken ct) => (await svc.StartAsync(id, body, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/{id:guid}/pause", async (Guid id, MigrationService svc, CancellationToken ct) => (await svc.PauseAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapPost("/{id:guid}/cancel", async (Guid id, MigrationService svc, CancellationToken ct) => (await svc.CancelAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapGet("/{id:guid}/events", async (Guid id, string? level, int? take, MigrationService svc, CancellationToken ct) =>
            (await svc.EventsAsync(id, level, take ?? 200, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
        g.MapGet("/{id:guid}/report", async (Guid id, MigrationService svc, CancellationToken ct) => (await svc.ReportAsync(id, ct).ConfigureAwait(false)).ToHttp(Results.Ok));
    }
}
