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

    private static TenantBrandingDto ToDto(TenantBranding? b) => new(b?.PrimaryColor, b?.SecondaryColor, b?.WelcomeMessageFa, b?.WelcomeMessageEn, b?.LogoVersion ?? 0);

    public const int MaxLogoBytes = 256 * 1024;

    private static readonly Error LogoTooLarge = new("logo.too_large", "The logo is too large (256 KB at most).", ErrorKind.Validation);

    /// <summary>Content type of a PNG, JPEG or WebP picture decided from its bytes (never from the declared type); null for anything else.</summary>
    public static string? SniffImage(ReadOnlySpan<byte> d)
    {
        if (d.Length >= 8 && d[0] == 0x89 && d[1] == 'P' && d[2] == 'N' && d[3] == 'G')
        {
            return "image/png";
        }

        if (d.Length >= 4 && d[0] == 0xFF && d[1] == 0xD8 && d[2] == 0xFF)
        {
            return "image/jpeg";
        }

        if (d.Length >= 16 && d[0] == 'R' && d[1] == 'I' && d[2] == 'F' && d[3] == 'F' && d[8] == 'W' && d[9] == 'E' && d[10] == 'B' && d[11] == 'P')
        {
            return "image/webp";
        }

        return null;
    }

    public async Task<Result<int>> SetLogoAsync(Stream content, long declaredLength, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (declaredLength > MaxLogoBytes)
        {
            return Result<int>.Failure(LogoTooLarge);
        }

        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await content.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + read > MaxLogoBytes)
            {
                return Result<int>.Failure(LogoTooLarge);
            }

            buffer.Write(chunk, 0, read);
        }

        var bytes = buffer.ToArray();
        var type = SniffImage(bytes);
        if (type is null)
        {
            return Result<int>.Failure(new Error("logo.type_invalid", "Only PNG, JPEG or WebP pictures are accepted.", ErrorKind.Validation));
        }

        var branding = await db.TenantBranding.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (branding is null)
        {
            return Result<int>.Failure(NotProvisioned);
        }

        var logo = await db.TenantLogos.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (logo is null)
        {
            logo = TenantLogo.Create(type, bytes);
            db.TenantLogos.Add(logo);
        }
        else
        {
            logo.Replace(type, bytes);
        }

        branding.SetLogoVersion(logo.Version);
        audit.Tenant("branding.logo_changed", "tenant_branding", branding.Id.ToString());
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<int>.Success(logo.Version);
    }

    public async Task<Result<bool>> RemoveLogoAsync(CancellationToken ct)
    {
        var logo = await db.TenantLogos.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        if (logo is not null)
        {
            db.TenantLogos.Remove(logo);
        }

        var branding = await db.TenantBranding.FirstOrDefaultAsync(ct).ConfigureAwait(false);
        branding?.SetLogoVersion(0);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<bool>.Success(true);
    }

    public async Task<LogoFile?> GetLogoAsync(CancellationToken ct) =>
        await db.TenantLogos.AsNoTracking().Select(l => new LogoFile(l.Data, l.ContentType, l.Version)).FirstOrDefaultAsync(ct).ConfigureAwait(false);

    private static readonly Error NotProvisioned = new("tenant.not_provisioned", "Tenant configuration is not provisioned.", ErrorKind.Conflict);
}
