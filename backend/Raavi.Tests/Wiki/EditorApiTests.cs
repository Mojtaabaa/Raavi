using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Raavi.Tests.Infrastructure;
using static Raavi.Tests.Wiki.WikiTestKit;

namespace Raavi.Tests.Wiki;

/// <summary>Phase 4 acceptance over HTTP: every extension survives save → reopen, server HTML, attachments-lite.</summary>
[Collection(SqlTestGroup.Name)]
public sealed class EditorApiTests(SqlServerFixture fx)
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52];

    private readonly WikiTestKit _kit = new(fx);

    /// <summary>All fixture blocks (shared with the frontend) concatenated into one document.</summary>
    private static JsonObject EverythingDocument()
    {
        var content = new JsonArray();
        foreach (var file in Directory.GetFiles(Path.Combine(RaaviDocumentTests.RepoRoot, "schema", "editor", "fixtures"), "*.json").Order(StringComparer.Ordinal))
        {
            var blocks = JsonNode.Parse(File.ReadAllText(file))!["doc"]!["content"]!.AsArray();
            foreach (var block in blocks.Take(blocks.Count - 1))
            {
                content.Add(block!.DeepClone());
            }
        }

        content.Add(JsonNode.Parse("""{"type":"paragraph","attrs":{"textAlign":null,"dir":null},"content":[{"type":"text","text":"end"}]}"""));
        return new JsonObject { ["schema"] = "raavi.doc", ["schemaVersion"] = Raavi.Wiki.Domain.RaaviDocument.CurrentSchemaVersion, ["doc"] = new JsonObject { ["type"] = "doc", ["content"] = content } };
    }

    [Fact]
    public async Task Every_extension_survives_save_and_reopen_and_renders_safely()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var id = Id(await CreatePageAsync(admin, key, "Everything"));
        var (_, etag) = await GetWithETagAsync(admin, $"api/v1/content/{id}");
        var everything = EverythingDocument();

        using (var save = await UpdatePageAsync(admin, id, etag, "Everything", JsonDocument.Parse(everything.ToJsonString()).RootElement))
        {
            await ReadAsync(save);
        }

        var reopened = await admin.GetJsonAsync($"api/v1/content/{id}");
        Assert.True(JsonNode.DeepEquals(everything, JsonNode.Parse(reopened.GetProperty("body").GetRawText())), "document changed between save and reopen");

        var html = (await admin.GetJsonAsync($"api/v1/content/{id}/html")).GetProperty("html").GetString()!;
        foreach (var expected in new[] { "<table", "raavi-panel-warning", "<details", "raavi-status-blue", "raavi-issue", "acme/wiki#42", "raavi-mermaid", "raavi-math", "<pre dir=\"ltr\"><code class=\"language-csharp\"", "data-checked=\"true\"", "/api/v1/attachments/", "rel=\"noopener noreferrer nofollow\"", "raavi-unsupported" })
        {
            Assert.Contains(expected, html, StringComparison.Ordinal);
        }

        Assert.DoesNotContain("<ac:structured-macro", html, StringComparison.Ordinal); // unsupported source stays escaped text

        var schema = await admin.GetJsonAsync("api/v1/editor/schema");
        Assert.Equal(4, schema.GetProperty("schemaVersion").GetInt32());
        Assert.Contains(schema.GetProperty("extensions").EnumerateArray(), e => e.GetProperty("id").GetString() == "raavi.panel");
        Assert.Contains(schema.GetProperty("extensions").EnumerateArray(), e => e.GetProperty("id").GetString() == "core.subscript");
    }

    [Fact]
    public async Task Pages_saved_in_phase_3_format_are_upgraded_when_read()
    {
        var (_, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = await CreatePageAsync(admin, key, "Old", body: Doc("v1 text")); // Doc() builds a schemaVersion 1 document
        Assert.Equal(4, page.GetProperty("body").GetProperty("schemaVersion").GetInt32());
        Assert.Equal("v1 text", page.GetProperty("body").GetProperty("doc").GetProperty("content")[0].GetProperty("content")[0].GetProperty("text").GetString());
    }

    [Fact]
    public async Task Attachments_are_sniffed_bounded_and_follow_content_permissions()
    {
        var (tenant, admin, _) = await _kit.Identity.TenantWithAdminAsync();
        var (member, _, _) = await _kit.Identity.AddMemberAsync(tenant, admin);
        var key = (await CreateSpaceAsync(admin)).GetProperty("key").GetString()!;
        var page = Id(await CreatePageAsync(admin, key, "With files"));

        var image = await UploadAsync(admin, page, Png, "diagram.png", "text/html");
        Assert.Equal(HttpStatusCode.Created, image.Status);
        Assert.Equal("image/png", image.Body.GetProperty("mediaType").GetString()); // client Content-Type ignored
        var imageId = Id(image.Body);

        using (var get = await member.GetAsync($"api/v1/attachments/{imageId}"))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            Assert.Equal("image/png", get.Content.Headers.ContentType!.MediaType);
            Assert.Equal("nosniff", get.Headers.GetValues("X-Content-Type-Options").Single());
            Assert.Contains("sandbox", get.Headers.GetValues("Content-Security-Policy").Single(), StringComparison.Ordinal);
            Assert.Equal(Png, await get.Content.ReadAsByteArrayAsync());
        }

        // An SVG (scriptable) is never served as an image.
        var svg = await UploadAsync(admin, page, "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray(), "x.svg", "image/svg+xml");
        using (var get = await member.GetAsync($"api/v1/attachments/{Id(svg.Body)}"))
        {
            Assert.Equal("application/octet-stream", get.Content.Headers.ContentType!.MediaType);
            Assert.Equal("attachment", get.Content.Headers.ContentDisposition!.DispositionType);
        }

        var tooBig = await UploadAsync(admin, page, new byte[1048577], "big.bin", "application/octet-stream");
        Assert.Equal(HttpStatusCode.BadRequest, tooBig.Status);
        Assert.Equal("attachment.too_large", tooBig.Body.GetProperty("code").GetString());

        // Members without Edit cannot upload; restricted pages hide their files (404).
        var adminId = (await admin.GetJsonAsync("api/v1/auth/me")).GetProperty("id").GetGuid();
        using (var restrict = await admin.PutAsync($"api/v1/content/{page}/restrictions", new { view = new[] { new { subjectType = "User", subjectId = adminId } }, edit = Array.Empty<object>() }))
        {
            await ReadAsync(restrict);
        }

        using (var hidden = await member.GetAsync($"api/v1/attachments/{imageId}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }

        var openPage = Id(await CreatePageAsync(admin, key, "Locked for edit"));
        using (var lockEdit = await admin.PutAsync($"api/v1/content/{openPage}/restrictions", new { view = Array.Empty<object>(), edit = new[] { new { subjectType = "User", subjectId = adminId } } }))
        {
            await ReadAsync(lockEdit);
        }

        var denied = await UploadAsync(member, openPage, Png, "a.png", "image/png");
        Assert.Equal(HttpStatusCode.Forbidden, denied.Status);

        // Another tenant cannot fetch the file even with its id.
        var (_, otherAdmin, _) = await _kit.Identity.TenantWithAdminAsync();
        using var foreign = await otherAdmin.GetAsync($"api/v1/attachments/{imageId}");
        Assert.Equal(HttpStatusCode.NotFound, foreign.StatusCode);
    }

    private static async Task<(HttpStatusCode Status, JsonElement Body)> UploadAsync(Browser browser, Guid contentId, byte[] data, string fileName, string contentType)
    {
        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(data);
        file.Headers.ContentType = MediaTypeHeaderValue.Parse(contentType);
        form.Add(file, "file", fileName);
        using var response = await browser.SendAsync(HttpMethod.Post, $"api/v1/content/{contentId}/attachments", form, withXsrf: true);
        var text = await response.Content.ReadAsStringAsync();
        return (response.StatusCode, text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone());
    }
}
