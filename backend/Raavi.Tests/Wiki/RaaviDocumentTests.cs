using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Raavi.Domain.Common;
using Raavi.Wiki.Domain;
using Raavi.Wiki.Editor;

namespace Raavi.Tests.Wiki;

/// <summary>Server side of the editor contract: manifests, fixtures (shared with the frontend), validation, migration, rendering.</summary>
public sealed class RaaviDocumentTests
{
    public static string RepoRoot
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "schema", "editor")))
            {
                dir = dir.Parent;
            }

            return dir?.FullName ?? throw new InvalidOperationException("schema/editor not found above the test output directory.");
        }
    }

    public static TheoryData<string> ManifestIds()
    {
        var data = new TheoryData<string>();
        foreach (var m in EditorSchema.Current.Manifests.OrderBy(m => m.Id, StringComparer.Ordinal))
        {
            data.Add(m.Id);
        }

        return data;
    }

    private static string Fixture(string id) => File.ReadAllText(Path.Combine(RepoRoot, "schema", "editor", "fixtures", id + ".json"));

    private static RaaviDocument Doc(string docJson) => RaaviDocument.Parse($$"""{"schema":"raavi.doc","schemaVersion":{{RaaviDocument.CurrentSchemaVersion}},"doc":{{docJson}}}""");

    private static string P(string inner) => $$"""{"type":"paragraph","content":[{{inner}}]}""";

    private static string D(params string[] blocks) => $$"""{"type":"doc","content":[{{string.Join(",", blocks)}}]}""";

    [Fact]
    public void Registry_loads_every_manifest_and_every_manifest_has_a_fixture()
    {
        var schema = EditorSchema.Current;
        Assert.Equal(4, schema.Info.SchemaVersion);
        Assert.True(schema.Manifests.Count >= 36);
        var onDisk = Directory.GetFiles(Path.Combine(RepoRoot, "schema", "editor", "extensions"), "*.json").Select(f => Path.GetFileNameWithoutExtension(f)!).Order().ToList();
        Assert.Equal(onDisk, schema.Manifests.Select(m => m.Id).Order().ToList()); // embedded == repository
        foreach (var m in schema.Manifests)
        {
            Assert.True(File.Exists(Path.Combine(RepoRoot, "schema", "editor", "fixtures", m.Id + ".json")), $"missing fixture for {m.Id}");
        }
    }

    [Theory]
    [MemberData(nameof(ManifestIds))]
    public void Fixture_round_trips_unchanged_and_renders(string id)
    {
        var json = Fixture(id);
        var doc = RaaviDocument.Parse(json);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(doc.Json)), $"{id}: canonical form differs\n{doc.Json}");
        Assert.Equal(doc.Json, RaaviDocument.Parse(doc.Json).Json); // idempotent
        var html = new DocumentHtmlRenderer().Render(doc.Doc);
        Assert.Contains("end", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("""{"type":"doc","content":[{"type":"script"}]}""")] // unknown node
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","attrs":{"onclick":"x"}}]}""")] // unknown attribute
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"a","marks":[{"type":"blink"}]}]}]}""")] // unknown mark
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"a","marks":[{"type":"link","attrs":{"href":"javascript:alert(1)"}}]}]}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"a","marks":[{"type":"link","attrs":{"href":"data:text/html,<script>"}}]}]}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"a","marks":[{"type":"link","attrs":{"href":"//evil.example"}}]}]}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"a","marks":[{"type":"link","attrs":{"href":" javascript:x"}}]}]}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"a","marks":[{"type":"link"}]}]}]}""")] // href required
    [InlineData("""{"type":"doc","content":[{"type":"listItem","content":[{"type":"paragraph"}]}]}""")] // illegal nesting
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"paragraph"}]}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"table","content":[{"type":"paragraph"}]}]}""")]
    [InlineData("""{"type":"doc","content":[]}""")] // doc needs block+
    [InlineData("""{"type":"doc","content":[{"type":"codeBlock","content":[{"type":"text","text":"x","marks":[{"type":"bold"}]}]}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"heading","attrs":{"level":9}}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"panel","attrs":{"type":"rainbow"},"content":[{"type":"paragraph"}]}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"image","attrs":{"attachmentId":"../../etc/passwd"}}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":"a","marks":[{"type":"textStyle","attrs":{"color":"red;background:url(x)"}}]}]}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","content":[{"type":"text","text":""}]}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"horizontalRule","content":[{"type":"paragraph"}]}]}""")]
    [InlineData("""{"type":"doc","content":[{"type":"paragraph","html":"<b>x</b>"}]}""")]
    public void Invalid_documents_are_rejected(string doc)
    {
        var ex = Assert.Throws<DomainException>(() => Doc(doc));
        Assert.Equal("content.body_invalid", ex.Code);
    }

    [Fact]
    public void Deeply_nested_documents_are_rejected()
    {
        var inner = P("""{"type":"text","text":"x"}""");
        for (var i = 0; i < 60; i++)
        {
            inner = $$"""{"type":"blockquote","content":[{{inner}}]}""";
        }

        Assert.Equal("content.body_invalid", Assert.Throws<DomainException>(() => Doc(D(inner))).Code);
    }

    [Fact]
    public void Version_one_documents_are_upgraded_on_read()
    {
        var v1 = """{"schema":"raavi.doc","schemaVersion":1,"doc":{"type":"doc","content":[{"type":"paragraph","attrs":{"dir":"rtl"},"content":[{"type":"text","text":"سلام"}]}]}}""";
        var doc = RaaviDocument.Parse(v1);
        var root = JsonNode.Parse(doc.Json)!;
        Assert.Equal(4, root["schemaVersion"]!.GetValue<int>());
        var attrs = root["doc"]!["content"]![0]!["attrs"]!;
        Assert.Equal("rtl", attrs["dir"]!.GetValue<string>());
        Assert.Null(attrs["textAlign"]);
        Assert.Equal("content.schema_unsupported",
            Assert.Throws<DomainException>(() => RaaviDocument.Parse("""{"schema":"raavi.doc","schemaVersion":99,"doc":{"type":"doc","content":[]}}""")).Code);

        // Phase 3 stored empty documents (e.g. space home pages) that v2+'s "block+" rejects: they read as one empty paragraph.
        Assert.Equal(RaaviDocument.Empty.Json, RaaviDocument.Parse("""{"schema":"raavi.doc","schemaVersion":1,"doc":{"type":"doc","content":[]}}""").Json);
        Assert.Equal(RaaviDocument.Empty.Json, RaaviDocument.Parse("""{"schema":"raavi.doc","schemaVersion":1,"doc":{"type":"doc"}}""").Json);
        Assert.Throws<DomainException>(() => RaaviDocument.Parse("""{"schema":"raavi.doc","schemaVersion":4,"doc":{"type":"doc","content":[]}}"""));
    }

    [Fact]
    public void Attribute_defaults_are_applied_and_output_never_echoes_client_bytes()
    {
        var doc = Doc(D("""{"type":"heading","content":[{"type":"text","text":"T"}]}""", """{"type":"panel","content":[{"type":"paragraph"}]}"""));
        var root = JsonNode.Parse(doc.Json)!["doc"]!["content"]!;
        Assert.Equal(1, root[0]!["attrs"]!["level"]!.GetValue<int>());
        Assert.Equal("info", root[1]!["attrs"]!["type"]!.GetValue<string>());
    }

    [Fact]
    public void Plain_text_is_extracted_per_extension()
    {
        var doc = Doc(D(
            P("""{"type":"text","text":"Hello "},{"type":"status","attrs":{"text":"DONE","color":"green"}}"""),
            """{"type":"mermaid","attrs":{"source":"graph TD"}}""",
            """{"type":"codeBlock","content":[{"type":"text","text":"x = 1"}]}"""));
        Assert.Contains("Hello DONE", doc.PlainText, StringComparison.Ordinal);
        Assert.Contains("graph TD", doc.PlainText, StringComparison.Ordinal);
        Assert.Contains("x = 1", doc.PlainText, StringComparison.Ordinal);
        Assert.Equal("first\nsecond", RaaviDocument.FromPlainText("first\r\nsecond\u0007").PlainText);
    }

    [Fact]
    public void Html_rendering_encodes_text_and_attributes_and_keeps_links_safe()
    {
        var doc = Doc(D(
            P("""{"type":"text","text":"<img src=x onerror=alert(1)>"}"""),
            P("""{"type":"text","text":"out","marks":[{"type":"link","attrs":{"href":"https://example.com/?a=1&b=\"><script>"}}]}"""),
            """{"type":"expand","attrs":{"title":"<b>t</b>"},"content":[{"type":"paragraph"}]}""",
            """{"type":"mermaid","attrs":{"source":"<script>alert(1)</script>"}}""",
            P("""{"type":"mention","attrs":{"id":"0b9f6a3c-1d2e-4f50-8a71-92b3c4d5e6f7","label":"Old name"}}"""),
            """{"type":"heading","attrs":{"level":2},"content":[{"type":"text","text":"Intro"}]}""",
            """{"type":"toc","attrs":{"maxLevel":3}}"""));
        var html = new DocumentHtmlRenderer().Render(doc.Doc, new Dictionary<Guid, string> { [Guid.Parse("0b9f6a3c-1d2e-4f50-8a71-92b3c4d5e6f7")] = "Sara" });
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.DoesNotContain("<script", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<b>", html, StringComparison.Ordinal);
        Assert.Contains("rel=\"noopener noreferrer nofollow\"", html, StringComparison.Ordinal);
        Assert.Contains("@Sara", html, StringComparison.Ordinal); // mention names resolved at render time
        Assert.Contains("href=\"#h-1\"", html, StringComparison.Ordinal); // table of contents
        Assert.Contains("<h2 id=\"h-1\"", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://example.com", true)]
    [InlineData("mailto:a@b.c", true)]
    [InlineData("/spaces/ENG", true)]
    [InlineData("#section", true)]
    [InlineData("raavi:content/1", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("JAVASCRIPT:alert(1)", false)]
    [InlineData("vbscript:x", false)]
    [InlineData("data:image/png;base64,AA", false)]
    [InlineData("//evil.example", false)]
    [InlineData("/\\evil.example", false)]
    [InlineData("ftp://x", false)]
    [InlineData("vianna/UI/TMS/Personnel/Current-classes.html", true)]
    [InlineData("evil/UI/x", false)]
    public void Link_targets_use_an_allow_list(string href, bool allowed)
    {
        LinkPathRewrites.Configure(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Raavi:Links:PathRewrites:0:Prefix"] = "vianna",
                ["Raavi:Links:PathRewrites:0:BaseUrl"] = "https://svn.viannacloud.ir",
            }).Build());
        Assert.Equal(allowed, EditorSchema.IsSafeHref(href));
    }

    [Fact]
    public void Path_prefix_hrefs_rewrite_to_the_configured_base()
    {
        LinkPathRewrites.Configure(new Microsoft.Extensions.Configuration.ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Raavi:Links:PathRewrites:0:Prefix"] = "vianna",
                ["Raavi:Links:PathRewrites:0:BaseUrl"] = "https://svn.viannacloud.ir",
            }).Build());
        Assert.True(LinkPathRewrites.TryRewrite("vianna/UI/TMS/Personnel/Current-classes.html", out var url));
        Assert.Equal("https://svn.viannacloud.ir/UI/TMS/Personnel/Current-classes.html", url);
        var doc = Doc(D(P("""{"type":"text","text":"file","marks":[{"type":"link","attrs":{"href":"vianna/UI/a.html"}}]}""")));
        var html = new DocumentHtmlRenderer().Render(doc.Doc, new Dictionary<Guid, string>());
        Assert.Contains("href=\"https://svn.viannacloud.ir/UI/a.html\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("href=\"vianna/", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Release notes 2026", "release-notes-2026")]
    [InlineData("  راهنمای   نصب  ", "راهنمای-نصب")]
    [InlineData("C# / .NET!", "c-net")]
    public void Slugs_keep_letters_of_any_script(string title, string slug) => Assert.Equal(slug, ContentNode.Slugify(ContentNode.NormalizeTitle(title)));

    [Theory]
    [InlineData("A_B")]
    [InlineData("1ENG")]
    [InlineData("EN G")]
    [InlineData("E")]
    public void Invalid_space_keys_are_rejected(string key) =>
        Assert.Equal("space.key_invalid", Assert.Throws<DomainException>(() => Space.Create(key, "n", null, SpaceType.Team, Guid.NewGuid(), DateTimeOffset.UtcNow)).Code);

    [Fact]
    public void Attachment_types_are_sniffed_from_content_never_trusted()
    {
        Assert.Equal("image/png", Attachment.Sniff([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0]));
        Assert.Equal("image/jpeg", Attachment.Sniff([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("application/octet-stream", Attachment.Sniff("<svg xmlns=\"http://www.w3.org/2000/svg\"><script/></svg>"u8));
        Assert.Equal("application/octet-stream", Attachment.Sniff("<html><script>"u8));
        Assert.Equal("گزارش.pdf", Attachment.CleanFileName("..\\..\\گزارش.pdf"));
        Assert.Equal("file", Attachment.CleanFileName("\u0000"));
    }

    [Fact]
    public void Malformed_json_is_a_validation_error() =>
        Assert.Equal("content.body_invalid", Assert.Throws<DomainException>(() => RaaviDocument.Parse("{\"schema\":")).Code);

    [Fact]
    public void Json_options_are_irrelevant_to_canonical_form()
    {
        using var a = JsonDocument.Parse("""{ "schemaVersion": 4, "doc": { "content": [ { "type": "paragraph" } ], "type": "doc" }, "schema": "raavi.doc" }""");
        Assert.Equal(RaaviDocument.Empty.Json, RaaviDocument.Parse(a.RootElement).Json);
    }
}
