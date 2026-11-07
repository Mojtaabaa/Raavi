using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Raavi.Infrastructure.Messaging;
using Raavi.Tests.Identity;
using Raavi.Tests.Infrastructure;
using Raavi.Wiki.Persistence;

namespace Raavi.Tests.Wiki;

/// <summary>Wiki scenario helpers over the public HTTP API (same calls the SPA makes).</summary>
public sealed class WikiTestKit(SqlServerFixture fx)
{
    public IdentityTestKit Identity { get; } = new(fx);

    public static object Doc(params string[] paragraphs) => new
    {
        schema = "raavi.doc",
        schemaVersion = 1,
        doc = new
        {
            type = "doc",
            content = paragraphs.Select(p => new { type = "paragraph", content = new[] { new { type = "text", text = p } } }).ToArray(),
        },
    };

    public static string UniqueKey() => "S" + SqlServerFixture.Unique("").ToUpperInvariant();

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {text}");
        return JsonDocument.Parse(text).RootElement.Clone();
    }

    public static async Task<JsonElement> CreateSpaceAsync(Browser browser, string? key = null, string visibility = "Open", string name = "Engineering")
    {
        ArgumentNullException.ThrowIfNull(browser);
        using var response = await browser.PostAsync("api/v1/spaces", new { key = key ?? UniqueKey(), name, description = "d", type = "Team", visibility });
        return await ReadAsync(response);
    }

    public static async Task<JsonElement> CreatePageAsync(Browser browser, string spaceKey, string title, Guid? parentId = null, object? body = null, string? contentType = null, Guid? templateId = null)
    {
        ArgumentNullException.ThrowIfNull(browser);
        using var response = await browser.PostAsync($"api/v1/spaces/{spaceKey}/content", new { title, parentId, body = body ?? Doc("hello"), contentType, templateId });
        return await ReadAsync(response);
    }

    public static async Task<(JsonElement Body, string ETag)> GetWithETagAsync(Browser browser, string path)
    {
        ArgumentNullException.ThrowIfNull(browser);
        using var response = await browser.GetAsync(path);
        var body = await ReadAsync(response);
        return (body, response.Headers.ETag!.Tag);
    }

    public static async Task<HttpResponseMessage> UpdatePageAsync(Browser browser, Guid id, string? etag, string title, object? body)
    {
        ArgumentNullException.ThrowIfNull(browser);
        return await browser.SendAsync(HttpMethod.Put, $"api/v1/content/{id}", new { title, body, changeSummary = (string?)null }, withXsrf: true, ifMatch: etag);
    }

    public static async Task<HttpResponseMessage> ConditionalAsync(Browser browser, HttpMethod method, string path, object? body = null)
    {
        ArgumentNullException.ThrowIfNull(browser);
        var etagPath = path.Contains("/content/", StringComparison.Ordinal)
            ? path[..(path.IndexOf("/content/", StringComparison.Ordinal) + 9 + 36)]
            : path[..path.LastIndexOf('/')];
        var (_, etag) = await GetWithETagAsync(browser, etagPath);
        return await browser.SendAsync(method, path, body, withXsrf: true, ifMatch: etag);
    }

    public static async Task<HttpResponseMessage> MoveAsync(Browser browser, Guid id, string? spaceKey, Guid? parentId, int? position) =>
        await ConditionalAsync(browser, HttpMethod.Post, $"api/v1/content/{id}/move", new { spaceKey, parentId, position });

    public static async Task AllowMembersToCreateSpacesAsync(Browser admin)
    {
        ArgumentNullException.ThrowIfNull(admin);
        using var response = await admin.PutAsync("api/v1/tenant/permissions", new { grants = new[] { new { subjectType = "AllMembers", subjectId = (Guid?)null, action = "Create", effect = "Allow" } } });
        await Browser.AssertStatusAsync(response, HttpStatusCode.OK);
    }

    public static async Task<string[]> TreeTitlesAsync(Browser browser, string spaceKey, Guid? parentId = null)
    {
        ArgumentNullException.ThrowIfNull(browser);
        var tree = await browser.GetJsonAsync($"api/v1/spaces/{spaceKey}/tree" + (parentId is null ? string.Empty : $"?parentId={parentId}"));
        return tree.EnumerateArray().Select(n => n.GetProperty("title").GetString()!).ToArray();
    }

    public async Task<int> DispatchOutboxAsync()
    {
        var dispatcher = fx.Services.GetRequiredService<OutboxDispatcher<WikiDbContext>>();
        var total = 0;
        int n;
        while ((n = await dispatcher.DispatchOnceAsync(500)) > 0)
        {
            total += n;
        }

        return total;
    }

    public static Guid Id(JsonElement e) => e.GetProperty("id").GetGuid();
}
