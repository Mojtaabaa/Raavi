using System.Net;

namespace Raavi.Tests.Infrastructure;

/// <summary>One request as GitHub/GitLab/Jira would see it.</summary>
public sealed record TrackerRequest(Uri Url, IReadOnlyDictionary<string, string> Headers, string Method = "GET");

/// <summary>In-process issue tracker registered as the primary handler of the connector HTTP client; the test scripts the answer.</summary>
public sealed class FakeIssueTracker : HttpMessageHandler
{
    private readonly List<TrackerRequest> _received = [];

    public IReadOnlyList<TrackerRequest> Received
    {
        get
        {
            lock (_received)
            {
                return [.. _received];
            }
        }
    }

    public Func<TrackerRequest, (HttpStatusCode Status, string Body)> Respond { get; set; } = _ => (HttpStatusCode.NotFound, "{}");

    public bool Unreachable { get; set; }

    public void Reset()
    {
        lock (_received)
        {
            _received.Clear();
        }

        Respond = _ => (HttpStatusCode.NotFound, "{}");
        Unreachable = false;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = request.Headers.ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var seen = new TrackerRequest(request.RequestUri!, headers, request.Method.Method);
        lock (_received)
        {
            _received.Add(seen);
        }

        if (Unreachable)
        {
            throw new HttpRequestException("connection refused");
        }

        var (status, body) = Respond(seen);
        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json") });
    }
}
