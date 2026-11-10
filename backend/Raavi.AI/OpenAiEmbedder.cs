using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Raavi.AI;

public sealed record AiEmbeddingResult(IReadOnlyList<float[]> Vectors, int Tokens);

/// <summary>Text embeddings from a provider (ADR-049). One implementation: OpenAI-compatible <c>POST {base}/embeddings</c>.</summary>
public interface IAiEmbedder
{
    Task<AiEmbeddingResult> EmbedAsync(AiEndpoint endpoint, IReadOnlyList<string> inputs, CancellationToken cancellationToken);
}

/// <summary>Same transport, allow-list and error codes as chat; nothing from the provider's body is ever surfaced.</summary>
public sealed class OpenAiEmbedder(IHttpClientFactory clients) : IAiEmbedder
{
    public const int MaxBatch = 64;

    public async Task<AiEmbeddingResult> EmbedAsync(AiEndpoint endpoint, IReadOnlyList<string> inputs, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
        {
            return new AiEmbeddingResult([], 0);
        }

        var vectors = new List<float[]>(inputs.Count);
        var tokens = 0;
        foreach (var batch in inputs.Chunk(MaxBatch))
        {
            var body = JsonSerializer.Serialize(new { model = endpoint.Model, input = batch });
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUrl, "embeddings")) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            var bytes = await AiTransport.SendAsync(clients, request, cancellationToken).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(bytes);
                var data = doc.RootElement.GetProperty("data").EnumerateArray().OrderBy(d => d.TryGetProperty("index", out var i) ? i.GetInt32() : 0).ToList();
                if (data.Count != batch.Length)
                {
                    throw new AiProviderException("ai.provider_bad_response", "The embedding answer did not match the request.");
                }

                foreach (var d in data)
                {
                    var v = d.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray();
                    if (v.Length is < 8 or > 8192 || v.Any(float.IsNaN))
                    {
                        throw new AiProviderException("ai.provider_bad_response", "The embedding answer could not be understood.");
                    }

                    vectors.Add(v);
                }

                tokens += doc.RootElement.TryGetProperty("usage", out var u) && u.TryGetProperty("total_tokens", out var t) && t.TryGetInt32(out var ti) ? ti : batch.Sum(s => s.Length) / 4;
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                throw new AiProviderException("ai.provider_bad_response", "The embedding answer could not be understood.");
            }
        }

        return new AiEmbeddingResult(vectors, tokens);
    }
}
