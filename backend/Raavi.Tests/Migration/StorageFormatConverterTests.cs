using System.Text.Json.Nodes;
using Raavi.Migration.Confluence;
using Raavi.Wiki.Domain;

namespace Raavi.Tests.Migration;

/// <summary>Confluence storage format → Raavi document. Every result must also pass the real editor-schema validation.</summary>
public sealed class StorageFormatConverterTests
{
    /// <summary>Converts and validates the result with the editor registry (the same check the importer performs before saving).</summary>
    private static (StorageConversion Result, RaaviDocument Valid) Convert(string storage, ConversionContext? context = null)
    {
        var result = StorageFormatConverter.Convert(storage, context);
        var envelope = new JsonObject { ["schema"] = "raavi.doc", ["schemaVersion"] = RaaviDocument.CurrentSchemaVersion, ["doc"] = result.Doc.DeepClone() };
        return (result, RaaviDocument.Parse(envelope.ToJsonString()));
    }

    private static IEnumerable<JsonNode> Walk(JsonNode node)
    {
        yield return node;
        if (node["content"] is JsonArray children)
        {
            foreach (var child in children)
            {
                foreach (var d in Walk(child!))
                {
                    yield return d;
                }
            }
        }
    }

    private static string[] Types(StorageConversion r) => [.. Walk(r.Doc).Select(n => (string)n["type"]!)];

    [Fact]
    public void Basic_formatting_headings_and_entities()
    {
        var (r, v) = Convert("<h1>عنوان</h1><p>سلام <strong>دنیا</strong> و <em>تو</em>&nbsp;&mdash; <u>x</u><s>y</s> <code>z</code><br/>خط بعد</p><hr/>");
        Assert.Equal(["heading", "paragraph", "horizontalRule"], r.Doc["content"]!.AsArray().Select(n => (string)n!["type"]!));
        Assert.Contains("hardBreak", Types(r));
        Assert.Contains("سلام", v.PlainText, StringComparison.Ordinal);
        Assert.Contains("—", v.PlainText, StringComparison.Ordinal);
        var marks = Walk(r.Doc).Where(n => n["marks"] is not null).SelectMany(n => n["marks"]!.AsArray().Select(m => (string)m!["type"]!)).ToHashSet();
        Assert.True(marks.IsSupersetOf(["bold", "italic", "underline", "strike", "code"]));
    }

    [Fact]
    public void Lists_wrap_loose_text_in_paragraphs_and_keep_nesting()
    {
        var (r, v) = Convert("<ul><li>one</li><li>two<ul><li>nested</li></ul></li></ul><ol><li><p>first</p></li></ol>");
        Assert.Equal(["bulletList", "orderedList"], r.Doc["content"]!.AsArray().Select(n => (string)n!["type"]!));
        Assert.Contains("nested", v.PlainText, StringComparison.Ordinal);
        Assert.Equal(2, Walk(r.Doc).Count(n => (string)n["type"]! == "bulletList"));
    }

    [Fact]
    public void Tables_become_header_and_data_cells_with_spans()
    {
        var (r, _) = Convert("<table><tbody><tr><th>H</th><th colspan=\"2\">H2</th></tr><tr><td>a</td><td>b</td><td><ul><li>c</li></ul></td></tr></tbody></table>");
        Assert.Equal(2, Walk(r.Doc).Count(n => (string)n["type"]! == "tableHeader"));
        Assert.Equal(3, Walk(r.Doc).Count(n => (string)n["type"]! == "tableCell"));
        Assert.Equal(2, Walk(r.Doc).First(n => (string?)n["content"]?[0]?["content"]?[0]?["text"] == "H2")["attrs"]!["colspan"]!.GetValue<int>());
    }

    [Fact]
    public void Panels_code_expand_status_and_toc_macros_are_converted()
    {
        const string storage = """
            <ac:structured-macro ac:name="tip"><ac:parameter ac:name="title">Hint</ac:parameter><ac:rich-text-body><p>Use it</p></ac:rich-text-body></ac:structured-macro>
            <ac:structured-macro ac:name="info"><ac:rich-text-body><p>FYI</p></ac:rich-text-body></ac:structured-macro>
            <ac:structured-macro ac:name="code"><ac:parameter ac:name="language">Python</ac:parameter><ac:plain-text-body><![CDATA[print("a < b")]]></ac:plain-text-body></ac:structured-macro>
            <ac:structured-macro ac:name="expand"><ac:parameter ac:name="title">More</ac:parameter><ac:rich-text-body><p>hidden</p></ac:rich-text-body></ac:structured-macro>
            <p>State: <ac:structured-macro ac:name="status"><ac:parameter ac:name="title">DONE</ac:parameter><ac:parameter ac:name="colour">Green</ac:parameter></ac:structured-macro></p>
            <ac:structured-macro ac:name="toc"><ac:parameter ac:name="maxLevel">2</ac:parameter></ac:structured-macro>
            """;
        var (r, _) = Convert(storage);
        var panels = Walk(r.Doc).Where(n => (string)n["type"]! == "panel").Select(n => (string)n["attrs"]!["type"]!).ToArray();
        Assert.Equal(["success", "info"], panels);
        Assert.Equal("python", Walk(r.Doc).First(n => (string)n["type"]! == "codeBlock")["attrs"]!["language"]!.GetValue<string>());
        Assert.Contains("a < b", Walk(r.Doc).First(n => (string)n["type"]! == "codeBlock")["content"]![0]!["text"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("More", Walk(r.Doc).First(n => (string)n["type"]! == "expand")["attrs"]!["title"]!.GetValue<string>());
        var status = Walk(r.Doc).First(n => (string)n["type"]! == "status")["attrs"]!;
        Assert.Equal("DONE", status["text"]!.GetValue<string>());
        Assert.Equal("green", status["color"]!.GetValue<string>());
        Assert.Equal(2, Walk(r.Doc).First(n => (string)n["type"]! == "toc")["attrs"]!["maxLevel"]!.GetValue<int>());
        Assert.Empty(r.Warnings);
    }

    [Fact]
    public void Task_lists_keep_their_state()
    {
        var (r, _) = Convert("<ac:task-list><ac:task><ac:task-id>1</ac:task-id><ac:task-status>complete</ac:task-status><ac:task-body>done thing</ac:task-body></ac:task><ac:task><ac:task-id>2</ac:task-id><ac:task-status>incomplete</ac:task-status><ac:task-body><span>todo</span></ac:task-body></ac:task></ac:task-list>");
        Assert.Equal([true, false], Walk(r.Doc).Where(n => (string)n["type"]! == "taskItem").Select(n => n["attrs"]!["checked"]!.GetValue<bool>()));
    }

    [Fact]
    public void Images_inside_paragraphs_are_lifted_and_attachments_recorded_for_the_link_pass()
    {
        var (r, _) = Convert("<p>before <ac:image ac:alt=\"Diagram\"><ri:attachment ri:filename=\"d.png\"/></ac:image> after</p>");
        Assert.Equal(["paragraph", "image", "paragraph"], r.Doc["content"]!.AsArray().Select(n => (string)n!["type"]!));
        Assert.Null(r.Doc["content"]![1]!["attrs"]!["attachmentId"]);
        Assert.Equal("Diagram", r.Doc["content"]![1]!["attrs"]!["alt"]!.GetValue<string>());
        var reference = Assert.Single(r.References);
        Assert.Equal((ReferenceKind.Attachment, "d.png"), (reference.Kind, reference.FileName));
    }

    [Fact]
    public void Attachment_ids_are_deterministic_per_workspace_source_page_and_file()
    {
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();
        var body = "<ac:image><ri:attachment ri:filename=\"a.png\"/></ac:image>";
        string? IdOf(Guid tenant, string system, string page)
        {
            var (r, _) = Convert(body, new ConversionContext(tenant, system, page));
            return (string?)r.Doc["content"]![0]!["attrs"]!["attachmentId"];
        }

        var id = IdOf(tenantA, "confluence:abc", "10");
        Assert.Equal(id, IdOf(tenantA, "confluence:abc", "10")); // stable: re-imports hit the same row
        Assert.Equal(Raavi.Application.Abstractions.Content.ImportIds.Attachment(tenantA, "confluence:abc", "10", "a.png").ToString("D"), id);
        Assert.NotEqual(id, IdOf(tenantB, "confluence:abc", "10")); // two workspaces never collide
        Assert.NotEqual(id, IdOf(tenantA, "confluence:other", "10"));
        Assert.NotEqual(id, IdOf(tenantA, "confluence:abc", "11"));
        Assert.NotNull(id); // present whenever the converter knows the page

        // Without a context (or for another page's file) there is no id; the image stays valid.
        var (plain, _) = Convert(body);
        Assert.Null((string?)plain.Doc["content"]![0]!["attrs"]!["attachmentId"]);
        var (foreign, _) = Convert("<ac:image><ri:attachment ri:filename=\"a.png\"><ri:page ri:content-title=\"Other\"/></ri:attachment></ac:image>", new ConversionContext(tenantA, "confluence:abc", "10"));
        Assert.Null((string?)foreign.Doc["content"]![0]!["attrs"]!["attachmentId"]);
        Assert.Contains(foreign.Warnings, w => w.Code == "image.other_page");
    }

    [Fact]
    public void Page_links_become_placeholders_resolved_later_and_unsafe_links_are_dropped()
    {
        var (r, _) = Convert("""
            <p><ac:link><ri:page ri:content-title="راهنما" ri:space-key="ENG"/><ac:plain-text-link-body><![CDATA[see guide]]></ac:plain-text-link-body></ac:link>
            <ac:link><ri:user ri:userkey="abc123"/></ac:link>
            <a href="https://example.com/x">web</a> <a href="javascript:alert(1)">bad</a></p>
            """);
        var reference = r.References.First(x => x.Kind == ReferenceKind.Page);
        Assert.Equal(("ENG", "راهنما"), (reference.SpaceKey, reference.Title));
        var hrefs = Walk(r.Doc).Where(n => n["marks"] is not null).SelectMany(n => n["marks"]!.AsArray()).Select(m => (string?)m!["attrs"]?["href"]).ToArray();
        Assert.Contains(hrefs, h => h!.StartsWith(StorageFormatConverter.PageLinkPrefix, StringComparison.Ordinal) && h.Contains("space=ENG", StringComparison.Ordinal));
        Assert.Contains("https://example.com/x", hrefs);
        Assert.DoesNotContain(hrefs, h => h!.Contains("javascript", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(r.References, x => x is { Kind: ReferenceKind.User, UserKey: "abc123" });
        Assert.Contains(r.Warnings, w => w.Code == "link.unsafe");
    }

    [Fact]
    public void Unknown_macros_are_kept_as_escaped_text_counted_and_reported()
    {
        const string storage = "<p>x</p><ac:structured-macro ac:name=\"contentbylabel\"><ac:parameter ac:name=\"cql\">label = &quot;a&quot; and type = page</ac:parameter></ac:structured-macro><ac:structured-macro ac:name=\"contentbylabel\"/><ac:structured-macro ac:name=\"jira\"/>";
        var (r, _) = Convert(storage);
        Assert.Equal(2, r.UnsupportedMacros["contentbylabel"]);
        Assert.Equal(1, r.UnsupportedMacros["jira"]);
        var node = Walk(r.Doc).First(n => (string)n["type"]! == "unsupported");
        Assert.Equal("confluence:macro:contentbylabel", node["attrs"]!["sourceType"]!.GetValue<string>());
        Assert.Contains("cql", node["attrs"]!["source"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Contains(r.Warnings, w => w is { Code: "macro.unsupported", MacroName: "contentbylabel" });
    }

    private static string Macro(string name, params (string Name, string Value)[] parameters) =>
        $"<ac:structured-macro ac:name=\"{name}\">" + string.Concat(parameters.Select(p => $"<ac:parameter ac:name=\"{p.Name}\">{p.Value}</ac:parameter>")) + "</ac:structured-macro>";

    [Fact]
    public void Children_and_content_by_label_macros_become_dynamic_lists_when_they_can_be_expressed()
    {
        var (a, _) = Convert(Macro("children") + Macro("children", ("depth", "3")) + Macro("children", ("all", "true")));
        Assert.Equal([1, 3, 5], a.Doc["content"]!.AsArray().Select(n => n!["attrs"]!["depth"]!.GetValue<int>()));
        Assert.Equal("children", (string)a.Doc["content"]![0]!["type"]!);

        var (b, _) = Convert(
            Macro("contentbylabel", ("labels", "HowTo, runbook"), ("max", "7")) +
            Macro("contentbylabel", ("cql", "label = &quot;faq&quot;"), ("spaces", "@self")) +
            Macro("contentbylabel", ("cql", "label in (&quot;a&quot;,&quot;b&quot;)")));
        // As found in real Confluence data: the page's own space is named by key.
        var (real, _) = Convert(Macro("contentbylabel", ("spaces", "IN"), ("showLabels", "false"), ("sort", "title"), ("labels", "featured"), ("showSpace", "false"), ("type", "page")), new ConversionContext(Guid.NewGuid(), "confluence:x", "1", "in"));
        Assert.Equal("contentByLabel", (string)real.Doc["content"]![0]!["type"]!);
        Assert.Equal("featured", (string)real.Doc["content"]![0]!["attrs"]!["labels"]!);
        Assert.True(real.Doc["content"]![0]!["attrs"]!["currentSpaceOnly"]!.GetValue<bool>());
        var (other, _) = Convert(Macro("contentbylabel", ("spaces", "OTHER"), ("labels", "featured")), new ConversionContext(Guid.NewGuid(), "confluence:x", "1", "in"));
        Assert.Equal("unsupported", (string)other.Doc["content"]![0]!["type"]!); // another space cannot be represented
        var nodes = b.Doc["content"]!.AsArray();
        Assert.Equal(["howto,runbook", "faq", "a,b"], nodes.Select(n => (string)n!["attrs"]!["labels"]!));
        Assert.Equal([7, 20, 20], nodes.Select(n => n!["attrs"]!["maxItems"]!.GetValue<int>()));
        Assert.Equal([false, true, false], nodes.Select(n => n!["attrs"]!["currentSpaceOnly"]!.GetValue<bool>()));
        Assert.Empty(a.UnsupportedMacros);
        Assert.Empty(b.UnsupportedMacros);
    }

    [Fact]
    public void Children_and_content_by_label_that_cannot_be_expressed_stay_visible_as_unsupported_text()
    {
        var (r, _) = Convert(
            Macro("children", ("page", "Other page")) +
            Macro("contentbylabel", ("cql", "label = &quot;x&quot; and space = &quot;ENG&quot;")) +
            Macro("contentbylabel", ("labels", "a"), ("spaces", "OTHER")) +
            Macro("contentbylabel", ("labels", "a"), ("type", "blogpost")) +
            Macro("contentbylabel"));
        Assert.All(r.Doc["content"]!.AsArray(), n => Assert.Equal("unsupported", (string)n!["type"]!));
        Assert.Equal(1, r.UnsupportedMacros["children"]);
        Assert.Equal(4, r.UnsupportedMacros["contentbylabel"]);
    }

    [Fact]
    public void Include_macro_becomes_include_node_with_space_and_title()
    {
        var link = "<ac:structured-macro ac:name=\"include\"><ac:parameter ac:name=\"\"><ac:link><ri:page ri:content-title=\"Shared policy\" ri:space-key=\"ENG\"/></ac:link></ac:parameter></ac:structured-macro>";
        var (a, _) = Convert(link);
        Assert.Equal("include", (string)a.Doc["content"]![0]!["type"]!);
        Assert.Null((string?)a.Doc["content"]![0]!["attrs"]!["pageId"]);
        Assert.Equal("ENG", (string)a.Doc["content"]![0]!["attrs"]!["spaceKey"]!);
        Assert.Equal("Shared policy", (string)a.Doc["content"]![0]!["attrs"]!["title"]!);
        Assert.Empty(a.UnsupportedMacros);

        var (b, _) = Convert(Macro("include", ("page", "OPS:Runbook")), new ConversionContext(Guid.NewGuid(), "confluence:x", "1", "in"));
        Assert.Equal("include", (string)b.Doc["content"]![0]!["type"]!);
        Assert.Equal("OPS", (string)b.Doc["content"]![0]!["attrs"]!["spaceKey"]!);
        Assert.Equal("Runbook", (string)b.Doc["content"]![0]!["attrs"]!["title"]!);

        var (same, _) = Convert(Macro("include", ("page", "Only title")), new ConversionContext(Guid.NewGuid(), "confluence:x", "1", "in"));
        Assert.Equal("in", (string)same.Doc["content"]![0]!["attrs"]!["spaceKey"]!);
        Assert.Equal("Only title", (string)same.Doc["content"]![0]!["attrs"]!["title"]!);

        var (bad, _) = Convert(Macro("include") + Macro("include", ("page", "X"), ("bogus", "1")));
        Assert.All(bad.Doc["content"]!.AsArray(), n => Assert.Equal("unsupported", (string)n!["type"]!));
        Assert.Equal(2, bad.UnsupportedMacros["include"]);
    }

    [Fact]
    public void Layouts_are_flattened_and_comment_markers_keep_their_text()
    {
        var (r, v) = Convert("<ac:layout><ac:layout-section ac:type=\"two_equal\"><ac:layout-cell><p>left <ac:inline-comment-marker ac:ref=\"1\">marked</ac:inline-comment-marker></p></ac:layout-cell><ac:layout-cell><p>right</p></ac:layout-cell></ac:layout-section></ac:layout>");
        Assert.Contains("left marked", v.PlainText, StringComparison.Ordinal);
        Assert.Contains("right", v.PlainText, StringComparison.Ordinal);
        Assert.DoesNotContain("unsupported", Types(r));
    }

    [Fact]
    public void Malformed_and_hostile_input_never_throws_and_never_passes_markup_through()
    {
        var (broken, _) = Convert("<p>unclosed <b>bold");
        Assert.Contains(broken.Warnings, w => w.Code == "body.unparseable");
        Assert.Equal("unsupported", (string)broken.Doc["content"]![0]!["type"]!);

        // DTD / external entity attempts are prohibited, not resolved.
        var (xxe, _) = Convert("<!DOCTYPE x [<!ENTITY e SYSTEM \"file:///c:/windows/win.ini\">]><p>&e;</p>");
        Assert.Contains(xxe.Warnings, w => w.Code == "body.unparseable");

        var (hostile, v) = Convert("<p><script>alert(1)</script><iframe src=\"x\"/><img src=\"x\" onerror=\"y\"/><span style=\"x\">ok</span></p>");
        var json = hostile.Doc.ToJsonString();
        Assert.DoesNotContain("onerror", json, StringComparison.Ordinal);
        Assert.DoesNotContain("<iframe", json, StringComparison.Ordinal);
        Assert.Contains("ok", v.PlainText, StringComparison.Ordinal);
    }

    [Fact]
    public void Empty_input_yields_one_empty_paragraph()
    {
        var (r, v) = Convert(string.Empty);
        Assert.Equal(RaaviDocument.Empty.Json, v.Json);
        Assert.Empty(r.Warnings);
    }
}
