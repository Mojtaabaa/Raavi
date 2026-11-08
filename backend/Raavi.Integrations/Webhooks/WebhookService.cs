using System.Security.Cryptography;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Raavi.Application.Abstractions.Identity;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Application.Common;
using Raavi.Infrastructure.Http;
using Raavi.Integrations.Persistence;

namespace Raavi.Integrations.Webhooks;

public sealed record WebhookDto(
    Guid Id, string Name, string Url, string[] Events, Guid? SpaceId, bool Enabled, int ConsecutiveFailures, string? DisabledReason, DateTimeOffset? LastSuccessAt, DateTimeOffset CreatedAt, int Revision);

/// <summary>Returned once, when a webhook is created or its secret rotated: the only time the signing secret is visible.</summary>
public sealed record WebhookWithSecretDto(WebhookDto Webhook, string Secret);

public sealed record WebhookRequest(string? Name, string? Url, string[]? Events, Guid? SpaceId, bool? Enabled);

public sealed record DeliveryDto(Guid Id, Guid EventId, string EventType, string Status, int? HttpStatus, int Attempts, int DurationMs, string? Error, DateTimeOffset At);

public sealed record WebhookTestDto(bool Success, int? HttpStatus, string? Error, int DurationMs);

public sealed record WebhookCatalogDto(string[] Events, bool HostPolicyConfigured);

/// <summary>Administration of the tenant's webhooks (Phase 11d, ADR-036). Delivery itself is <see cref="WebhookDispatcher"/>.</summary>
public sealed class WebhookService(IntegrationsDbContext db, WebhookHostPolicy policy, WebhookSender sender, IDataProtectionProvider protection, ITenantContext tenant, ICurrentUser current, IClock clock)
{
    public const int MaxWebhooksPerTenant = 20;

    public static readonly Error NotFound = new("webhook.not_found", "Webhook not found.", ErrorKind.NotFound);
    public static readonly Error TooMany = new("webhook.too_many", $"A workspace can have at most {MaxWebhooksPerTenant} webhooks.", ErrorKind.Conflict);

    public static string ProtectorPurpose(Guid tenantId) => $"Raavi.Webhooks.Secret.v1.{tenantId:N}";

    private IDataProtector Protector => protection.CreateProtector(ProtectorPurpose(tenant.Tenant.TenantId));

    public WebhookCatalogDto Catalog() => new([.. WebhookEvents.All], policy.IsConfigured);

    public async Task<IReadOnlyList<WebhookDto>> ListAsync(CancellationToken ct) =>
        (await db.Webhooks.AsNoTracking().OrderBy(w => w.Name).Take(MaxWebhooksPerTenant).ToListAsync(ct).ConfigureAwait(false)).Select(ToDto).ToList();

    public async Task<Result<WebhookDto>> GetAsync(Guid id, CancellationToken ct)
    {
        var w = await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        return w is null ? Result<WebhookDto>.Failure(NotFound) : Result<WebhookDto>.Success(ToDto(w));
    }

    public async Task<Result<WebhookWithSecretDto>> CreateAsync(WebhookRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var url = ValidateUrl(request.Url);
        if (!url.IsSuccess)
        {
            return Result<WebhookWithSecretDto>.Failure(url.Error!);
        }

        if (await db.Webhooks.CountAsync(ct).ConfigureAwait(false) >= MaxWebhooksPerTenant)
        {
            return Result<WebhookWithSecretDto>.Failure(TooMany);
        }

        var secret = NewSecret();
        var w = Webhook.Create(request.Name, url.Value, request.Events, request.SpaceId, Protector.Protect(secret), current.UserId, clock.UtcNow);
        db.Webhooks.Add(w);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<WebhookWithSecretDto>.Success(new WebhookWithSecretDto(ToDto(w), secret));
    }

    public async Task<Result<WebhookDto>> UpdateAsync(Guid id, int expectedRevision, WebhookRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        var w = await db.Webhooks.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (w is null)
        {
            return Result<WebhookDto>.Failure(NotFound);
        }

        if (w.Revision != expectedRevision)
        {
            return Result<WebhookDto>.Failure(ETags.PreconditionFailed);
        }

        var url = ValidateUrl(request.Url);
        if (!url.IsSuccess)
        {
            return Result<WebhookDto>.Failure(url.Error!);
        }

        w.Update(request.Name, url.Value, request.Events, request.SpaceId, request.Enabled ?? w.Enabled, clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<WebhookDto>.Success(ToDto(w));
    }

    public async Task<Result<WebhookWithSecretDto>> RotateSecretAsync(Guid id, CancellationToken ct)
    {
        var w = await db.Webhooks.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (w is null)
        {
            return Result<WebhookWithSecretDto>.Failure(NotFound);
        }

        var secret = NewSecret();
        w.RotateSecret(Protector.Protect(secret), clock.UtcNow);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result<WebhookWithSecretDto>.Success(new WebhookWithSecretDto(ToDto(w), secret));
    }

    public async Task<Result> DeleteAsync(Guid id, CancellationToken ct)
    {
        var w = await db.Webhooks.FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (w is null)
        {
            return Result.Failure(NotFound);
        }

        db.WebhookDeliveries.RemoveRange(await db.WebhookDeliveries.Where(d => d.WebhookId == id).ToListAsync(ct).ConfigureAwait(false));
        db.Webhooks.Remove(w);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Result.Success();
    }

    /// <summary>Sends a signed <c>ping</c> right now (no log entry) so an administrator can check the address and the signature handling.</summary>
    public async Task<Result<WebhookTestDto>> TestAsync(Guid id, CancellationToken ct)
    {
        var w = await db.Webhooks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id, ct).ConfigureAwait(false);
        if (w is null)
        {
            return Result<WebhookTestDto>.Failure(NotFound);
        }

        string secret;
        try
        {
            secret = Protector.Unprotect(w.ProtectedSecret);
        }
        catch (CryptographicException)
        {
            return Result<WebhookTestDto>.Success(new WebhookTestDto(false, null, "secret_unreadable", 0));
        }

        var now = clock.UtcNow;
        var eventId = Guid.NewGuid();
        var body = WebhookPayload.Build(eventId, WebhookEvents.Ping, now, tenant.Tenant.Slug, new Dictionary<string, object>());
        var result = await sender.SendAsync(w.Url, secret, WebhookEvents.Ping, eventId, body, ct).ConfigureAwait(false);
        return Result<WebhookTestDto>.Success(new WebhookTestDto(result.Success, result.HttpStatus, result.Error, result.DurationMs));
    }

    public async Task<Result<IReadOnlyList<DeliveryDto>>> DeliveriesAsync(Guid id, int take, CancellationToken ct)
    {
        if (!await db.Webhooks.AnyAsync(x => x.Id == id, ct).ConfigureAwait(false))
        {
            return Result<IReadOnlyList<DeliveryDto>>.Failure(NotFound);
        }

        var rows = await db.WebhookDeliveries.AsNoTracking().Where(d => d.WebhookId == id).OrderByDescending(d => d.At).Take(Math.Clamp(take, 1, 100)).ToListAsync(ct).ConfigureAwait(false);
        return Result<IReadOnlyList<DeliveryDto>>.Success(rows.Select(d => new DeliveryDto(d.Id, d.EventId, d.EventType, d.Status.ToString(), d.HttpStatus, d.Attempts, d.DurationMs, d.Error, d.At)).ToList());
    }

    private Result<string> ValidateUrl(string? url)
    {
        var uri = policy.TryValidate(url, out var code);
        return uri is null
            ? Result<string>.Failure(new Error(code!, code switch
            {
                "webhook.host_not_allowed" => "This host is not in the webhook hosts allowed by the administrator of the installation.",
                "webhook.host_forbidden" => "This address is not allowed for a webhook.",
                _ => "The address must be an http(s) URL without credentials or a fragment.",
            }, ErrorKind.Validation))
            : Result<string>.Success(uri.ToString());
    }

    private static string NewSecret() => "whsec_" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static WebhookDto ToDto(Webhook w) => new(w.Id, w.Name, w.Url, [.. w.Events], w.SpaceId, w.Enabled, w.ConsecutiveFailures, w.DisabledReason, w.LastSuccessAt, w.CreatedAt, w.Revision);
}
