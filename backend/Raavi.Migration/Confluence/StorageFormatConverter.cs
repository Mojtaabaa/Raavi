using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace Raavi.Migration.Confluence;

public enum ReferenceKind
{
    Page = 1,
    Attachment = 2,
    User = 3,
}

/// <summary>A reference found in the source body that is resolved in the link pass (CONFLUENCE_MIGRATION.md §2.7).</summary>
/// <summary>Where the body being converted comes from; lets images point at their (deterministically identified) attachment.</summary>
public sealed record ConversionContext(Guid TenantId, string SourceSystem, string PageSourceId, string? SpaceKey = null);

public sealed record SourceReference(ReferenceKind Kind, string? SpaceKey, string? Title, string? FileName, string? UserKey);

public sealed record StorageConversion(
    JsonObject Doc,
    IReadOnlyList<ConversionWarning> Warnings,
    IReadOnlyList<SourceReference> References,
    IReadOnlyDictionary<string, int> UnsupportedMacros);

/// <summary>
/// Converts Confluence <b>storage format</b> into the content of a Raavi document (the <c>doc</c> node; the caller wraps and
/// validates it with the editor registry). The source is parsed as XML (DTD prohibited, no resolver, fixed HTML-entity
/// table) — never as HTML — and nothing is copied through verbatim: unknown macros become <c>unsupported</c> nodes whose
/// source is kept as <b>escaped text</b> and counted. No raw HTML, scripts, iframes or styles can survive.
/// </summary>
public static partial class StorageFormatConverter
{
    public const string PageLinkPrefix = "raavi:confluence-page";
    public const int MaxUnsupportedSourceLength = 20_000;

    private const string Wrapper = "<root xmlns:ac=\"urn:ac\" xmlns:ri=\"urn:ri\" xmlns:at=\"urn:at\">";
    private static readonly XNamespace Ac = "urn:ac";
    private static readonly XNamespace Ri = "urn:ri";

    // Named HTML entities that XML does not know, mapped to numeric references (storage format is XHTML with these).
    private static readonly Dictionary<string, int> Entities = new(StringComparer.Ordinal)
    {
        ["nbsp"] = 160, ["iexcl"] = 161, ["cent"] = 162, ["pound"] = 163, ["curren"] = 164, ["yen"] = 165, ["sect"] = 167, ["copy"] = 169,
        ["laquo"] = 171, ["reg"] = 174, ["deg"] = 176, ["plusmn"] = 177, ["micro"] = 181, ["para"] = 182, ["middot"] = 183, ["raquo"] = 187,
        ["frac12"] = 189, ["times"] = 215, ["divide"] = 247, ["ndash"] = 8211, ["mdash"] = 8212, ["lsquo"] = 8216, ["rsquo"] = 8217,
        ["ldquo"] = 8220, ["rdquo"] = 8221, ["bull"] = 8226, ["hellip"] = 8230, ["prime"] = 8242, ["euro"] = 8364, ["trade"] = 8482,
        ["larr"] = 8592, ["rarr"] = 8594, ["uarr"] = 8593, ["darr"] = 8595, ["harr"] = 8596, ["rArr"] = 8658, ["hArr"] = 8660,
        ["ne"] = 8800, ["le"] = 8804, ["ge"] = 8805, ["zwnj"] = 8204, ["zwj"] = 8205, ["lrm"] = 8206, ["rlm"] = 8207, ["check"] = 10003,
    };

    private static readonly Dictionary<string, string> PanelTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["info"] = "info", ["note"] = "note", ["warning"] = "warning", ["tip"] = "success", ["success"] = "success", ["error"] = "error", ["panel"] = "info",
    };

    private static readonly Dictionary<string, string> StatusColors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["grey"] = "neutral", ["gray"] = "neutral", ["red"] = "red", ["yellow"] = "yellow", ["green"] = "green", ["blue"] = "blue", ["purple"] = "purple",
    };

    public static StorageConversion Convert(string? storage, ConversionContext? context = null)
    {
        var state = new State { Context = context };
        XElement root;
        try
        {
            root = Parse(storage ?? string.Empty);
        }
        catch (XmlException ex)
        {
            // Unparseable source: keep it (escaped) so nothing is lost, and say so.
            state.Warnings.Add(new ConversionWarning("body.unparseable", null, $"The page body is not well-formed ({ex.Message})."));
            state.Blocks.Add(Unsupported("confluence:body", storage ?? string.Empty));
            return Finish(state);
        }

        state.Blocks.AddRange(Blocks(root.Nodes(), state));
        return Finish(state);
    }

    private static StorageConversion Finish(State state)
    {
        if (state.Blocks.Count == 0)
        {
            state.Blocks.Add(Paragraph([]));
        }

        var doc = new JsonObject { ["type"] = "doc", ["content"] = new JsonArray([.. state.Blocks]) };
        return new StorageConversion(doc, state.Warnings, state.References, state.Unsupported);
    }

    private static XElement Parse(string storage)
    {
        var text = EntityRegex().Replace(storage, m => Entities.TryGetValue(m.Groups[1].Value, out var cp) ? $"&#{cp};" : m.Value);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersFromEntities = 0, IgnoreComments = true, IgnoreProcessingInstructions = true };
        using var reader = XmlReader.Create(new StringReader(Wrapper + text + "</root>"), settings);
        return XElement.Load(reader, LoadOptions.PreserveWhitespace);
    }

    [GeneratedRegex("&([A-Za-z][A-Za-z0-9]*);")]
    private static partial Regex EntityRegex();

    [GeneratedRegex("\\s+")]
    private static partial Regex Whitespace();

    // ── Blocks ─────────────────────────────────────────────────────────────────────────────────

    private sealed class State
    {
        public List<JsonNode> Blocks { get; } = [];

        public ConversionContext? Context { get; init; }

        public List<ConversionWarning> Warnings { get; } = [];

        public List<SourceReference> References { get; } = [];

        public Dictionary<string, int> Unsupported { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void Warn(string code, string message, string? macro = null)
        {
            if (!Warnings.Any(w => w.Code == code && w.Detail == message))
            {
                Warnings.Add(new ConversionWarning(code, macro, message));
            }
        }
    }

    /// <summary>Block content of a container. Loose inline content (text, strong, links…) is gathered into paragraphs.</summary>
    private static List<JsonNode> Blocks(IEnumerable<XNode> nodes, State state)
    {
        var blocks = new List<JsonNode>();
        var inline = new InlineBuilder();

        void Flush()
        {
            if (inline.HasContent)
            {
                blocks.Add(Paragraph(inline.Take()));
            }
            else
            {
                inline.Clear();
            }
        }

        foreach (var node in nodes)
        {
            switch (node)
            {
                case XText t:
                    inline.AddText(t.Value, []);
                    break;
                case XElement e when IsBlock(e):
                    Flush();
                    blocks.AddRange(Block(e, state));
                    break;
                case XElement e:
                    var lifted = new List<JsonNode>();
                    Inline(e, [], inline, state, lifted, () => Flush());
                    blocks.AddRange(lifted);
                    break;
            }
        }

        Flush();
        return blocks;
    }

    private static bool IsBlock(XElement e)
    {
        var n = e.Name;
        if (n.Namespace == Ac)
        {
            return n.LocalName is "structured-macro" or "macro" or "layout" or "layout-section" or "layout-cell" or "task-list" or "image" or "rich-text-body"
                && !(n.LocalName is "structured-macro" && InlineMacros.Contains(Attr(e, "name") ?? string.Empty));
        }

        return n.NamespaceName.Length == 0 && n.LocalName is "p" or "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "ul" or "ol" or "blockquote" or "pre" or "hr" or "table" or "div" or "fieldset";
    }

    private static readonly HashSet<string> InlineMacros = new(StringComparer.OrdinalIgnoreCase) { "status", "anchor" };

    private static List<JsonNode> Block(XElement e, State state)
    {
        var name = e.Name.LocalName;
        if (e.Name.Namespace == Ac)
        {
            return name switch
            {
                "layout" or "layout-section" or "layout-cell" or "rich-text-body" => Blocks(e.Nodes(), state),
                "task-list" => [TaskList(e, state)],
                "image" => [Image(e, state)],
                "structured-macro" => Macro(e, state),
                _ => [UnsupportedElement(e, "ac:" + name, state)],
            };
        }

        switch (name)
        {
            case "p":
                // Loose inline content becomes a paragraph; images inside it are lifted to blocks in order. Blank paragraphs are dropped.
                return Blocks(e.Nodes(), state);

            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
                {
                    var inline = new InlineBuilder();
                    AddInlineChildren(e, [], inline, state);
                    return [new JsonObject { ["type"] = "heading", ["attrs"] = new JsonObject { ["level"] = name[1] - '0' }, ["content"] = new JsonArray([.. inline.Take()]) }];
                }

            case "ul" or "ol":
                return [List(e, state)];
            case "blockquote":
                return [new JsonObject { ["type"] = "blockquote", ["content"] = new JsonArray([.. NonEmpty(Blocks(e.Nodes(), state))]) }];
            case "pre":
                return [CodeBlock(null, e.Value)];
            case "hr":
                return [new JsonObject { ["type"] = "horizontalRule" }];
            case "table":
                return [Table(e, state)];
            default: // div, fieldset
                return Blocks(e.Nodes(), state);
        }
    }

    private static List<JsonNode> NonEmpty(List<JsonNode> blocks) => blocks.Count > 0 ? blocks : [Paragraph([])];

    private static JsonObject List(XElement e, State state)
    {
        var items = new JsonArray();
        foreach (var li in e.Elements().Where(x => x.Name.LocalName == "li"))
        {
            var content = Blocks(li.Nodes(), state);
            if (content.Count == 0 || (string?)content[0]["type"] != "paragraph")
            {
                content.Insert(0, Paragraph([]));
            }

            items.Add(new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray([.. content]) });
        }

        // Nested lists written directly inside the list (invalid XHTML, common in storage format) attach to the previous item.
        foreach (var nested in e.Elements().Where(x => x.Name.LocalName is "ul" or "ol"))
        {
            if (items.Count == 0)
            {
                items.Add(new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(Paragraph([])) });
            }

            ((JsonArray)items[^1]!["content"]!).Add(List(nested, state));
        }

        if (items.Count == 0)
        {
            items.Add(new JsonObject { ["type"] = "listItem", ["content"] = new JsonArray(Paragraph([])) });
        }

        return new JsonObject { ["type"] = e.Name.LocalName == "ol" ? "orderedList" : "bulletList", ["content"] = items };
    }

    private static JsonObject TaskList(XElement e, State state)
    {
        var items = new JsonArray();
        foreach (var task in e.Elements(Ac + "task"))
        {
            var checkedState = string.Equals(task.Element(Ac + "task-status")?.Value, "complete", StringComparison.OrdinalIgnoreCase);
            var body = task.Element(Ac + "task-body");
            var content = body is null ? [] : Blocks(body.Nodes(), state);
            if (content.Count == 0 || (string?)content[0]["type"] != "paragraph")
            {
                content.Insert(0, Paragraph([]));
            }

            items.Add(new JsonObject { ["type"] = "taskItem", ["attrs"] = new JsonObject { ["checked"] = checkedState }, ["content"] = new JsonArray([.. content]) });
        }

        if (items.Count == 0)
        {
            items.Add(new JsonObject { ["type"] = "taskItem", ["attrs"] = new JsonObject { ["checked"] = false }, ["content"] = new JsonArray(Paragraph([])) });
        }

        return new JsonObject { ["type"] = "taskList", ["content"] = items };
    }

    private static JsonObject Table(XElement e, State state)
    {
        var rows = new JsonArray();
        foreach (var tr in e.Descendants().Where(x => x.Name.LocalName == "tr" && x.Name.NamespaceName.Length == 0 && x.Ancestors().First(a => a.Name.LocalName == "table") == e))
        {
            var cells = new JsonArray();
            foreach (var cell in tr.Elements().Where(x => x.Name.LocalName is "td" or "th"))
            {
                var attrs = new JsonObject();
                if (int.TryParse(cell.Attribute("colspan")?.Value, out var cs) && cs > 1)
                {
                    attrs["colspan"] = Math.Min(cs, 100);
                }

                if (int.TryParse(cell.Attribute("rowspan")?.Value, out var rs) && rs > 1)
                {
                    attrs["rowspan"] = Math.Min(rs, 100);
                }

                cells.Add(new JsonObject
                {
                    ["type"] = cell.Name.LocalName == "th" ? "tableHeader" : "tableCell",
                    ["attrs"] = attrs,
                    ["content"] = new JsonArray([.. NonEmpty(Blocks(cell.Nodes(), state))]),
                });
            }

            if (cells.Count > 0)
            {
                rows.Add(new JsonObject { ["type"] = "tableRow", ["content"] = cells });
            }
        }

        if (rows.Count == 0)
        {
            state.Warn("table.empty", "An empty table was dropped.");
            return Paragraph([]);
        }

        return new JsonObject { ["type"] = "table", ["content"] = rows };
    }

    private static JsonObject Image(XElement e, State state)
    {
        var attachment = e.Element(Ri + "attachment");
        var url = e.Element(Ri + "url");
        var alt = Attr(e, "alt") ?? string.Empty;
        if (attachment is not null)
        {
            var file = attachment.Attribute(Ri + "filename")?.Value ?? string.Empty;
            state.References.Add(new SourceReference(ReferenceKind.Attachment, SpaceOf(attachment), PageTitleOf(attachment), file, null));
            // An attachment of this very page has a deterministic id, so the image can point at it now. An image that shows a file
            // of another page cannot (its page is not known by id yet): it is kept without a file and reported.
            JsonNode? id = null;
            if (state.Context is { } ctx && file.Length > 0 && attachment.Element(Ri + "page") is null)
            {
                id = Raavi.Application.Abstractions.Content.ImportIds.Attachment(ctx.TenantId, ctx.SourceSystem, ctx.PageSourceId, file).ToString("D");
            }
            else if (attachment.Element(Ri + "page") is not null)
            {
                state.Warn("image.other_page", $"The image '{file}' belongs to another page and was kept without a file.");
            }

            return new JsonObject { ["type"] = "image", ["attrs"] = new JsonObject { ["attachmentId"] = id, ["alt"] = Cut(alt.Length > 0 ? alt : file, 500) } };
        }

        // External images are not fetched or embedded (privacy, SSRF); keep the address as a link in a paragraph.
        var href = url?.Attribute(Ri + "value")?.Value;
        state.Warn("image.external", "An external image was converted to a link.");
        return string.IsNullOrEmpty(href) || !IsSafeHref(href)
            ? Paragraph([Text(alt.Length > 0 ? alt : "[image]", [])])
            : Paragraph([Text(alt.Length > 0 ? alt : href, [LinkMark(href)])]);
    }

    // ── Macros ─────────────────────────────────────────────────────────────────────────────────

    private static List<JsonNode> Macro(XElement e, State state)
    {
        var name = Attr(e, "name") ?? string.Empty;
        string? Param(string p) => e.Elements(Ac + "parameter").FirstOrDefault(x => Attr(x, "name") == p)?.Value;
        var body = e.Element(Ac + "rich-text-body");

        if (PanelTypes.TryGetValue(name, out var panelType))
        {
            var content = body is null ? [] : Blocks(body.Nodes(), state);
            if (Param("title") is { Length: > 0 } title)
            {
                content.Insert(0, Paragraph([Text(title, [Mark("bold")])]));
            }

            return [new JsonObject { ["type"] = "panel", ["attrs"] = new JsonObject { ["type"] = panelType }, ["content"] = new JsonArray([.. NonEmpty(content)]) }];
        }

        switch (name.ToLowerInvariant())
        {
            case "code" or "noformat":
                return [CodeBlock(name == "code" ? Param("language") : null, e.Element(Ac + "plain-text-body")?.Value ?? string.Empty)];
            case "expand":
                return [new JsonObject
                {
                    ["type"] = "expand",
                    ["attrs"] = new JsonObject { ["title"] = Cut(Param("title") ?? string.Empty, 200) },
                    ["content"] = new JsonArray([.. NonEmpty(body is null ? [] : Blocks(body.Nodes(), state))]),
                }];
            case "toc":
                return [new JsonObject { ["type"] = "toc", ["attrs"] = new JsonObject { ["maxLevel"] = int.TryParse(Param("maxLevel"), out var l) && l is >= 1 and <= 6 ? l : 3 } }];
            case "status":
                var inline = StatusNode(Param("title"), Param("colour"));
                return [Paragraph([inline])];
            case "children":
                // Only "the children of this page" is expressible (a "page" parameter points elsewhere).
                if (Param("page") is null && e.Elements(Ac + "parameter").All(x => Attr(x, "name") is "depth" or "sort" or "reverse" or "style" or "excerptType" or "first" or "showLabels" or "showSpace" or "page" or "all" or "excerpt"))
                {
                    var depth = Param("all") == "true" ? 5 : int.TryParse(Param("depth"), out var d) && d is >= 1 and <= 5 ? d : Param("depth") is { Length: > 0 } ? 5 : 1;
                    return [new JsonObject { ["type"] = "children", ["attrs"] = new JsonObject { ["depth"] = depth } }];
                }

                return [UnsupportedMacro(e, name, state)];
            case "include":
                {
                    // Full-page include only. Excerpt-include needs raavi.excerpt first.
                    var page = e.Descendants(Ri + "page").FirstOrDefault();
                    var title = page?.Attribute(Ri + "content-title")?.Value?.Trim()
                        ?? Param("contentTitle")?.Trim()
                        ?? Param("page")?.Trim();
                    var space = page?.Attribute(Ri + "space-key")?.Value?.Trim()
                        ?? Param("space")?.Trim()
                        ?? Param("spaceKey")?.Trim();
                    // "SPACE:Title" form used by some Confluence versions in the page parameter.
                    if (title is { Length: > 0 } && title.Contains(':', StringComparison.Ordinal) && string.IsNullOrEmpty(space))
                    {
                        var colon = title.IndexOf(':', StringComparison.Ordinal);
                        space = title[..colon].Trim();
                        title = title[(colon + 1)..].Trim();
                    }

                    if (string.IsNullOrEmpty(title) || e.Elements(Ac + "parameter").Any(x => Attr(x, "name") is { Length: > 0 } n && n is not ("" or "page" or "space" or "spaceKey" or "contentTitle")))
                    {
                        return [UnsupportedMacro(e, name, state)];
                    }

                    space ??= state.Context?.SpaceKey ?? string.Empty;
                    state.References.Add(new SourceReference(ReferenceKind.Page, string.IsNullOrEmpty(space) ? null : space, title, null, null));
                    return [new JsonObject
                    {
                        ["type"] = "include",
                        ["attrs"] = new JsonObject
                        {
                            ["pageId"] = null,
                            ["spaceKey"] = Cut(space, 50),
                            ["title"] = Cut(title, 500),
                        },
                    }];
                }
            case "contentbylabel":
                {
                    var labels = ParseLabels(Param("labels"), Param("cql"));
                    var spaces = Param("spaces");
                    // "this space only": @self, or the page's own space named by key (what real macros contain).
                    var ownSpace = state.Context?.SpaceKey;
                    var selfOnly = spaces is "@self" || (ownSpace is not null && spaces is { Length: > 0 } && spaces.Split(',').Select(x => x.Trim()).All(x => string.Equals(x, ownSpace, StringComparison.OrdinalIgnoreCase)));
                    var otherParams = e.Elements(Ac + "parameter").Select(x => Attr(x, "name")).Any(n => n is not ("labels" or "cql" or "spaces" or "max" or "maxResults" or "showLabels" or "showSpace" or "sort" or "reverse" or "style" or "excerptType" or "title" or "type"));
                    // Only pages are listed; a different content type (blog posts, attachments…) cannot be represented.
                    var typeOk = Param("type") is null or "" || string.Equals(Param("type"), "page", StringComparison.OrdinalIgnoreCase);
                    if (labels.Count == 0 || otherParams || !typeOk || (spaces is { Length: > 0 } && spaces is not ("@self" or "@all") && !selfOnly))
                    {
                        return [UnsupportedMacro(e, name, state)];
                    }

                    var max = int.TryParse(Param("max") ?? Param("maxResults"), out var m) && m is >= 1 and <= 100 ? m : 20;
                    return [new JsonObject { ["type"] = "contentByLabel", ["attrs"] = new JsonObject { ["labels"] = Cut(string.Join(',', labels), 300), ["maxItems"] = max, ["currentSpaceOnly"] = selfOnly } }];
                }

            default:
                return [UnsupportedMacro(e, name, state)];
        }
    }

    /// <summary>
    /// Labels of a "content by label" macro: the <c>labels</c> parameter, or a simple CQL query (<c>label = "x"</c>,
    /// <c>label in ("a","b")</c>, joined only by OR). Anything else (AND, NOT, other fields) returns nothing so the macro is kept as text.
    /// </summary>
    internal static List<string> ParseLabels(string? labels, string? cql)
    {
        var result = new List<string>();
        static string Clean(string l) => l.Trim().Trim('"', '\'').Trim().ToLowerInvariant();
        if (!string.IsNullOrWhiteSpace(labels) && string.IsNullOrWhiteSpace(cql))
        {
            result.AddRange(labels.Split(',').Select(Clean).Where(l => l.Length > 0 && !l.Any(char.IsWhiteSpace)));
            return result;
        }

        if (!string.IsNullOrWhiteSpace(cql))
        {
            var q = cql.Trim();
            var single = SingleLabel().Match(q);
            if (single.Success)
            {
                result.Add(Clean(single.Groups[1].Value));
            }
            else
            {
                var list = LabelIn().Match(q);
                if (list.Success)
                {
                    result.AddRange(list.Groups[1].Value.Split(',').Select(Clean).Where(l => l.Length > 0));
                }
            }
        }

        return [.. result.Where(l => l.Length is > 0 and <= 100 && !l.Any(c => char.IsWhiteSpace(c) || c is ',' or '#' or '/' or '\\' or '?' or '&')).Distinct()];
    }

    [GeneratedRegex("^label\\s*=\\s*\"?([^\"\\s]+)\"?$", RegexOptions.IgnoreCase)]
    private static partial Regex SingleLabel();

    [GeneratedRegex("^label\\s+in\\s*\\(([^)]*)\\)$", RegexOptions.IgnoreCase)]
    private static partial Regex LabelIn();

    private static JsonObject UnsupportedMacro(XElement e, string name, State state)
    {
        state.Unsupported[name] = state.Unsupported.GetValueOrDefault(name) + 1;
        state.Warn("macro.unsupported", $"Unsupported macro '{name}' was kept as text.", name);
        return Unsupported("confluence:macro:" + name, e.ToString(SaveOptions.DisableFormatting));
    }

    private static JsonObject UnsupportedElement(XElement e, string name, State state)
    {
        state.Unsupported[name] = state.Unsupported.GetValueOrDefault(name) + 1;
        state.Warn("element.unsupported", $"Unsupported element '{name}' was kept as text.");
        return Unsupported("confluence:" + name, e.ToString(SaveOptions.DisableFormatting));
    }

    private static JsonObject Unsupported(string type, string source) => new()
    {
        ["type"] = "unsupported",
        ["attrs"] = new JsonObject { ["sourceType"] = Cut(type, 100), ["source"] = Cut(source, MaxUnsupportedSourceLength) },
    };

    private static JsonObject StatusNode(string? title, string? colour) => new()
    {
        ["type"] = "status",
        ["attrs"] = new JsonObject { ["text"] = Cut(string.IsNullOrWhiteSpace(title) ? "—" : title.Trim(), 60), ["color"] = colour is not null && StatusColors.TryGetValue(colour, out var c) ? c : "neutral" },
    };

    private static JsonObject CodeBlock(string? language, string code)
    {
        var node = new JsonObject { ["type"] = "codeBlock" };
        if (!string.IsNullOrWhiteSpace(language))
        {
            node["attrs"] = new JsonObject { ["language"] = Cut(language.Trim().ToLowerInvariant(), 40) };
        }

        var text = code.Replace("\r\n", "\n", StringComparison.Ordinal).TrimEnd('\n');
        if (text.Length > 0)
        {
            node["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text });
        }

        return node;
    }

    // ── Inline ─────────────────────────────────────────────────────────────────────────────────

    private sealed class InlineBuilder
    {
        private readonly List<JsonObject> _nodes = [];

        public bool HasContent => _nodes.Any(n => (string?)n["type"] != "text" || !string.IsNullOrWhiteSpace((string?)n["text"]) || (string?)n["type"] == "hardBreak");

        public void AddText(string text, List<string> marks)
        {
            var collapsed = Whitespace().Replace(text, " ");
            if (collapsed.Length == 0)
            {
                return;
            }

            // Collapse across node boundaries too.
            if (collapsed.StartsWith(' ') && _nodes.Count > 0 && _nodes[^1]["text"]?.GetValue<string>() is { } last && last.EndsWith(' '))
            {
                collapsed = collapsed[1..];
            }

            if (collapsed.Length == 0)
            {
                return;
            }

            if (_nodes.Count > 0 && _nodes[^1]["type"]?.GetValue<string>() == "text" && SameMarks(_nodes[^1], marks))
            {
                _nodes[^1]["text"] = _nodes[^1]["text"]!.GetValue<string>() + collapsed;
            }
            else
            {
                _nodes.Add(Text(collapsed, [.. marks.Select(Mark)]));
            }
        }

        public void AddNode(JsonObject node) => _nodes.Add(node);

        public void AddBreak() => _nodes.Add(new JsonObject { ["type"] = "hardBreak" });

        public List<JsonObject> Take()
        {
            var result = new List<JsonObject>(_nodes);
            _nodes.Clear();
            // Trim leading/trailing whitespace of the paragraph.
            while (result.Count > 0 && (string?)result[0]["type"] == "text")
            {
                var t = result[0]["text"]!.GetValue<string>().TrimStart();
                if (t.Length == 0) { result.RemoveAt(0); continue; }
                result[0]["text"] = t;
                break;
            }

            while (result.Count > 0 && (string?)result[^1]["type"] == "text")
            {
                var t = result[^1]["text"]!.GetValue<string>().TrimEnd();
                if (t.Length == 0) { result.RemoveAt(result.Count - 1); continue; }
                result[^1]["text"] = t;
                break;
            }

            return result;
        }

        public void Clear() => _nodes.Clear();

        private static bool SameMarks(JsonObject node, List<string> marks)
        {
            var existing = node["marks"] is JsonArray a ? a.Select(m => (string?)m!["type"] + (m["attrs"]?["href"] is { } h ? "\u0001" + h : string.Empty)).ToList() : [];
            return existing.SequenceEqual(marks);
        }
    }

    private static void AddInlineChildren(XElement e, List<string> marks, InlineBuilder inline, State state)
    {
        var lifted = new List<JsonNode>();
        foreach (var child in e.Nodes())
        {
            if (child is XText t)
            {
                inline.AddText(t.Value, marks);
            }
            else if (child is XElement c)
            {
                Inline(c, marks, inline, state, lifted, () => { });
            }
        }
    }

    /// <summary>Marks are carried as strings: "bold", "italic", … or "link\u0001{href}".</summary>
    private static void Inline(XElement e, List<string> marks, InlineBuilder inline, State state, List<JsonNode> lifted, Action flush)
    {
        var name = e.Name.LocalName;
        List<string> With(string mark) => marks.Contains(mark) ? marks : [.. marks, mark];

        if (e.Name.Namespace == Ac)
        {
            switch (name)
            {
                case "link":
                    InlineLink(e, marks, inline, state);
                    return;
                case "inline-comment-marker" or "link-body" or "plain-text-link-body":
                    AddInlineChildren(e, marks, inline, state);
                    return;
                case "emoticon":
                    inline.AddText(Attr(e, "emoji-fallback") ?? $":{Attr(e, "name") ?? "emoticon"}:", marks);
                    return;
                case "image":
                    flush();
                    lifted.Add(Image(e, state));
                    return;
                case "structured-macro" when InlineMacros.Contains(Attr(e, "name") ?? string.Empty):
                    if (string.Equals(Attr(e, "name"), "status", StringComparison.OrdinalIgnoreCase))
                    {
                        string? Param(string p) => e.Elements(Ac + "parameter").FirstOrDefault(x => Attr(x, "name") == p)?.Value;
                        inline.AddNode(StatusNode(Param("title"), Param("colour")));
                    }

                    return;
                default:
                    flush();
                    lifted.Add(name is "structured-macro" or "macro" ? UnsupportedMacro(e, Attr(e, "name") ?? name, state) : UnsupportedElement(e, "ac:" + name, state));
                    return;
            }
        }

        switch (name)
        {
            case "strong" or "b":
                AddInlineChildren(e, With("bold"), inline, state);
                break;
            case "em" or "i":
                AddInlineChildren(e, With("italic"), inline, state);
                break;
            case "u":
                AddInlineChildren(e, With("underline"), inline, state);
                break;
            case "s" or "del" or "strike":
                AddInlineChildren(e, With("strike"), inline, state);
                break;
            case "code":
                AddInlineChildren(e, With("code"), inline, state);
                break;
            case "a":
                {
                    var href = e.Attribute("href")?.Value?.Trim();
                    var linkMarks = !string.IsNullOrEmpty(href) && IsSafeHref(href) ? With("link\u0001" + href) : marks;
                    if (!string.IsNullOrEmpty(href) && !IsSafeHref(href))
                    {
                        state.Warn("link.unsafe", "A link with an unsupported address was converted to text.");
                    }

                    AddInlineChildren(e, linkMarks, inline, state);
                    break;
                }

            case "br":
                inline.AddBreak();
                break;
            case "time":
                inline.AddText(e.Attribute("datetime")?.Value ?? e.Value, marks);
                break;
            default: // span, sub, sup, font, small… — formatting is dropped, text kept
                AddInlineChildren(e, marks, inline, state);
                break;
        }
    }

    private static void InlineLink(XElement e, List<string> marks, InlineBuilder inline, State state)
    {
        var page = e.Element(Ri + "page");
        var attachment = e.Element(Ri + "attachment");
        var user = e.Element(Ri + "user");
        var label = (e.Element(Ac + "plain-text-link-body")?.Value ?? e.Element(Ac + "link-body")?.Value)?.Trim();

        if (page is not null)
        {
            var title = page.Attribute(Ri + "content-title")?.Value ?? string.Empty;
            var space = page.Attribute(Ri + "space-key")?.Value;
            state.References.Add(new SourceReference(ReferenceKind.Page, space, title, null, null));
            var href = $"{PageLinkPrefix}?space={Uri.EscapeDataString(space ?? string.Empty)}&title={Uri.EscapeDataString(title)}";
            inline.AddText(string.IsNullOrEmpty(label) ? title : label, [.. marks.Where(m => !m.StartsWith("link", StringComparison.Ordinal)), "link\u0001" + href]);
            return;
        }

        if (attachment is not null)
        {
            var file = attachment.Attribute(Ri + "filename")?.Value ?? string.Empty;
            state.References.Add(new SourceReference(ReferenceKind.Attachment, SpaceOf(attachment), PageTitleOf(attachment), file, null));
            inline.AddText(string.IsNullOrEmpty(label) ? file : label, marks);
            return;
        }

        if (user is not null)
        {
            var key = user.Attribute(Ri + "userkey")?.Value ?? user.Attribute(Ri + "account-id")?.Value ?? user.Attribute(Ri + "username")?.Value;
            state.References.Add(new SourceReference(ReferenceKind.User, null, null, null, key));
            inline.AddText("@" + (string.IsNullOrEmpty(label) ? key ?? "user" : label), marks);
            return;
        }

        AddInlineChildren(e, marks, inline, state);
    }

    // ── Helpers ────────────────────────────────────────────────────────────────────────────────

    private static string? Attr(XElement e, string name) => e.Attribute(Ac + name)?.Value ?? e.Attribute(name)?.Value;

    private static string? SpaceOf(XElement attachment) => attachment.Element(Ri + "page")?.Attribute(Ri + "space-key")?.Value;

    private static string? PageTitleOf(XElement attachment) => attachment.Element(Ri + "page")?.Attribute(Ri + "content-title")?.Value;

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private static bool IsSafeHref(string href)
    {
        if (href.Length is 0 or > 2048 || href != href.Trim() || href.Any(char.IsControl) || href.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        if (href.StartsWith('/'))
        {
            return !href.StartsWith("//", StringComparison.Ordinal);
        }

        return href.StartsWith('#') || (Uri.TryCreate(href, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto" or "raavi");
    }

    private static JsonObject Mark(string mark)
    {
        var parts = mark.Split('\u0001');
        var node = new JsonObject { ["type"] = parts[0] };
        if (parts.Length > 1)
        {
            node["attrs"] = new JsonObject { ["href"] = parts[1] };
        }

        return node;
    }

    private static JsonObject LinkMark(string href) => new() { ["type"] = "link", ["attrs"] = new JsonObject { ["href"] = href } };

    private static JsonObject Text(string text, IEnumerable<JsonObject> marks)
    {
        var node = new JsonObject { ["type"] = "text", ["text"] = text };
        var list = marks.ToList();
        if (list.Count > 0)
        {
            node["marks"] = new JsonArray([.. list]);
        }

        return node;
    }

    private static JsonObject Paragraph(IEnumerable<JsonObject> inline)
    {
        var content = inline.ToList();
        var node = new JsonObject { ["type"] = "paragraph" };
        if (content.Count > 0)
        {
            node["content"] = new JsonArray([.. content]);
        }

        return node;
    }

    // Keep Encoding referenced for callers measuring sizes.
    internal static int Utf8Length(string s) => Encoding.UTF8.GetByteCount(s);
}
