using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Tenancy;
using Raavi.Tenancy.Domain;
using Raavi.Tenancy.Persistence;

namespace Raavi.Tenancy.Application;

/// <summary>
/// System-host tenant lifecycle (MULTI_TENANT_ARCHITECTURE.md §7). Tenant-owned rows (settings, branding) are
/// written inside an explicit tenant scope for that tenant, never through a bypass.
/// </summary>
public sealed class TenantAdministrationService(
    TenancyDbContext db,
    TenancyAudit audit,
    ITenantDirectory directory,
    ITenantScopeFactory tenantScopes,
    IClock clock,
    IConfiguration configuration)
{
    public async Task<Result<TenantDto>> CreateAsync(CreateTenantRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        Tenant tenant;
        try
        {
            tenant = Tenant.Create(request.Code, request.Slug, request.DisplayNameFa, request.DisplayNameEn, clock.UtcNow);
            // Validate settings before anything is written.
            _ = TenantSettings.CreateDefault(Locale(request.DefaultLocale), TimeZone(request.TimeZone), Calendar(request.Calendar));
        }
        catch (DomainException ex)
        {
            return Result<TenantDto>.Failure(new Error(ex.Code, ex.Message));
        }

        if (await db.Tenants.AnyAsync(t => t.Code == tenant.Code, ct).ConfigureAwait(false))
        {
            return Result<TenantDto>.Failure(new Error("tenant.code_taken", "Tenant code is already in use.", ErrorKind.Conflict));
        }

        if (await db.Tenants.AnyAsync(t => t.Slug == tenant.Slug, ct).ConfigureAwait(false))
        {
            return Result<TenantDto>.Failure(new Error("tenant.slug_taken", "Tenant slug is already in use.", ErrorKind.Conflict));
        }

        db.Tenants.Add(tenant);
        audit.System("tenant.created", tenant.Id, "tenant", tenant.Id.ToString(), new { tenant.Code, tenant.Slug });
        try
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqlException { Number: 2601 or 2627 })
        {
            return Result<TenantDto>.Failure(new Error("tenant.code_or_slug_taken", "Tenant code or slug is already in use.", ErrorKind.Conflict));
        }

        return await ProvisionAsync(tenant.Id, request.DefaultLocale, request.TimeZone, request.Calendar, ct).ConfigureAwait(false);
    }

    /// <summary>Idempotent: seeds tenant-owned defaults inside the tenant's scope, then activates. Safe to retry after a partial failure.</summary>
    public async Task<Result<TenantDto>> ProvisionAsync(Guid tenantId, string? locale, string? timeZone, string? calendar, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return NotFound();
        }

        if (tenant.Status != TenantStatus.Provisioning)
        {
            return Result<TenantDto>.Failure(new Error("tenant.already_provisioned", "The tenant is already provisioned.", ErrorKind.Conflict));
        }

        var scope = tenantScopes.CreateScope(new ResolvedTenant(tenant.Id, tenant.Code, tenant.Slug, TenantResolutionSource.ServicePrincipal, null));
        await using (scope.ConfigureAwait(false))
        {
            var tenantDb = scope.ServiceProvider.GetRequiredService<TenancyDbContext>();
            if (!await tenantDb.TenantSettings.AnyAsync(ct).ConfigureAwait(false))
            {
                tenantDb.TenantSettings.Add(TenantSettings.CreateDefault(Locale(locale), TimeZone(timeZone), Calendar(calendar)));
            }

            if (!await tenantDb.TenantBranding.AnyAsync(ct).ConfigureAwait(false))
            {
                tenantDb.TenantBranding.Add(TenantBranding.CreateEmpty());
            }

            scope.ServiceProvider.GetRequiredService<TenancyAudit>().Tenant("tenant.provisioned", "tenant", tenant.Id.ToString());
            await tenantDb.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        tenant.Activate(clock.UtcNow);
        audit.System("tenant.activated", tenant.Id, "tenant", tenant.Id.ToString(), new { reason = "provisioned" });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await directory.InvalidateAsync(tenant.Id, cancellationToken: ct).ConfigureAwait(false);
        return Result<TenantDto>.Success(TenantDto.From(tenant));
    }

    public Task<Result<TenantDto>> ActivateAsync(Guid tenantId, CancellationToken ct) =>
        Transition(tenantId, "tenant.activated", null, (t, now) => t.Activate(now), ct);

    public Task<Result<TenantDto>> SuspendAsync(Guid tenantId, string? reason, CancellationToken ct) =>
        Transition(tenantId, "tenant.suspended", reason, (t, now) => t.Suspend(reason ?? string.Empty, now), ct);

    public Task<Result<TenantDto>> DeactivateAsync(Guid tenantId, string? reason, CancellationToken ct) =>
        Transition(tenantId, "tenant.deactivated", reason, (t, now) => t.Deactivate(reason ?? string.Empty, now), ct);

    public async Task<Result<TenantDto>> GetAsync(Guid tenantId, CancellationToken ct)
    {
        var tenant = await db.Tenants.AsNoTracking().FirstOrDefaultAsync(t => t.Id == tenantId, ct).ConfigureAwait(false);
        return tenant is null ? NotFound() : Result<TenantDto>.Success(TenantDto.From(tenant));
    }

    public async Task<PagedResult<TenantDto>> ListAsync(TenantStatus? status, int skip, int take, CancellationToken ct)
    {
        skip = Math.Max(0, skip);
        take = Math.Clamp(take, 1, 100);
        var query = db.Tenants.AsNoTracking();
        if (status is not null)
        {
            query = query.Where(t => t.Status == status);
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query.OrderBy(t => t.Code).Skip(skip).Take(take).ToListAsync(ct).ConfigureAwait(false);
        return new PagedResult<TenantDto>(items.Select(TenantDto.From).ToList(), total, skip, take);
    }

    private async Task<Result<TenantDto>> Transition(Guid tenantId, string action, string? reason, Action<Tenant, DateTimeOffset> apply, CancellationToken ct)
    {
        var tenant = await db.Tenants.FirstOrDefaultAsync(t => t.Id == tenantId, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return NotFound();
        }

        try
        {
            apply(tenant, clock.UtcNow);
        }
        catch (DomainException ex)
        {
            return Result<TenantDto>.Failure(new Error(ex.Code, ex.Message, ex.Code == "tenant.invalid_transition" ? ErrorKind.Conflict : ErrorKind.Validation));
        }

        audit.System(action, tenant.Id, "tenant", tenant.Id.ToString(), new { reason });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await directory.InvalidateAsync(tenant.Id, cancellationToken: ct).ConfigureAwait(false);
        return Result<TenantDto>.Success(TenantDto.From(tenant));
    }

    private static Result<TenantDto> NotFound() => Result<TenantDto>.Failure(new Error("tenant.not_found", "Tenant not found.", ErrorKind.NotFound));

    private string Locale(string? value) => string.IsNullOrWhiteSpace(value) ? DefaultLocale() : value.Trim();

    private string TimeZone(string? value) => string.IsNullOrWhiteSpace(value) ? configuration["Raavi:Defaults:TimeZone"] ?? "Asia/Tehran" : value.Trim();

    private string Calendar(string? value) => string.IsNullOrWhiteSpace(value) ? (configuration["Raavi:Defaults:Calendar"] == "gregorian" ? "gregorian" : "persian") : value.Trim();

    private string DefaultLocale()
    {
        var culture = configuration["Raavi:Defaults:Culture"] ?? "fa-IR";
        return culture.StartsWith("en", StringComparison.OrdinalIgnoreCase) ? "en" : "fa";
    }
}
