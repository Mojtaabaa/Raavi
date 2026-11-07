using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Infrastructure.Tenancy;
using Raavi.Tenancy.Domain;
using Raavi.Tenancy.Persistence;

namespace Raavi.Tenancy.Application;

/// <summary>
/// Reads and updates tenant-owned configuration. All reads/writes go through the tenant-filtered context of the
/// current scope — on a tenant host that is the resolved tenant; for system administrators it is an explicit
/// tenant scope opened for the tenant in the route (<see cref="ForTenantAsync{T}"/>).
/// </summary>
public sealed class TenantConfigurationService(TenancyDbContext db, TenancyAudit audit, ITenantContext tenantContext)
{
    public async Task<TenantPublicContextDto> GetPublicContextAsync(CancellationToken ct)
    {
        var tenantId = tenantContext.Tenant.TenantId;
        var tenant = await db.Tenants.AsNoTracking().FirstAsync(t => t.Id == tenantId, ct).ConfigureAwait(false);
        var settings = await db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var branding = await db.TenantBranding.AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return new TenantPublicContextDto(
            tenant.Slug,
            new LocalizedText(tenant.DisplayNameFa, tenant.DisplayNameEn),
            settings?.DefaultLocale ?? "fa",
            settings?.TimeZone ?? "Asia/Tehran",
            settings?.Calendar ?? "persian",
            ToDto(branding));
    }

    public async Task<Result<TenantSettingsDto>> GetSettingsAsync(CancellationToken ct)
    {
        var s = await db.TenantSettings.AsNoTracking().FirstOrDefaultAsync(ct).ConfigureAwait(false);
        return s is null ? Result<TenantSettingsDto>.Failure(NotProvisioned) : Result<TenantSettingsDto>.Success(ToDto(s));
    }

    public async Task<Result<TenantSettingsDto>> UpdateSettingsAsync(UpdateSettingsRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var s = await db.TenantSettings.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (s is null)
        {
            return Result<TenantSettingsDto>.Failure(NotProvisioned);
        }

        try
        {
            s.Update(request.DefaultLocale, request.TimeZone, request.Calendar, request.StorageQuotaBytes);
        }
        catch (DomainException ex)
        {
            return Result<TenantSettingsDto>.Failure(new Error(ex.Code, ex.Message));
        }

        audit.Tenant("settings.updated", "tenant_settings", s.Id.ToString(), new { request.DefaultLocale, request.TimeZone, request.Calendar, request.StorageQuotaBytes });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TenantSettingsDto>.Success(ToDto(s));
    }

    public async Task<Result<TenantBrandingDto>> UpdateBrandingAsync(UpdateBrandingRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var b = await db.TenantBranding.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (b is null)
        {
            return Result<TenantBrandingDto>.Failure(NotProvisioned);
        }

        try
        {
            b.Update(request.PrimaryColor, request.SecondaryColor, request.WelcomeMessageFa, request.WelcomeMessageEn);
        }
        catch (DomainException ex)
        {
            return Result<TenantBrandingDto>.Failure(new Error(ex.Code, ex.Message));
        }

        audit.Tenant("branding.updated", "tenant_branding", b.Id.ToString());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TenantBrandingDto>.Success(ToDto(b));
    }

    /// <summary>Runs <paramref name="action"/> inside an explicit scope bound to <paramref name="tenantId"/> (system administration).</summary>
    public static async Task<Result<T>> ForTenantAsync<T>(IServiceProvider services, Guid tenantId, Func<TenantConfigurationService, Task<Result<T>>> action, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(action);
        var directory = services.GetRequiredService<ITenantDirectory>();
        var tenant = await directory.FindByIdAsync(tenantId, ct).ConfigureAwait(false);
        if (tenant is null)
        {
            return Result<T>.Failure(new Error("tenant.not_found", "Tenant not found.", ErrorKind.NotFound));
        }

        var scope = services.GetRequiredService<ITenantScopeFactory>()
            .CreateScope(new ResolvedTenant(tenant.Id, tenant.Code, tenant.Slug, TenantResolutionSource.ServicePrincipal, null));
        await using (scope.ConfigureAwait(false))
        {
            return await action(scope.ServiceProvider.GetRequiredService<TenantConfigurationService>()).ConfigureAwait(false);
        }
    }

    private static TenantSettingsDto ToDto(TenantSettings s) => new(s.DefaultLocale, s.TimeZone, s.Calendar, s.StorageQuotaBytes);

    private static TenantBrandingDto ToDto(TenantBranding? b) => new(b?.PrimaryColor, b?.SecondaryColor, b?.WelcomeMessageFa, b?.WelcomeMessageEn);

    private static readonly Error NotProvisioned = new("tenant.not_provisioned", "Tenant configuration is not provisioned.", ErrorKind.Conflict);
}
