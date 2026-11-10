using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Http;
using Raavi.Infrastructure.Tenancy;
using Raavi.Tenancy.Persistence;

namespace Raavi.Tenancy.Application;

/// <summary>
/// Workspace (tenant) deletion, ADR-050: request (access stops, grace period starts) → cancel or purge. The purge removes every
/// tenant-owned row from inside an explicit scope for that tenant (RLS still applies, so no other tenant can be touched), releases
/// the domains, and leaves the <c>Tenants</c> row as a tombstone so the code and slug are never reused. Audit rows are kept.
/// </summary>
public sealed partial class TenantDeletionService(
    TenancyDbContext db,
    TenancyAudit audit,
    ITenantDirectory directory,
    ITenantScopeFactory tenantScopes,
    IAuditActorAccessor actors,
    IClock clock,
    IConfiguration configuration,
    ILogger<TenantDeletionService> logger)
{
    /// <summary>Schemas whose tables are never purged (infrastructure, audit trail). The tenancy tables are listed explicitly below.</summary>
    private static readonly string[] KeptSchemas = ["audit", "system", "hangfire", "sys", "INFORMATION_SCHEMA"];

    /// <summary>Tenant-owned tables of the system schema; <c>Tenants</c>, <c>TenantDomains</c> and the system audit log stay.</summary>
    private static readonly string[] TenancyOwnedTables = ["system.TenantSettings", "system.TenantBranding", "system.TenantLogos"];

    public const int MaxPasses = 8;

    public int GraceDays => Math.Clamp(int.TryParse(configuration["Raavi:Tenants:DeletionGraceDays"], out var d) ? d : 14, 0, 365);

    public async Task<Result<TenantDto>> RequestAsync(Guid tenantId, string? reason, string? confirmCode, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return NotFound();
        }

        if (!Confirmed(tenant.Code, confirmCode))
        {
            return Mismatch();
        }

        var now = clock.UtcNow;
        try
        {
            tenant.RequestDeletion(reason ?? string.Empty, actors.Current.ActorId, now.AddDays(GraceDays), now);
        }
        catch (DomainException ex)
        {
            return Fail(ex);
        }

        audit.System("tenant.deletion_requested", tenant.Id, "tenant", tenant.Id.ToString(), new { reason, scheduledFor = tenant.DeletionScheduledFor });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await directory.InvalidateAsync(tenant.Id, cancellationToken: ct).ConfigureAwait(false);
        return Result<TenantDto>.Success(TenantDto.From(tenant));
    }

    public async Task<Result<TenantDto>> CancelAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return NotFound();
        }

        try
        {
            tenant.CancelDeletion();
        }
        catch (DomainException ex)
        {
            return Fail(ex);
        }

        audit.System("tenant.deletion_cancelled", tenant.Id, "tenant", tenant.Id.ToString());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await directory.InvalidateAsync(tenant.Id, cancellationToken: ct).ConfigureAwait(false);
        return Result<TenantDto>.Success(TenantDto.From(tenant));
    }

    /// <summary>Purge now (the administrator typed the code) — only for a tenant whose deletion was requested.</summary>
    public async Task<Result<TenantDto>> PurgeNowAsync(Guid tenantId, string? confirmCode, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return NotFound();
        }

        return Confirmed(tenant.Code, confirmCode) ? await PurgeAsync(tenantId, ct).ConfigureAwait(false) : Mismatch();
    }

    /// <summary>Tenants whose grace period has ended (used by the daily job).</summary>
    public Task<List<Guid>> DueAsync(CancellationToken ct)
    {
        var now = clock.UtcNow;
        return db.Tenants.AsNoTracking()
            .Where(t => t.Status == TenantStatus.PendingDeletion && t.DeletionScheduledFor != null && t.DeletionScheduledFor <= now)
            .Select(t => t.Id).Take(50).ToListAsync(ct);
    }

    public async Task<Result<TenantDto>> PurgeAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return NotFound();
        }

        if (tenant.Status != TenantStatus.PendingDeletion)
        {
            return Fail(new DomainException("tenant.invalid_transition", $"Cannot purge a tenant in status {tenant.Status}."));
        }

        var snapshot = await directory.FindByIdAsync(tenantId, ct).ConfigureAwait(false);
        if (snapshot is null)
        {
            return NotFound();
        }

        var removed = new Dictionary<string, int>();
        List<string> remaining;
        var scope = tenantScopes.CreateScope(new ResolvedTenant(snapshot.Id, snapshot.Code, snapshot.Slug, TenantResolutionSource.ServicePrincipal, null));
        await using (scope.ConfigureAwait(false))
        {
            var scoped = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            scoped.Database.SetCommandTimeout(900);
            remaining = (await scoped.Database.SqlQueryRaw<string>(TableListSql).ToListAsync(ct).ConfigureAwait(false))
                .Concat(TenancyOwnedTables).Where(IsSafeName).Distinct().ToList();
            for (var pass = 0; pass < MaxPasses && remaining.Count > 0; pass++)
            {
                var blocked = new List<string>();
                foreach (var table in remaining)
                {
                    try
                    {
                        var n = await scoped.Database.ExecuteSqlRawAsync(DeleteSql(table), [new SqlParameter("@t", tenantId)], ct).ConfigureAwait(false);
                        removed[table] = removed.GetValueOrDefault(table) + n;
                    }
                    catch (Exception ex) when (IsForeignKeyViolation(ex))
                    {
                        blocked.Add(table); // a child table is cleared later in the pass; try again
                    }
                }

                remaining = blocked;
            }
        }

        if (remaining.Count > 0)
        {
            LogIncomplete(logger, tenant.Code, string.Join(", ", remaining));
            audit.System("tenant.purge_incomplete", tenant.Id, "tenant", tenant.Id.ToString(), new { remaining });
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return Result<TenantDto>.Failure(new Error("tenant.purge_incomplete", "Some data could not be removed yet; it will be retried.", ErrorKind.Conflict));
        }

        var now = clock.UtcNow;
        foreach (var domain in await db.TenantDomains.Where(d => d.TenantId == tenantId && d.RemovedAt == null).ToListAsync(ct).ConfigureAwait(false))
        {
            domain.Remove(now);
        }

        tenant.MarkDeleted(now);
        audit.System("tenant.purged", tenant.Id, "tenant", tenant.Id.ToString(), new { tables = removed.Count(kv => kv.Value > 0), rows = removed.Values.Sum() });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await directory.InvalidateAsync(tenant.Id, cancellationToken: ct).ConfigureAwait(false);
        var totalRows = removed.Values.Sum();
        LogPurged(logger, tenant.Code, totalRows);
        return Result<TenantDto>.Success(TenantDto.From(tenant));
    }

    private static readonly string TableListSql =
        "SELECT DISTINCT c.TABLE_SCHEMA + '.' + c.TABLE_NAME AS [Value] FROM INFORMATION_SCHEMA.COLUMNS c " +
        "JOIN INFORMATION_SCHEMA.TABLES t ON t.TABLE_SCHEMA = c.TABLE_SCHEMA AND t.TABLE_NAME = c.TABLE_NAME AND t.TABLE_TYPE = 'BASE TABLE' " +
        $"WHERE c.COLUMN_NAME = 'TenantId' AND c.TABLE_SCHEMA NOT IN ({string.Join(", ", KeptSchemas.Select(s => $"'{s}'"))})";

    private static bool IsSafeName(string name) => name.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '.' or '_');

    private static string DeleteSql(string table)
    {
        var parts = table.Split('.');
        return $"DELETE FROM [{parts[0]}].[{parts[1]}] WHERE [TenantId] = @t";
    }

    private static bool IsForeignKeyViolation(Exception ex) =>
        (ex as SqlException ?? ex.InnerException as SqlException)?.Number == 547;

    private static bool Confirmed(string code, string? typed) => string.Equals(code, (typed ?? string.Empty).Trim(), StringComparison.OrdinalIgnoreCase);

    private static Result<TenantDto> Mismatch() => Result<TenantDto>.Failure(new Error("tenant.confirm_mismatch", "Type the workspace code to confirm.", ErrorKind.Validation));

    private static Result<TenantDto> Fail(DomainException ex) =>
        Result<TenantDto>.Failure(new Error(ex.Code, ex.Message, ex.Code == "tenant.invalid_transition" ? ErrorKind.Conflict : ErrorKind.Validation));

    private static Result<TenantDto> NotFound() => Result<TenantDto>.Failure(new Error("tenant.not_found", "Tenant not found.", ErrorKind.NotFound));

    [LoggerMessage(Level = LogLevel.Warning, Message = "Purge of tenant {Code} incomplete; tables left: {Tables}")]
    private static partial void LogIncomplete(ILogger logger, string code, string tables);

    [LoggerMessage(Level = LogLevel.Information, Message = "Tenant {Code} purged ({Rows} rows)")]
    private static partial void LogPurged(ILogger logger, string code, int rows);
}

/// <summary>Daily system job: purges the workspaces whose grace period has ended.</summary>
public sealed partial class TenantPurgeJob(TenantDeletionService deletion, ILogger<TenantPurgeJob> logger)
{
    public const string RecurringJobId = "tenant-purge";

    public async Task RunAsync(CancellationToken ct)
    {
        foreach (var id in await deletion.DueAsync(ct).ConfigureAwait(false))
        {
            var result = await deletion.PurgeAsync(id, ct).ConfigureAwait(false);
            if (!result.IsSuccess)
            {
                LogFailed(logger, id, result.Error!.Code);
            }
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Purge of tenant {TenantId} failed: {Code}")]
    private static partial void LogFailed(ILogger logger, Guid tenantId, string code);
}
