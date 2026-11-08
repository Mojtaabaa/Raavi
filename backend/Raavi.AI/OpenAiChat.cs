using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Raavi.AI;

public sealed record AiMessage(string Role, string Content);

/// <summary>Where and how to call a provider. The key lives only in memory for the duration of the call.</summary>
public sealed record AiEndpoint(Uri BaseUrl, string Model, string ApiKey);

public sealed record AiChatResult(string Text, int PromptTokens, int CompletionTokens);

/// <summary>Chat completion against a provider. One implementation (OpenAI-compatible HTTP) today; another protocol is another class (ADR-034).</summary>
public interface IAiChat
{
    Task<AiChatResult> CompleteAsync(AiEndpoint endpoint, IReadOnlyList<AiMessage> messages, int maxTokens, CancellationToken cancellationToken);
}

public static class AiHttp
{
    public const string ClientName = "raavi-ai";
}

/// <summary>
/// OpenAI-compatible <c>POST {base}/chat/completions</c> (OpenAI, DeepSeek, Ollama, vLLM, ...). Errors are mapped to stable codes without ever
/// including the response body of the provider, the prompt or the key.
/// </summary>
public sealed class OpenAiChat(IHttpClientFactory clients) : IAiChat
{
    private const int MaxResponseBytes = 1_000_000;

    public async Task<AiChatResult> CompleteAsync(AiEndpoint endpoint, IReadOnlyList<AiMessage> messages, int maxTokens, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        ArgumentNullException.ThrowIfNull(messages);
        var body = JsonSerializer.Serialize(new
        {
            model = endpoint.Model,
            messages = messages.Select(m => new { role = m.Role, content = m.Content }),
            max_tokens = maxTokens,
            temperature = 0.2,
            stream = false,
        });
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(endpoint.BaseUrl, "chat/completions")) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", endpoint.ApiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        HttpResponseMessage response;
        try
        {
            response = await clients.CreateClient(AiHttp.ClientName).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AiProviderException)
        {
            throw;
        }
        catch (Exception ex) when (ex.InnerException is AiProviderException inner)
        {
            throw new AiProviderException(inner.Code, inner.Message);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested && ex is HttpRequestException or TaskCanceledException or TimeoutException or IOException)
        {
            throw new AiProviderException("ai.provider_unavailable", "The AI provider could not be reached.");
        }

        using (response)
        {
            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    throw new AiProviderException("ai.provider_auth", "The AI provider refused the credentials.");
                case HttpStatusCode.TooManyRequests:
                    throw new AiProviderException("ai.provider_rate_limited", "The AI provider is rate limiting requests.");
                case HttpStatusCode.NotFound:
                    throw new AiProviderException("ai.provider_not_found", "The AI provider has no such endpoint or model.");
                case >= HttpStatusCode.InternalServerError:
                    throw new AiProviderException("ai.provider_unavailable", "The AI provider is not available.");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new AiProviderException("ai.provider_rejected", "The AI provider rejected the request.");
            }

            var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            await using (stream.ConfigureAwait(false))
            {
                using var buffer = new MemoryStream();
                var chunk = new byte[16 * 1024];
                int n;
                while ((n = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    buffer.Write(chunk, 0, n);
                    if (buffer.Length > MaxResponseBytes)
                    {
                        throw new AiProviderException("ai.provider_bad_response", "The response of the AI provider is too large.");
                    }
                }

                return Parse(buffer.ToArray(), messages, maxTokens);
            }
        }
    }

    private static AiChatResult Parse(byte[] json, IReadOnlyList<AiMessage> messages, int maxTokens)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var text = root.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString();
            if (string.IsNullOrWhiteSpace(text))
            {
                throw new AiProviderException("ai.provider_bad_response", "The AI provider returned no text.");
            }

            var prompt = 0;
            var completion = 0;
            if (root.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                prompt = usage.TryGetProperty("prompt_tokens", out var p) && p.TryGetInt32(out var pi) ? pi : 0;
                completion = usage.TryGetProperty("completion_tokens", out var c) && c.TryGetInt32(out var ci) ? ci : 0;
            }

            // Providers that do not report usage: count roughly four characters per token so quotas still bite.
            if (prompt == 0)
            {
                prompt = Math.Max(1, messages.Sum(m => m.Content.Length) / 4);
            }

            if (completion == 0)
            {
                completion = Math.Clamp(text.Length / 4, 1, maxTokens);
            }

            return new AiChatResult(text.Trim(), prompt, completion);
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            throw new AiProviderException("ai.provider_bad_response", "The response of the AI provider could not be understood.");
        }
    }
}
