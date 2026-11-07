using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace Raavi.Migration.Confluence;

/// <summary>Raised for source problems that an administrator must see (unreachable, unauthorized, unknown version…).</summary>
public sealed class ConfluenceSourceException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>
/// Which source hosts a migration may contact (SSRF guard). Configured globally (<c>Raavi:Migration:AllowedSourceHosts</c>);
/// an empty list denies everything. Loopback, link-local (cloud metadata), multicast and unspecified addresses are always
/// refused, also after DNS resolution. Private ranges are allowed — on-premises sources live there — but only for allow-listed hosts.
/// </summary>
public sealed class SourceHostPolicy(IEnumerable<string> allowedHosts)
{
    private readonly string[] _allowed = [.. allowedHosts.Select(h => h.Trim().ToLowerInvariant()).Where(h => h.Length > 0)];

    public Uri Validate(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo))
        {
            throw new ConfluenceSourceException("source.url_invalid", "The source address must be an http(s) URL without credentials.");
        }

        var host = uri.IdnHost.ToLowerInvariant();
        if (!_allowed.Any(a => a == host || (a.StartsWith("*.", StringComparison.Ordinal) && host.EndsWith(a[1..], StringComparison.Ordinal))))
        {
            throw new ConfluenceSourceException("source.host_not_allowed", $"The host '{host}' is not in the allowed source hosts of this installation.");
        }

        if (IPAddress.TryParse(host, out var ip) && IsForbidden(ip))
        {
            throw new ConfluenceSourceException("source.host_forbidden", "This address is not allowed as a migration source.");
        }

        return new Uri(uri.GetLeftPart(UriPartial.Path).TrimEnd('/') + "/");
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
            return b[0] == 169 && b[1] == 254 || b[0] >= 224 || b[0] == 0;
        }

        return false;
    }

    /// <summary>HTTP handler that re-checks every resolved address at connect time (defeats DNS rebinding).</summary>
    public static SocketsHttpHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false,
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct).ConfigureAwait(false);
            var allowed = addresses.Where(a => !IsForbidden(a)).ToArray();
            if (allowed.Length == 0)
            {
                throw new ConfluenceSourceException("source.host_forbidden", "The source host resolves only to forbidden addresses.");
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

public sealed class ConfluenceRestOptions
{
    public int RequestsPerSecond { get; set; } = 10;

    public int PageSize { get; set; } = 50;

    public int MaxRetries { get; set; } = 5;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// REST adapter for Confluence Server/Data Center (CONFLUENCE_MIGRATION.md §1). Read-only: only GET requests are made, with
/// a bearer personal access token that is never logged or included in errors. Version listing is detected at runtime
/// (<c>/rest/api/content/{id}/version</c> where present, else the experimental endpoint — verified on Data Center 10.2).
/// Requests are throttled and retried with back-off on 429/5xx.
/// </summary>
public sealed class ConfluenceRestSource : IConfluenceSourceProvider, IDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _baseUrl;
    private readonly ConfluenceRestOptions _options;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _next = DateTimeOffset.MinValue;
    private readonly bool _ownsClient;
    private bool? _versionListingStable;

    public ConfluenceRestSource(HttpClient http, Uri baseUrl, string personalAccessToken, ConfluenceRestOptions? options = null, bool ownsClient = false)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(personalAccessToken);
        _http = http;
        _ownsClient = ownsClient;
        _baseUrl = baseUrl;
        _options = options ?? new ConfluenceRestOptions();
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", personalAccessToken);
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        _http.Timeout = _options.RequestTimeout;
    }

    public ConfluenceSourceKind Kind => ConfluenceSourceKind.RestApi;

    /// <summary>Releases the throttle (and the <see cref="HttpClient"/> when this adapter created it).</summary>
    public void Dispose()
    {
        _gate.Dispose();
        if (_ownsClient)
        {
            _http.Dispose();
        }
    }

    public async Task<ConfluenceSourceInfo> DetectAsync(CancellationToken cancellationToken)
    {
        // Atlassian applinks manifest exists on Server/Data Center and carries the product version and build number.
        using var doc = await GetAsync("rest/applinks/1.0/manifest", cancellationToken).ConfigureAwait(false);
        var root = doc.RootElement;
        var type = root.TryGetProperty("typeId", out var t) ? t.GetString() : null;
        var version = root.TryGetProperty("version", out var v) ? v.GetString() : null;
        if (!string.Equals(type, "confluence", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(version))
        {
            throw new ConfluenceSourceException("source.not_confluence", "The address did not identify itself as a Confluence Server/Data Center instance.");
        }

        int? build = root.TryGetProperty("buildNumber", out var b) && b.TryGetInt32(out var bn) ? bn : null;
        if (!Version.TryParse(version, out var parsed) || parsed.Major < 6)
        {
            throw new ConfluenceSourceException("source.version_unsupported", $"Confluence version '{version}' (build {build}) is not supported by the REST adapter (6.0 or later is required).");
        }

        // Data Center and Server share the REST surface used here; the edition cannot be told apart through REST.
        return new ConfluenceSourceInfo(ConfluenceEdition.Unknown, version, build, ConfluenceSourceKind.RestApi);
    }

    public async Task<IReadOnlyList<SourceSpace>> ListSpacesAsync(CancellationToken cancellationToken)
    {
        var spaces = new List<SourceSpace>();
        await foreach (var item in PagedAsync("rest/api/space?type=global&status=current&expand=description.plain,homepage", cancellationToken).ConfigureAwait(false))
        {
            var key = item.GetProperty("key").GetString()!;
            var name = item.GetProperty("name").GetString() ?? key;
            var description = item.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.Object && d.TryGetProperty("plain", out var p) && p.TryGetProperty("value", out var pv) ? pv.GetString() : null;
            var home = item.TryGetProperty("homepage", out var hp) && hp.ValueKind == JsonValueKind.Object && hp.TryGetProperty("id", out var hid) ? hid.GetString() : null;
            spaces.Add(new SourceSpace(key, name, string.IsNullOrWhiteSpace(description) ? null : description, ParseTime(item, "creationDate"), home));
        }

        return spaces;
    }

    public async IAsyncEnumerable<SourcePage> StreamPagesAsync(string spaceKey, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(spaceKey);
        var path = $"rest/api/content?spaceKey={Uri.EscapeDataString(spaceKey)}&type=page&status=current&expand=version,ancestors,metadata.labels,extensions.position";
        await foreach (var item in PagedAsync(path, cancellationToken).ConfigureAwait(false))
        {
            var id = item.GetProperty("id").GetString()!;
            string? parent = null;
            if (item.TryGetProperty("ancestors", out var anc) && anc.ValueKind == JsonValueKind.Array && anc.GetArrayLength() > 0)
            {
                parent = anc[anc.GetArrayLength() - 1].GetProperty("id").GetString();
            }

            var position = item.TryGetProperty("extensions", out var ext) && ext.TryGetProperty("position", out var pos) && pos.ValueKind == JsonValueKind.Number ? pos.GetInt32() : 0;
            var labels = new List<string>();
            if (item.TryGetProperty("metadata", out var meta) && meta.TryGetProperty("labels", out var lb) && lb.TryGetProperty("results", out var lr))
            {
                labels.AddRange(lr.EnumerateArray().Select(l => l.GetProperty("name").GetString()!).Where(n => n.Length > 0));
            }

            yield return new SourcePage(id, spaceKey, item.GetProperty("title").GetString() ?? string.Empty, parent, position, item.GetProperty("version").GetProperty("number").GetInt32(), labels);
        }
    }

    public async IAsyncEnumerable<SourceVersion> StreamVersionsAsync(string sourceContentId, [EnumeratorCancellation] CancellationToken cancellationToken, IReadOnlySet<int>? skip = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceContentId);
        var id = Uri.EscapeDataString(sourceContentId);
        var listing = await ListVersionsAsync(id, cancellationToken).ConfigureAwait(false);
        var newest = listing.Count == 0 ? 0 : listing.Max(v => v.Number);
        foreach (var version in listing.OrderBy(v => v.Number))
        {
            if (skip?.Contains(version.Number) == true)
            {
                continue;
            }

            using var doc = await GetAsync($"rest/api/content/{id}?status=historical&version={version.Number}&expand=body.storage,version", cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            var body = root.TryGetProperty("body", out var b) && b.TryGetProperty("storage", out var s) && s.TryGetProperty("value", out var value) ? value.GetString() ?? string.Empty : string.Empty;
            yield return new SourceVersion(
                sourceContentId, version.Number, root.GetProperty("title").GetString() ?? string.Empty, body,
                version.AuthorKey, version.AuthorName, version.When, version.Message, version.Number == newest);
        }
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    private sealed record ListedVersion(int Number, string? AuthorKey, string? AuthorName, DateTimeOffset When, string? Message);

    private async Task<List<ListedVersion>> ListVersionsAsync(string escapedId, CancellationToken ct)
    {
        // Newer builds expose /rest/api/content/{id}/version; this one (verified on 10.2.x) only has the experimental
        // endpoint, so the choice is probed once and remembered.
        string[] candidates = _versionListingStable == false
            ? [$"rest/experimental/content/{escapedId}/version"]
            : [$"rest/api/content/{escapedId}/version", $"rest/experimental/content/{escapedId}/version"];
        foreach (var path in candidates)
        {
            var results = new List<ListedVersion>();
            try
            {
                await foreach (var item in PagedAsync(path, ct).ConfigureAwait(false))
                {
                    var by = item.TryGetProperty("by", out var byEl) && byEl.ValueKind == JsonValueKind.Object ? byEl : default;
                    string? Text(string name) => by.ValueKind == JsonValueKind.Object && by.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                    results.Add(new ListedVersion(item.GetProperty("number").GetInt32(), Text("userKey") ?? Text("username"), Text("displayName"), ParseTime(item, "when") ?? DateTimeOffset.UnixEpoch, item.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null));
                }
            }
            catch (ConfluenceSourceException ex) when (ex.Code == "source.not_found" && path != candidates[^1])
            {
                _versionListingStable = false;
                continue;
            }

            _versionListingStable ??= path.StartsWith("rest/api/", StringComparison.Ordinal);
            return results;
        }

        throw new ConfluenceSourceException("source.versions_unavailable", "This Confluence build exposes no version-history endpoint.");
    }

    private async IAsyncEnumerable<JsonElement> PagedAsync(string path, [EnumeratorCancellation] CancellationToken ct)
    {
        var start = 0;
        for (;;)
        {
            var sep = path.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            using var doc = await GetAsync($"{path}{sep}start={start}&limit={_options.PageSize}", ct).ConfigureAwait(false);
            var results = doc.RootElement.GetProperty("results");
            var count = 0;
            foreach (var item in results.EnumerateArray())
            {
                count++;
                yield return item.Clone();
            }

            // `size` is the number of results on this page; a full page may be followed by another.
            if (count < _options.PageSize)
            {
                yield break;
            }

            start += count;
        }
    }

    private async Task<JsonDocument> GetAsync(string relative, CancellationToken ct)
    {
        using var response = await SendAsync(relative, ct).ConfigureAwait(false);
        var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            try
            {
                return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            }
            catch (JsonException)
            {
                throw new ConfluenceSourceException("source.response_invalid", "The source returned a response that is not JSON.");
            }
        }
    }

    /// <summary>One throttled GET with retries on 429/5xx; returns a successful response or throws a mapped source error. Never follows redirects.</summary>
    private async Task<HttpResponseMessage> SendAsync(string relative, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            await ThrottleAsync(ct).ConfigureAwait(false);
            HttpResponseMessage response;
            try
            {
                response = await _http.GetAsync(new Uri(_baseUrl, relative), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex) when (attempt < _options.MaxRetries)
            {
                _ = ex;
                await Task.Delay(Backoff(attempt, null), ct).ConfigureAwait(false);
                continue;
            }
            catch (HttpRequestException ex)
            {
                throw new ConfluenceSourceException("source.unreachable", "The source could not be reached: " + ex.Message);
            }

            if (response.IsSuccessStatusCode)
            {
                return response;
            }

            using (response)
            {
                if ((response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout) && attempt < _options.MaxRetries)
                {
                    await Task.Delay(Backoff(attempt, response.Headers.RetryAfter?.Delta), ct).ConfigureAwait(false);
                    continue;
                }

                throw response.StatusCode switch
                {
                    HttpStatusCode.Unauthorized => new ConfluenceSourceException("source.unauthorized", "The source rejected the access token."),
                    HttpStatusCode.Forbidden => new ConfluenceSourceException("source.forbidden", "The access token may not read this content."),
                    HttpStatusCode.NotFound => new ConfluenceSourceException("source.not_found", "The source has no such resource."),
                    _ => new ConfluenceSourceException("source.error", $"The source answered HTTP {(int)response.StatusCode}."),
                };
            }
        }
    }

    public async Task<IReadOnlyList<SourceComment>> ListCommentsAsync(string sourceContentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceContentId);
        var list = new List<SourceComment>();
        var path = $"rest/api/content/{Uri.EscapeDataString(sourceContentId)}/child/comment?depth=all&expand=body.storage,history,extensions.inlineProperties,extensions.resolution,ancestors";
        await foreach (var item in PagedAsync(path, cancellationToken).ConfigureAwait(false))
        {
            var body = item.TryGetProperty("body", out var b) && b.TryGetProperty("storage", out var s) && s.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty;
            var history = item.TryGetProperty("history", out var h) && h.ValueKind == JsonValueKind.Object ? h : default;
            var by = history.ValueKind == JsonValueKind.Object && history.TryGetProperty("createdBy", out var cb) && cb.ValueKind == JsonValueKind.Object ? cb : default;
            string? ByText(string name) => by.ValueKind == JsonValueKind.Object && by.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            string? parent = null;
            if (item.TryGetProperty("ancestors", out var anc) && anc.ValueKind == JsonValueKind.Array)
            {
                // Ancestors run from the page down; only comment ancestors (type "comment") are parents.
                foreach (var a in anc.EnumerateArray())
                {
                    if (a.TryGetProperty("type", out var at) && at.GetString() == "comment" && a.TryGetProperty("id", out var aid))
                    {
                        parent = aid.GetString();
                    }
                }
            }

            var ext = item.TryGetProperty("extensions", out var e2) && e2.ValueKind == JsonValueKind.Object ? e2 : default;
            var location = ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty("location", out var loc) ? loc.GetString() : null;
            string? selection = null;
            if (ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty("inlineProperties", out var inl) && inl.ValueKind == JsonValueKind.Object && inl.TryGetProperty("originalSelection", out var sel))
            {
                selection = sel.GetString();
            }

            var resolved = ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty("resolution", out var res) && res.ValueKind == JsonValueKind.Object
                && res.TryGetProperty("status", out var st) && string.Equals(st.GetString(), "resolved", StringComparison.OrdinalIgnoreCase);
            list.Add(new SourceComment(
                item.GetProperty("id").GetString()!, parent, string.Equals(location, "inline", StringComparison.OrdinalIgnoreCase), selection, body,
                ByText("userKey") ?? ByText("username"), ByText("displayName"), ParseTime(history, "createdDate") ?? DateTimeOffset.UnixEpoch, resolved));
        }

        return list;
    }

    public async Task<SourceRestrictions> GetRestrictionsAsync(string sourceContentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceContentId);
        using var doc = await GetAsync($"rest/api/content/{Uri.EscapeDataString(sourceContentId)}/restriction/byOperation?expand=restrictions.user,restrictions.group", cancellationToken).ConfigureAwait(false);
        static List<SourceSubject> Subjects(JsonElement operation)
        {
            var list = new List<SourceSubject>();
            if (operation.ValueKind != JsonValueKind.Object || !operation.TryGetProperty("restrictions", out var r) || r.ValueKind != JsonValueKind.Object)
            {
                return list;
            }

            if (r.TryGetProperty("user", out var users) && users.TryGetProperty("results", out var ur))
            {
                foreach (var u in ur.EnumerateArray())
                {
                    var key = (u.TryGetProperty("userKey", out var k) ? k.GetString() : null) ?? (u.TryGetProperty("username", out var n) ? n.GetString() : null);
                    if (!string.IsNullOrEmpty(key))
                    {
                        list.Add(new SourceSubject(SourceSubjectKind.User, key, u.TryGetProperty("displayName", out var d) ? d.GetString() : null));
                    }
                }
            }

            if (r.TryGetProperty("group", out var groups) && groups.TryGetProperty("results", out var gr))
            {
                foreach (var g in gr.EnumerateArray())
                {
                    if (g.TryGetProperty("name", out var gn) && !string.IsNullOrEmpty(gn.GetString()))
                    {
                        list.Add(new SourceSubject(SourceSubjectKind.Group, gn.GetString()!, gn.GetString()));
                    }
                }
            }

            return list;
        }

        var root = doc.RootElement;
        return new SourceRestrictions(
            root.TryGetProperty("read", out var read) ? Subjects(read) : [],
            root.TryGetProperty("update", out var update) ? Subjects(update) : []);
    }

    public async Task<SourceUser?> GetUserAsync(string keyOrUserName, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyOrUserName);
        foreach (var query in new[] { "key", "username" })
        {
            try
            {
                using var doc = await GetAsync($"rest/api/user?{query}={Uri.EscapeDataString(keyOrUserName)}", cancellationToken).ConfigureAwait(false);
                var u = doc.RootElement;
                string? Text(string name) => u.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                return new SourceUser(Text("userKey") ?? keyOrUserName, Text("username"), Text("displayName"), Text("email"));
            }
            catch (ConfluenceSourceException ex) when (ex.Code is "source.not_found" or "source.error" or "source.forbidden")
            {
                // try the other lookup, then give up: an unknown or hidden user is simply unmapped
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<SourceAttachment>> ListAttachmentsAsync(string sourceContentId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceContentId);
        var list = new List<SourceAttachment>();
        await foreach (var item in PagedAsync($"rest/api/content/{Uri.EscapeDataString(sourceContentId)}/child/attachment?expand=version,extensions", cancellationToken).ConfigureAwait(false))
        {
            var title = item.TryGetProperty("title", out var t) ? t.GetString() : null;
            var download = item.TryGetProperty("_links", out var links) && links.TryGetProperty("download", out var d) ? d.GetString() : null;
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(download))
            {
                continue;
            }

            var ext = item.TryGetProperty("extensions", out var e) ? e : default;
            var version = item.TryGetProperty("version", out var v) ? v : default;
            var by = version.ValueKind == JsonValueKind.Object && version.TryGetProperty("by", out var b) && b.ValueKind == JsonValueKind.Object ? b : default;
            list.Add(new SourceAttachment(
                item.GetProperty("id").GetString()!, title,
                ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty("mediaType", out var mt) ? mt.GetString() : null,
                ext.ValueKind == JsonValueKind.Object && ext.TryGetProperty("fileSize", out var fs) && fs.TryGetInt64(out var size) ? size : 0,
                version.ValueKind == JsonValueKind.Object && version.TryGetProperty("number", out var vn) ? vn.GetInt32() : 1,
                ParseTime(version, "when") ?? DateTimeOffset.UnixEpoch,
                by.ValueKind == JsonValueKind.Object && by.TryGetProperty("userKey", out var uk) ? uk.GetString() : by.ValueKind == JsonValueKind.Object && by.TryGetProperty("username", out var un) ? un.GetString() : null,
                download));
        }

        return list;
    }

    public async Task<byte[]> DownloadAttachmentAsync(SourceAttachment attachment, long maxBytes, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attachment);
        // The path comes from the source; it must stay on the source host (relative path only).
        if (attachment.DownloadPath.Contains("://", StringComparison.Ordinal) || attachment.DownloadPath.StartsWith("//", StringComparison.Ordinal))
        {
            throw new ConfluenceSourceException("source.response_invalid", "The source returned an absolute download address.");
        }

        using var response = await SendAsync(attachment.DownloadPath.TrimStart('/'), cancellationToken).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength > maxBytes)
        {
            throw new ConfluenceSourceException("attachment.too_large", "The file is larger than the allowed size.");
        }

        var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using (stream.ConfigureAwait(false))
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken).ConfigureAwait(false)) > 0)
            {
                if (buffer.Length + read > maxBytes)
                {
                    throw new ConfluenceSourceException("attachment.too_large", "The file is larger than the allowed size.");
                }

                buffer.Write(chunk, 0, read);
            }

            return buffer.ToArray();
        }
    }

    private static TimeSpan Backoff(int attempt, TimeSpan? retryAfter) =>
        retryAfter is { } r && r > TimeSpan.Zero ? TimeSpan.FromSeconds(Math.Min(r.TotalSeconds, 120)) : TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, attempt)));

    private async Task ThrottleAsync(CancellationToken ct)
    {
        if (_options.RequestsPerSecond <= 0)
        {
            return;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var wait = _next - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero)
            {
                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            _next = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1.0 / _options.RequestsPerSecond);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static DateTimeOffset? ParseTime(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(v.GetString(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var t)
            ? t.ToUniversalTime()
            : null;
}
