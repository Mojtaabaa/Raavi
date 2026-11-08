using System.Net;
using System.Text;
using System.Text.Json;

namespace Raavi.Tests.Infrastructure;

/// <summary>A recorded call to the fake model server.</summary>
public sealed record AiCall(Uri Url, string? Authorization, JsonElement Body);

/// <summary>
/// In-process OpenAI-compatible model server. Records every request (so tests can prove what was — and was not — sent to the model) and
/// answers with a scripted status and text. Registered as the primary handler of the AI HTTP client.
/// </summary>
public sealed class FakeAiProvider : HttpMessageHandler
{
    private readonly List<AiCall> _calls = [];

    public IReadOnlyList<AiCall> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;

    public string Reply { get; set; } = "A short summary.";

    /// <summary>When set, the response omits the <c>usage</c> block (some local servers do).</summary>
    public bool OmitUsage { get; set; }

    public int PromptTokens { get; set; } = 120;

    public int CompletionTokens { get; set; } = 30;

    public void Reset()
    {
        lock (_calls)
        {
            _calls.Clear();
        }

        Status = HttpStatusCode.OK;
        Reply = "A short summary.";
        OmitUsage = false;
        PromptTokens = 120;
        CompletionTokens = 30;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_calls)
        {
            _calls.Add(new AiCall(request.RequestUri!, request.Headers.Authorization?.ToString(), JsonDocument.Parse(text).RootElement.Clone()));
        }

        if (Status != HttpStatusCode.OK)
        {
            return new HttpResponseMessage(Status) { Content = new StringContent("{\"error\":{\"message\":\"secret provider detail\"}}", Encoding.UTF8, "application/json") };
        }

        var payload = OmitUsage
            ? new { choices = new[] { new { message = new { role = "assistant", content = Reply } } } } as object
            : new { choices = new[] { new { message = new { role = "assistant", content = Reply } } }, usage = new { prompt_tokens = PromptTokens, completion_tokens = CompletionTokens } };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json") };
    }
}
