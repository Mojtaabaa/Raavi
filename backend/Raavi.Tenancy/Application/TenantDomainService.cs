using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Domain.Common;
using Raavi.Tenancy.Domain;
using Raavi.Tenancy.Persistence;
using Raavi.Tenancy.Resolution;

namespace Raavi.Tenancy.Application;

public sealed class DomainVerificationOptions
{
    public const string Section = "Raavi:Security:CustomDomains";

    public string VerificationRecordPrefix { get; set; } = "_raavi-verify";

    public int MaxDomainsPerTenant { get; set; } = 10;

    /// <summary>A removed host cannot be claimed by a <i>different</i> tenant during this period (domain takeover protection).</summary>
    public int QuarantineDays { get; set; } = 30;
}

/// <summary>Custom domain management and DNS TXT verification (MULTI_TENANT_ARCHITECTURE.md §5).</summary>
public sealed class TenantDomainService(
    TenancyDbContext db,
    TenancyAudit audit,
    ITenantDirectory directory,
    IDnsTxtResolver dns,
    IClock clock,
    IOptions<HostingOptions> hosting,
    IOptions<DomainVerificationOptions> options)
{
    public async Task<Result<IReadOnlyList<TenantDomainDto>>> ListAsync(Guid tenantId, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<TenantDomainDto>>.Failure(TenantNotFound);
        }

        var domains = await db.TenantDomains.AsNoTracking().Where(d => d.TenantId == tenantId && d.RemovedAt == null).OrderBy(d => d.Host).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<TenantDomainDto>>.Success(domains.Select(ToDto).ToList());
    }

    public async Task<Result<TenantDomainDto>> AddAsync(Guid tenantId, string rawHost, CancellationToken ct)
    {
        if (!await db.Tenants.AnyAsync(t => t.Id == tenantId, ct).ConfigureAwait(false))
        {
            return Result<TenantDomainDto>.Failure(TenantNotFound);
        }

        if (!TenantHost.TryNormalize(rawHost, out var host) || !host.Contains('.', StringComparison.Ordinal))
        {
            return Result<TenantDomainDto>.Failure(new Error("domain.invalid", "Not a valid domain name."));
        }

        if (hosting.Value.IsPlatformHost(host))
        {
            return Result<TenantDomainDto>.Failure(new Error("domain.platform_host", "Platform host names cannot be used as custom domains."));
        }

        var live = await db.TenantDomains.Where(d => d.RemovedAt == null && (d.Host == host || d.TenantId == tenantId)).ToListAsync(ct).ConfigureAwait(false);
        if (live.Any(d => d.Host == host))
        {
            return Result<TenantDomainDto>.Failure(new Error("domain.taken", "This domain is already registered.", ErrorKind.Conflict));
        }

        if (live.Count(d => d.TenantId == tenantId) >= options.Value.MaxDomainsPerTenant)
        {
            return Result<TenantDomainDto>.Failure(new Error("domain.limit_reached", "Maximum number of domains reached.", ErrorKind.Conflict));
        }

        var quarantineStart = clock.UtcNow.AddDays(-options.Value.QuarantineDays);
        var quarantined = await db.TenantDomains.AnyAsync(d => d.Host == host && d.TenantId != tenantId && d.RemovedAt != null && d.RemovedAt > quarantineStart, ct).ConfigureAwait(false);
        if (quarantined)
        {
            return Result<TenantDomainDto>.Failure(new Error("domain.quarantined", "This domain was recently used by another workspace and is temporarily unavailable.", ErrorKind.Conflict));
        }

        var domain = TenantDomain.Create(tenantId, host, clock.UtcNow);
        db.TenantDomains.Add(domain);
        audit.System("domain.added", tenantId, "tenant_domain", domain.Id.ToString(), new { host });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TenantDomainDto>.Success(ToDto(domain));
    }

    public async Task<Result<TenantDomainDto>> VerifyAsync(Guid tenantId, Guid domainId, CancellationToken ct)
    {
        var domain = await Find(tenantId, domainId, ct).ConfigureAwait(false);
        if (domain is null)
        {
            return Result<TenantDomainDto>.Failure(DomainNotFound);
        }

        await CheckAsync(domain, ct).ConfigureAwait(false);
        return domain.Status == TenantDomainStatus.Verified
            ? Result<TenantDomainDto>.Success(ToDto(domain))
            : Result<TenantDomainDto>.Failure(new Error("domain.verification_failed", $"TXT record {domain.TxtRecordName(options.Value.VerificationRecordPrefix)} with value '{domain.ExpectedTxtValue}' was not found."));
    }

    /// <summary>Runs one DNS check, persists the outcome and invalidates the resolution cache for the host.</summary>
    public async Task CheckAsync(TenantDomain domain, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(domain);
        var before = domain.Status;
        IReadOnlyList<string> values;
        try
        {
            values = await dns.QueryTxtAsync(domain.TxtRecordName(options.Value.VerificationRecordPrefix), ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            values = [];
        }

        if (values.Any(v => string.Equals(v.Trim(), domain.ExpectedTxtValue, StringComparison.Ordinal)))
        {
            domain.RecordVerificationSuccess(clock.UtcNow);
        }
        else
        {
            domain.RecordVerificationFailure(clock.UtcNow);
        }

        if (before != domain.Status)
        {
            audit.System(domain.Status == TenantDomainStatus.Verified ? "domain.verified" : "domain.verification_lost", domain.TenantId, "tenant_domain", domain.Id.ToString(), new { domain.Host });
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await directory.InvalidateAsync(domain.TenantId, [domain.Host], ct).ConfigureAwait(false);
    }

    public async Task<Result<TenantDomainDto>> MakePrimaryAsync(Guid tenantId, Guid domainId, CancellationToken ct)
    {
        var domain = await Find(tenantId, domainId, ct).ConfigureAwait(false);
        if (domain is null)
        {
            return Result<TenantDomainDto>.Failure(DomainNotFound);
        }

        try
        {
            domain.MakePrimary();
        }
        catch (DomainException ex)
        {
            return Result<TenantDomainDto>.Failure(new Error(ex.Code, ex.Message, ErrorKind.Conflict));
        }

        var others = await db.TenantDomains.Where(d => d.TenantId == tenantId && d.Id != domainId && d.IsPrimary).ToListAsync(ct).ConfigureAwait(false);
        others.ForEach(d => d.ClearPrimary());
        audit.System("domain.primary_set", tenantId, "tenant_domain", domain.Id.ToString(), new { domain.Host });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<TenantDomainDto>.Success(ToDto(domain));
    }

    public async Task<Result> RemoveAsync(Guid tenantId, Guid domainId, CancellationToken ct)
    {
        var domain = await Find(tenantId, domainId, ct).ConfigureAwait(false);
        if (domain is null)
        {
            return Result.Failure(DomainNotFound);
        }

        domain.Remove(clock.UtcNow);
        audit.System("domain.removed", tenantId, "tenant_domain", domain.Id.ToString(), new { domain.Host });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await directory.InvalidateAsync(tenantId, [domain.Host], ct).ConfigureAwait(false);
        return Result.Success();
    }

    private Task<TenantDomain?> Find(Guid tenantId, Guid domainId, CancellationToken ct) =>
        db.TenantDomains.FirstOrDefaultAsync(d => d.Id == domainId && d.TenantId == tenantId && d.RemovedAt == null, ct);

    private TenantDomainDto ToDto(TenantDomain d) =>
        new(d.Id, d.Host, d.IsPrimary, d.Status, d.TxtRecordName(options.Value.VerificationRecordPrefix), d.ExpectedTxtValue, d.CreatedAt, d.VerifiedAt, d.LastCheckedAt, d.ConsecutiveFailures);

    private static readonly Error TenantNotFound = new("tenant.not_found", "Tenant not found.", ErrorKind.NotFound);
    private static readonly Error DomainNotFound = new("domain.not_found", "Domain not found.", ErrorKind.NotFound);
}
