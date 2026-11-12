using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Raavi.Application.Abstractions.Tenancy;
using Raavi.Application.Abstractions.Time;
using Raavi.Infrastructure.Messaging;
using Raavi.Integrations.Persistence;

namespace Raavi.Integrations.Webhooks;

public sealed class IntegrationsOptions
{
    public const string Section = "Raavi:Integrations";

    /// <summary>Hosts webhooks may call (exact name or <c>*.suffix</c>). Empty = webhooks cannot be created or delivered (SSRF guard).</summary>
    public IList<string> AllowedWebhookHosts { get; } = [];

    /// <summary>Hosts issue connectors (GitHub, GitLab, Jira) may call. Empty = connectors cannot be created or used (SSRF guard).</summary>
    public IList<string> AllowedConnectorHosts { get; } = [];

    /// <summary>
    /// When true, a connector with an access token may use <c>http://</c> (on-prem trackers without TLS).
    /// Default false: token-bearing connectors must use https. Host must still be on <see cref="AllowedConnectorHosts"/>.
    /// </summary>
    public bool AllowHttpConnectorTokens { get; set; }

    public int TimeoutSeconds { get; set; } = 10;
}

/// <summary>A webhook call failed in a way that is recorded as a stable code.</summary>
public sealed class WebhookException(string code) : Exception(code)
{
    public string Code { get; } = code;
}

/// <summary>
/// Which hosts webhooks may call (SSRF guard): the installation administrator lists them; loopback, link-local (cloud metadata), multicast and
/// unspecified addresses are refused even when listed and re-checked at connect time after DNS resolution; redirects are never followed.
/// </summary>
public sealed class WebhookHostPolicy(IEnumerable<string> allowedHosts)
{
    private readonly string[] _allowed = [.. allowedHosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0)];

    public bool IsConfigured => _allowed.Length > 0;

    public Uri? TryValidate(string? url, out string? code)
    {
        code = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
        {
            code = "webhook.url_invalid";
            return null;
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (!_allowed.Any(a => a == host || (a.StartsWith("*.", StringComparison.Ordinal) && host.EndsWith(a[1..], StringComparison.Ordinal))))
        {
            code = "webhook.host_not_allowed";
            return null;
        }

        if (IPAddress.TryParse(host, out var ip) && IsForbidden(ip))
        {
            code = "webhook.host_forbidden";
            return null;
        }

        return uri;
    }

    public static bool IsForbidden(IPAddress ip)
    {
        ArgumentNullException.ThrowIfNull(ip);
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any) || ip.IsIPv6LinkLocal || ip.IsIPv6Multicast)
        {
            return true;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return (b[0] == 169 && b[1] == 254) || b[0] >= 224 || b[0] == 0;
        }

        return false;
    }

    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            var allowed = addresses.Where(a => !IsForbidden(a)).ToArray();
            if (allowed.Length == 0)
            {
                throw new WebhookException("host_forbidden");
            }

            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(allowed, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };
}

public static class WebhookHttp
{
    public const string ClientName = "raavi-webhooks";
}

public sealed record SendResult(bool Success, int? HttpStatus, string? Error, int DurationMs)
{
    /// <summary>A client error (other than "request timeout" and "too many requests") will not get better by retrying.</summary>
    public bool Permanent => HttpStatus is >= 400 and < 500 and not (408 or 429);
}

/// <summary>Signs and POSTs one event. The response body is never read or stored; errors are stable codes.</summary>
public sealed class WebhookSender(IHttpClientFactory clients, WebhookHostPolicy policy, IOptions<IntegrationsOptions> options, IClock clock)
{
    public static string Sign(string secret, long unixSeconds, string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(secret));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes($"{unixSeconds.ToString(CultureInfo.InvariantCulture)}.{body}"))).ToLowerInvariant();
    }

    public async Task<SendResult> SendAsync(string url, string secret, string eventType, Guid deliveryId, string body, CancellationToken ct)
    {
        if (policy.TryValidate(url, out _) is not { } uri)
        {
            return new SendResult(false, null, "host_not_allowed", 0); // the policy may have been tightened since the webhook was saved
        }

        var started = clock.UtcNow;
        var t = started.ToUnixTimeSeconds();
        using var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.TryAddWithoutValidation("User-Agent", "Raavi-Webhooks/1");
        request.Headers.TryAddWithoutValidation("X-Raavi-Event", eventType);
        request.Headers.TryAddWithoutValidation("X-Raavi-Delivery", deliveryId.ToString("D"));
        request.Headers.TryAddWithoutValidation("X-Raavi-Signature", $"t={t.ToString(CultureInfo.InvariantCulture)},v1={Sign(secret, t, body)}");
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(options.Value.TimeoutSeconds, 1, 60)));
        try
        {
            using var response = await clients.CreateClient(WebhookHttp.ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            var ms = (int)(clock.UtcNow - started).TotalMilliseconds;
            return status is >= 200 and < 300
                ? new SendResult(true, status, null, ms)
                : new SendResult(false, status, status is >= 300 and < 400 ? "redirect" : status >= 500 ? "http_5xx" : "http_4xx", ms);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new SendResult(false, null, "timeout", (int)(clock.UtcNow - started).TotalMilliseconds);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException || ex.InnerException is WebhookException)
        {
            var code = (ex.InnerException as WebhookException)?.Code ?? "unreachable";
            return new SendResult(false, null, code, (int)(clock.UtcNow - started).TotalMilliseconds);
        }
    }
}

/// <summary>
/// Outbox handler that delivers events to the tenant's webhooks (Phase 11d, ADR-036). At-least-once becomes idempotent through the delivery log
/// (unique per webhook and event): a delivered event is never sent again, a failed one is retried by the outbox with back-off — except permanent
/// client errors, which are recorded and not retried. One failing webhook does not hold back the others. The payload carries identifiers only.
/// </summary>
public sealed class WebhookDispatcher(
    IntegrationsDbContext db,
    WebhookSender sender,
    IDataProtectionProvider protection,
    ITenantContext tenant,
    IClock clock) : IOutboxHandler
{
    public const int MaxWebhooksPerEvent = 50;

    public bool CanHandle(string eventType) => WebhookEvents.IsKnown(eventType);

    public async Task HandleAsync(OutboxEvent outboxEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(outboxEvent);
        var ct = cancellationToken;
        var (data, spaceId) = WebhookPayload.Extract(outboxEvent.PayloadJson);
        var candidates = await db.Webhooks.Where(w => w.Enabled).OrderBy(w => w.CreatedAt).Take(MaxWebhooksPerEvent * 4).ToListAsync(ct).ConfigureAwait(false);
        var targets = candidates.Where(w => w.Events.Contains(outboxEvent.Type, StringComparer.Ordinal) && (w.SpaceId is null || w.SpaceId == spaceId)).Take(MaxWebhooksPerEvent).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var body = WebhookPayload.Build(outboxEvent.Id, outboxEvent.Type, outboxEvent.OccurredAt, tenant.Tenant.Slug, data);
        var retry = false;
        foreach (var w in targets)
        {
            var delivery = await db.WebhookDeliveries.FirstOrDefaultAsync(d => d.WebhookId == w.Id && d.EventId == outboxEvent.Id, ct).ConfigureAwait(false);
            if (delivery is { Status: DeliveryStatus.Succeeded })
            {
                continue; // already delivered
            }

            if (delivery is null)
            {
                delivery = WebhookDelivery.Start(w.Id, outboxEvent.Id, outboxEvent.Type);
                db.WebhookDeliveries.Add(delivery);
            }

            var secret = Unprotect(w);
            var result = secret is null
                ? new SendResult(false, null, "secret_unreadable", 0)
                : await sender.SendAsync(w.Url, secret, outboxEvent.Type, outboxEvent.Id, body, ct).ConfigureAwait(false);
            var now = clock.UtcNow;
            delivery.Record(result.Success ? DeliveryStatus.Succeeded : DeliveryStatus.Failed, result.HttpStatus, result.DurationMs, result.Error, now);
            if (result.Success)
            {
                w.Delivered(now);
            }
            else
            {
                w.Failed(now);
                retry |= !result.Permanent && secret is not null;
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        if (retry)
        {
            throw new InvalidOperationException("webhook delivery failed; the outbox will retry"); // delivered webhooks are skipped on the retry
        }
    }

    private string? Unprotect(Webhook w)
    {
        try
        {
            return protection.CreateProtector(WebhookService.ProtectorPurpose(tenant.Tenant.TenantId)).Unprotect(w.ProtectedSecret);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}

/// <summary>Builds the webhook body. Only identifiers (and a few scalar flags) are taken from an event: no titles, texts or excerpts, so a webhook never reveals content of restricted pages.</summary>
public static class WebhookPayload
{
    private static readonly string[] IdKeys = ["spaceId", "contentId", "actorId", "requestId", "commentId", "threadId"];

    public static (Dictionary<string, object> Data, Guid? SpaceId) Extract(string payloadJson)
    {
        var data = new Dictionary<string, object>();
        Guid? space = null;
        try
        {
            using var doc = JsonDocument.Parse(payloadJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (data, null);
            }

            foreach (var key in IdKeys)
            {
                if (doc.RootElement.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String && Guid.TryParse(v.GetString(), out var id) && id != Guid.Empty)
                {
                    data[key] = id.ToString("D");
                    if (key == "spaceId")
                    {
                        space = id;
                    }
                }
            }

            if (doc.RootElement.TryGetProperty("version", out var ver) && ver.ValueKind == JsonValueKind.Number && ver.TryGetInt32(out var version))
            {
                data["version"] = version;
            }

            if (doc.RootElement.TryGetProperty("status", out var st) && st.ValueKind == JsonValueKind.String && st.GetString() is { Length: > 0 and <= 20 } status && status.All(char.IsLetter))
            {
                data["status"] = status;
            }
        }
        catch (JsonException)
        {
            // a payload we cannot read is delivered with empty data
        }

        return (data, space);
    }

    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    public static string Build(Guid eventId, string type, DateTimeOffset occurredAt, string tenantSlug, IReadOnlyDictionary<string, object> data) =>
        JsonSerializer.Serialize(new { id = eventId, type, occurredAt, tenant = tenantSlug, data }, Web);
}
