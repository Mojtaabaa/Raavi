using System.Net;
using System.Net.Http.Json;
using System.Text.Json;

namespace Raavi.Tests.Infrastructure;

/// <summary>
/// Minimal browser emulation for API tests: keeps host-only cookies explicitly (so tests can also replay a cookie on a
/// different host on purpose), fetches the antiforgery token and sends it on unsafe requests like the SPA does.
/// </summary>
public sealed class Browser : IDisposable
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _client;
    private string? _xsrf;

    public Browser(ApiFactory api, string host)
    {
        ArgumentNullException.ThrowIfNull(api);
        Host = host;
        _client = api.ForHost(host);
    }

    public string Host { get; }

    public Dictionary<string, string> Cookies { get; } = new(StringComparer.Ordinal);

    public List<string> LastSetCookieHeaders { get; } = [];

    public string? SessionCookie => Cookies.GetValueOrDefault("__Host-raavi");

    /// <summary>GET /auth/antiforgery — must be repeated after sign-in/out because tokens are bound to the identity.</summary>
    public async Task PrimeAsync()
    {
        using var response = await SendAsync(HttpMethod.Get, "api/v1/auth/antiforgery", null, withXsrf: false);
        response.EnsureSuccessStatusCode();
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Json);
        _xsrf = body.GetProperty("token").GetString();
    }

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null, withXsrf: false);

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null, bool withXsrf = true) => SendAsync(HttpMethod.Post, path, body, withXsrf);

    public Task<HttpResponseMessage> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, body, withXsrf: true);

    public Task<HttpResponseMessage> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, null, withXsrf: true);

    public async Task<JsonElement> GetJsonAsync(string path)
    {
        using var response = await GetAsync(path);
        Assert.True(response.IsSuccessStatusCode, $"GET {path} → {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<JsonElement>(Json);
    }

    /// <summary>POST that must succeed; re-primes antiforgery afterwards (identity may have changed).</summary>
    public async Task<JsonElement> PostOkAsync(string path, object? body = null)
    {
        using var response = await PostAsync(path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"POST {path} → {(int)response.StatusCode} {text}");
        await PrimeAsync();
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body, bool withXsrf, string? ifMatch = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (ifMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-Match", ifMatch);
        }

        if (body is not null)
        {
            request.Content = body as HttpContent ?? JsonContent.Create(body, options: Json);
        }

        if (Cookies.Count > 0)
        {
            request.Headers.Add("Cookie", string.Join("; ", Cookies.Select(c => $"{c.Key}={c.Value}")));
        }

        if (withXsrf && _xsrf is not null)
        {
            request.Headers.Add("X-XSRF-TOKEN", _xsrf);
        }

        var response = await _client.SendAsync(request);
        LastSetCookieHeaders.Clear();
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            foreach (var header in setCookies)
            {
                LastSetCookieHeaders.Add(header);
                var nameValue = header.Split(';')[0];
                var eq = nameValue.IndexOf('=', StringComparison.Ordinal);
                var name = nameValue[..eq].Trim();
                var value = nameValue[(eq + 1)..].Trim();
                var expired = header.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase);
                if (expired || value.Length == 0)
                {
                    Cookies.Remove(name);
                }
                else
                {
                    Cookies[name] = value;
                }
            }
        }

        return response;
    }

    public static async Task AssertStatusAsync(HttpResponseMessage response, HttpStatusCode expected, string? code = null)
    {
        ArgumentNullException.ThrowIfNull(response);
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"expected {(int)expected}, got {(int)response.StatusCode}: {body}");
        if (code is not null)
        {
            Assert.Contains($"\"{code}\"", body, StringComparison.Ordinal);
        }
    }

    public void Dispose() => _client.Dispose();
}

/// <summary>RFC 4648 base32 decoding for TOTP secrets in tests.</summary>
public static class Base32
{
    public static byte[] Decode(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in input.TrimEnd('='))
        {
            buffer = (buffer << 5) | alphabet.IndexOf(c, StringComparison.Ordinal);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return [.. output];
    }
}
