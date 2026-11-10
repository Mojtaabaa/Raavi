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
        EmbeddingStatus = null;
        Reply = "A short summary.";
        OmitUsage = false;
        PromptTokens = 120;
        CompletionTokens = 30;
    }

    /// <summary>Embedding dimension of the fake model (a bag of hashed words: texts that share words are similar).</summary>
    public const int Dims = 32;

    /// <summary>When set, embedding requests fail with this status.</summary>
    public HttpStatusCode? EmbeddingStatus { get; set; }

    private HttpResponseMessage Embeddings(string requestText)
    {
        if (EmbeddingStatus is { } failure)
        {
            return new HttpResponseMessage(failure) { Content = new StringContent("{}", Encoding.UTF8, "application/json") };
        }

        var inputs = JsonDocument.Parse(requestText).RootElement.GetProperty("input").EnumerateArray().Select(i => i.GetString() ?? string.Empty).ToList();
        var data = inputs.Select((t, index) => new { index, embedding = Vector(t) }).ToList();
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new { data, usage = new { total_tokens = inputs.Sum(i => i.Length) / 4 + 1 } }), Encoding.UTF8, "application/json") };
    }

    public static float[] Vector(string text)
    {
        var v = new float[Dims];
        foreach (var word in System.Text.RegularExpressions.Regex.Matches(text.ToLowerInvariant(), @"[\p{L}\p{N}]{2,}").Select(m => m.Value))
        {
            var h = 17;
            foreach (var c in word)
            {
                h = unchecked((h * 31) + c);
            }

            v[Math.Abs(h) % Dims] += 1f;
        }

        return v;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var text = request.Content is null ? "{}" : await request.Content.ReadAsStringAsync(cancellationToken);
        lock (_calls)
        {
            _calls.Add(new AiCall(request.RequestUri!, request.Headers.Authorization?.ToString(), JsonDocument.Parse(text).RootElement.Clone()));
        }

        if (request.RequestUri!.AbsolutePath.EndsWith("/embeddings", StringComparison.Ordinal))
        {
            return Embeddings(text);
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
