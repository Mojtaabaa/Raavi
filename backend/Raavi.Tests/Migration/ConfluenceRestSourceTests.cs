using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Raavi.Migration.Confluence;
using Raavi.Wiki.Domain;

namespace Raavi.Tests.Migration;

public sealed class ConfluenceRestSourceTests
{
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<(HttpMethod Method, string Url, string? Auth)> Calls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls.Add((request.Method, request.RequestUri!.PathAndQuery, request.Headers.Authorization?.ToString()));
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static ConfluenceRestSource Source(Handler h, int pageSize = 2) =>
        new(new HttpClient(h), new Uri("https://wiki.example.test/"), "secret-token", new ConfluenceRestOptions { RequestsPerSecond = 0, PageSize = pageSize, MaxRetries = 2 });

    [Fact]
    public async Task Detection_reads_the_applinks_manifest_and_refuses_other_products_and_old_versions()
    {
        var ok = new Handler(_ => Json("""{"typeId":"confluence","version":"10.2.13","buildNumber":23551}"""));
        using (var source = Source(ok))
        {
            var info = await source.DetectAsync(default);
            Assert.Equal(("10.2.13", 23551, ConfluenceSourceKind.RestApi), (info.Version, info.BuildNumber, info.Kind));
            Assert.Equal("Bearer secret-token", ok.Calls[0].Auth);
            Assert.All(ok.Calls, c => Assert.Equal(HttpMethod.Get, c.Method)); // read-only
        }

        using var jira = Source(new Handler(_ => Json("""{"typeId":"jira","version":"9.0.0"}""")));
        Assert.Equal("source.not_confluence", (await Assert.ThrowsAsync<ConfluenceSourceException>(() => jira.DetectAsync(default))).Code);
        using var old = Source(new Handler(_ => Json("""{"typeId":"confluence","version":"5.9.1","buildNumber":1}""")));
        Assert.Equal("source.version_unsupported", (await Assert.ThrowsAsync<ConfluenceSourceException>(() => old.DetectAsync(default))).Code);
    }

    [Fact]
    public async Task Pages_are_streamed_across_result_pages_with_tree_position_and_labels()
    {
        var h = new Handler(r =>
        {
            var q = r.RequestUri!.Query;
            if (q.Contains("start=0", StringComparison.Ordinal))
            {
                return Json("""{"results":[{"id":"1","title":"Root","version":{"number":2},"ancestors":[],"extensions":{"position":0},"metadata":{"labels":{"results":[{"name":"a"}]}}},{"id":"2","title":"Child","version":{"number":1},"ancestors":[{"id":"1"}],"extensions":{"position":3}}]}""");
            }

            return Json("""{"results":[{"id":"3","title":"Third","version":{"number":1},"ancestors":[{"id":"1"},{"id":"2"}]}]}""");
        });
        using var source = Source(h);
        var pages = new List<SourcePage>();
        await foreach (var p in source.StreamPagesAsync("ENG", default))
        {
            pages.Add(p);
        }

        Assert.Equal(["1", "2", "3"], pages.Select(p => p.SourceContentId));
        Assert.Equal([null, "1", "2"], pages.Select(p => p.ParentSourceContentId));
        Assert.Equal(3, pages[1].Position);
        Assert.Equal(["a"], pages[0].Labels);
        Assert.Equal(2, pages[0].CurrentVersionNumber);
    }

    [Fact]
    public async Task Versions_fall_back_to_the_experimental_endpoint_and_stream_oldest_first_with_authors()
    {
        var h = new Handler(r =>
        {
            var path = r.RequestUri!.AbsolutePath;
            if (path == "/rest/api/content/7/version")
            {
                return Json("{}", HttpStatusCode.NotFound);
            }

            if (path == "/rest/experimental/content/7/version")
            {
                return Json("""{"results":[{"number":2,"when":"2024-02-01T10:00:00.000+03:30","message":"second","by":{"userKey":"k2","displayName":"Sara"}},{"number":1,"when":"2024-01-01T10:00:00.000+03:30","by":{"username":"ali"}}]}""");
            }

            var n = r.RequestUri.Query.Contains("version=2", StringComparison.Ordinal) ? 2 : 1;
            return Json("{\"title\":\"T" + n + "\",\"body\":{\"storage\":{\"value\":\"<p>v" + n + "</p>\"}}}");
        });
        using var source = Source(h, pageSize: 50);
        var versions = new List<SourceVersion>();
        await foreach (var v in source.StreamVersionsAsync("7", default))
        {
            versions.Add(v);
        }

        Assert.Equal([1, 2], versions.Select(v => v.SourceVersionNumber));
        Assert.Equal([false, true], versions.Select(v => v.IsCurrent));
        Assert.Equal(["<p>v1</p>", "<p>v2</p>"], versions.Select(v => v.StorageFormatBody));
        Assert.Equal(["ali", "k2"], versions.Select(v => v.AuthorKey));
        Assert.Equal("second", versions[1].VersionComment);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 6, 30, 0, TimeSpan.Zero), versions[0].CreatedAtUtc);
        Assert.Equal(1, h.Calls.Count(c => c.Url.Contains("rest/api/content/7/version", StringComparison.Ordinal))); // probed once...

        await foreach (var _ in source.StreamVersionsAsync("7", default))
        {
        }

        Assert.Equal(1, h.Calls.Count(c => c.Url.Contains("rest/api/content/7/version", StringComparison.Ordinal))); // ...and remembered
    }

    [Fact]
    public async Task Throttling_responses_are_retried_and_errors_never_leak_the_token()
    {
        var attempts = 0;
        var h = new Handler(_ => ++attempts == 1 ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Headers = { RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero) } } : Json("""{"results":[]}"""));
        using var source = Source(h);
        Assert.Empty(await source.ListSpacesAsync(default));
        Assert.Equal(2, attempts);

        using var denied = Source(new Handler(_ => Json("{}", HttpStatusCode.Unauthorized)));
        var ex = await Assert.ThrowsAsync<ConfluenceSourceException>(() => denied.ListSpacesAsync(default));
        Assert.Equal("source.unauthorized", ex.Code);
        Assert.DoesNotContain("secret-token", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://wiki.example.test/confluence", true)]
    [InlineData("https://Docs.Corp.Example/", true)]
    [InlineData("https://evil.example/", false)]
    [InlineData("ftp://wiki.example.test/", false)]
    [InlineData("https://user:pw@wiki.example.test/", false)]
    [InlineData("https://169.254.169.254/", false)]
    [InlineData("http://127.0.0.1:8090/", false)]
    [InlineData("not a url", false)]
    public void Source_hosts_must_be_allow_listed_and_never_loopback_or_metadata_addresses(string url, bool allowed)
    {
        var policy = new SourceHostPolicy(["wiki.example.test", "*.corp.example", "169.254.169.254", "127.0.0.1"]);
        if (allowed)
        {
            Assert.EndsWith("/", policy.Validate(url).ToString(), StringComparison.Ordinal);
        }
        else
        {
            Assert.Throws<ConfluenceSourceException>(() => policy.Validate(url));
        }

        Assert.Throws<ConfluenceSourceException>(() => new SourceHostPolicy([]).Validate("https://wiki.example.test/")); // empty list denies everything
    }

    /// <summary>
    /// Live check against a real Confluence (read-only GETs). Runs only when <c>RAAVI_CONFLUENCE_TEST_URL</c> and
    /// <c>RAAVI_CONFLUENCE_TEST_TOKEN</c> are set; the token is never printed. Every version is converted and validated with
    /// the real editor schema.
    /// </summary>
    [Fact]
    public async Task Live_server_versions_convert_to_valid_documents()
    {
        var url = Environment.GetEnvironmentVariable("RAAVI_CONFLUENCE_TEST_URL");
        var token = Environment.GetEnvironmentVariable("RAAVI_CONFLUENCE_TEST_TOKEN");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
        {
            return;
        }

        var baseUrl = new SourceHostPolicy([new Uri(url).Host]).Validate(url);
        using var http = new HttpClient(SourceHostPolicy.CreateHandler());
        using var source = new ConfluenceRestSource(http, baseUrl, token, new ConfluenceRestOptions { RequestsPerSecond = 20 });

        var info = await source.DetectAsync(default);
        Assert.False(string.IsNullOrEmpty(info.Version));
        var spaces = await source.ListSpacesAsync(default);
        Assert.NotEmpty(spaces);

        var converted = 0;
        var failures = new List<string>();
        var macros = new Dictionary<string, int>();
        var multiVersionSeen = false;
        foreach (var space in spaces)
        {
            var pages = new List<SourcePage>();
            await foreach (var p in source.StreamPagesAsync(space.Key, default))
            {
                pages.Add(p);
            }

            Assert.All(pages, p => Assert.Equal(space.Key, p.SpaceKey));
            Assert.All(pages.Where(p => p.ParentSourceContentId is not null), p => Assert.Contains(pages, x => x.SourceContentId == p.ParentSourceContentId));

            foreach (var page in pages.Take(12))
            {
                var last = 0;
                var lastWhen = DateTimeOffset.MinValue;
                await foreach (var v in source.StreamVersionsAsync(page.SourceContentId, default))
                {
                    Assert.True(v.SourceVersionNumber > last, "versions must be oldest first");
                    Assert.True(v.CreatedAtUtc >= lastWhen, "timestamps must not go backwards");
                    (last, lastWhen) = (v.SourceVersionNumber, v.CreatedAtUtc);
                    var r = StorageFormatConverter.Convert(v.StorageFormatBody);
                    try
                    {
                        var envelope = new JsonObject { ["schema"] = "raavi.doc", ["schemaVersion"] = RaaviDocument.CurrentSchemaVersion, ["doc"] = r.Doc.DeepClone() };
                        _ = RaaviDocument.Parse(envelope.ToJsonString());
                        converted++;
                    }
#pragma warning disable CA1031 // collect every failure for the report
                    catch (Exception ex)
#pragma warning restore CA1031
                    {
                        failures.Add($"{space.Key}/{page.SourceContentId} v{v.SourceVersionNumber}: {ex.Message}");
                    }

                    foreach (var (name, count) in r.UnsupportedMacros)
                    {
                        macros[name] = macros.GetValueOrDefault(name) + count;
                    }
                }

                multiVersionSeen |= last > 1;
                Assert.Equal(page.CurrentVersionNumber, last);
            }
        }

        Assert.True(converted > 0);
        Assert.True(failures.Count == 0, string.Join('\n', failures.Take(10)));
        Assert.True(multiVersionSeen, "expected at least one page with more than one version");
        Console.WriteLine($"LIVE: Confluence {info.Version} build {info.BuildNumber}; {spaces.Count} spaces; {converted} versions converted and schema-valid; unsupported macros: {string.Join(", ", macros.Select(m => $"{m.Key}={m.Value}"))}");
    }

    /// <summary>Live: every file of the first pages with attachments downloads byte-for-byte like a plain independent request (regression for doubled JPEG bodies).</summary>
    [Fact]
    public async Task Live_attachment_downloads_are_byte_exact()
    {
        var url = Environment.GetEnvironmentVariable("RAAVI_CONFLUENCE_TEST_URL");
        var token = Environment.GetEnvironmentVariable("RAAVI_CONFLUENCE_TEST_TOKEN");
        if (string.IsNullOrEmpty(url) || string.IsNullOrEmpty(token))
        {
            return;
        }

        var baseUrl = new SourceHostPolicy([new Uri(url).Host]).Validate(url);
        using var http = new HttpClient(SourceHostPolicy.CreateHandler());
        using var source = new ConfluenceRestSource(http, baseUrl, token, new ConfluenceRestOptions { RequestsPerSecond = 20 });
        using var plain = new HttpClient { DefaultRequestHeaders = { Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token) } };

        var checkedFiles = 0;
        foreach (var space in await source.ListSpacesAsync(default))
        {
            await foreach (var page in source.StreamPagesAsync(space.Key, default))
            {
                foreach (var file in await source.ListAttachmentsAsync(page.SourceContentId, default))
                {
                    var viaAdapter = await source.DownloadAttachmentAsync(file, 50 * 1024 * 1024, default);
                    var expected = await plain.GetByteArrayAsync(new Uri(baseUrl, file.DownloadPath.TrimStart('/')));
                    Assert.True(viaAdapter.AsSpan().SequenceEqual(expected), $"{space.Key}/{page.SourceContentId} {file.FileName}: adapter {viaAdapter.Length} bytes, plain request {expected.Length} bytes");
                    checkedFiles++;
                }
            }
        }

        Assert.True(checkedFiles > 0);
    }
}
