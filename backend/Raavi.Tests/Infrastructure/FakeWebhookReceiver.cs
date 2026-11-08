using System.Net;

namespace Raavi.Tests.Infrastructure;

/// <summary>A webhook request as the receiving system sees it.</summary>
public sealed record ReceivedHook(Uri Url, string Body, IReadOnlyDictionary<string, string> Headers);

/// <summary>In-process webhook receiver registered as the primary handler of the webhook HTTP client; answers with a scripted status.</summary>
public sealed class FakeWebhookReceiver : HttpMessageHandler
{
    private readonly List<ReceivedHook> _received = [];

    public IReadOnlyList<ReceivedHook> Received
    {
        get
        {
            lock (_received)
            {
                return [.. _received];
            }
        }
    }

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    /// <summary>Throws instead of answering (connection refused / timeout).</summary>
    public bool Unreachable { get; set; }

    public void Reset()
    {
        lock (_received)
        {
            _received.Clear();
        }

        Status = HttpStatusCode.OK;
        Unreachable = false;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        lock (_received)
        {
            _received.Add(new ReceivedHook(request.RequestUri!, body, headers));
        }

        if (Unreachable)
        {
            throw new HttpRequestException("connection refused");
        }

        return new HttpResponseMessage(Status) { Content = new StringContent("secret receiver detail") };
    }
}
