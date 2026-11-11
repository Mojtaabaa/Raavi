using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Unicode;
using Ganss.Xss;

namespace Raavi.Wiki.Editor;

/// <summary>
/// Server-side HTML for export, e-mail and caches (EDITOR_ARCHITECTURE.md §5): a whitelist renderer per node type (no
/// HTML passthrough — text and attributes are always encoded), followed by HtmlSanitizer as defence in depth.
/// Mermaid and math are emitted as escaped source for client-side rendering; nothing is executed on the server.
/// </summary>
public sealed class DocumentHtmlRenderer
{
    private static readonly HtmlEncoder Encoder = HtmlEncoder.Create(UnicodeRanges.All);
    private readonly HtmlSanitizer _sanitizer;

    public DocumentHtmlRenderer()
    {
        _sanitizer = new HtmlSanitizer(new HtmlSanitizerOptions
        {
            AllowedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "p", "h1", "h2", "h3", "h4", "h5", "h6", "strong", "em", "u", "s", "sub", "sup", "code", "pre", "a", "span", "br", "hr",
                "ul", "ol", "li", "blockquote", "table", "thead", "tbody", "tr", "th", "td", "colgroup", "col", "img",
                "div", "details", "summary", "nav", "mark",
            },
            AllowedAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "href", "src", "alt", "width", "dir", "class", "start", "type", "colspan", "rowspan", "id", "title", "style",
                "data-user-id", "data-checked", "data-latex", "data-type", "rel", "target", "role", "download", "loading",
            },
            AllowedCssProperties = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "color", "background-color", "text-align", "width", "font-family", "font-size", "line-height" },
            AllowedSchemes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "raavi" },
            UriAttributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "href", "src" },
        });
        _sanitizer.KeepChildNodes = true;
    }

    /// <param name="userNames">Display names of mentioned users the viewer may see (others fall back to the stored label).</param>
    public string Render(JsonObject doc, IReadOnlyDictionary<Guid, string>? userNames = null)
    {
        ArgumentNullException.ThrowIfNull(doc);
        var sb = new StringBuilder();
        var headings = new List<(int Level, string Id, string Text)>();
        CollectHeadings(doc, headings);
        var ctx = new Context(userNames ?? new Dictionary<Guid, string>(), headings);
        Children(doc, sb, ctx);
        return _sanitizer.Sanitize(sb.ToString());
    }

    private sealed record Context(IReadOnlyDictionary<Guid, string> Names, List<(int Level, string Id, string Text)> Headings)
    {
        public int HeadingIndex { get; set; }
    }

    private static void CollectHeadings(JsonObject node, List<(int, string, string)> list)
    {
        if (node["type"]?.GetValue<string>() == "heading")
        {
            var level = (int)(node["attrs"]?["level"]?.GetValue<long>() ?? 1);
            list.Add((level, "h-" + (list.Count + 1).ToString(CultureInfo.InvariantCulture), EditorSchema.Current.PlainText(node)));
        }

        if (node["content"] is JsonArray children)
        {
            foreach (var c in children.OfType<JsonObject>())
            {
                CollectHeadings(c, list);
            }
        }
    }

    private static void Children(JsonObject node, StringBuilder sb, Context ctx)
    {
        if (node["content"] is JsonArray children)
        {
            foreach (var c in children.OfType<JsonObject>())
            {
                Node(c, sb, ctx);
            }
        }
    }

    private static string E(string? s) => Encoder.Encode(s ?? string.Empty);

    private static string? Attr(JsonObject node, string name) => node["attrs"]?[name] is JsonValue v ? v.ToString() : null;

    private static string BlockAttrs(JsonObject node)
    {
        var dir = Attr(node, "dir") ?? "auto";
        var align = Attr(node, "textAlign");
        return $" dir=\"{E(dir)}\"" + (align is null ? string.Empty : $" style=\"text-align:{E(align)}\"");
    }

    private static void Node(JsonObject n, StringBuilder sb, Context ctx)
    {
        switch (n["type"]!.GetValue<string>())
        {
            case "text":
                Text(n, sb);
                break;
            case "paragraph":
                sb.Append("<p").Append(BlockAttrs(n)).Append('>');
                Children(n, sb, ctx);
                sb.Append("</p>");
                break;
            case "heading":
                var level = Math.Clamp(int.Parse(Attr(n, "level") ?? "1", CultureInfo.InvariantCulture), 1, 6);
                var id = ctx.HeadingIndex < ctx.Headings.Count ? ctx.Headings[ctx.HeadingIndex++].Id : "h";
                sb.Append(CultureInfo.InvariantCulture, $"<h{level} id=\"{id}\"").Append(BlockAttrs(n)).Append('>');
                Children(n, sb, ctx);
                sb.Append(CultureInfo.InvariantCulture, $"</h{level}>");
                break;
            case "blockquote":
                Wrap(n, sb, ctx, "<blockquote dir=\"auto\">", "</blockquote>");
                break;
            case "bulletList":
                Wrap(n, sb, ctx, "<ul>", "</ul>");
                break;
            case "orderedList":
                sb.Append("<ol start=\"").Append(E(Attr(n, "start") ?? "1")).Append('"');
                if (Attr(n, "type") is { } listType)
                {
                    sb.Append(" type=\"").Append(E(listType)).Append('"');
                }

                sb.Append('>');
                Children(n, sb, ctx);
                sb.Append("</ol>");
                break;
            case "listItem":
                Wrap(n, sb, ctx, "<li>", "</li>");
                break;
            case "taskList":
                Wrap(n, sb, ctx, "<ul class=\"raavi-tasks\">", "</ul>");
                break;
            case "taskItem":
                var checkedState = Attr(n, "checked") == "true";
                Wrap(n, sb, ctx, $"<li class=\"raavi-task\" data-checked=\"{(checkedState ? "true" : "false")}\">", "</li>");
                break;
            case "codeBlock":
                var language = Attr(n, "language");
                sb.Append("<pre dir=\"ltr\"><code").Append(language is null ? string.Empty : $" class=\"language-{E(language)}\"").Append('>');
                Children(n, sb, ctx);
                sb.Append("</code></pre>");
                break;
            case "horizontalRule":
                sb.Append("<hr>");
                break;
            case "hardBreak":
                sb.Append("<br>");
                break;
            case "table":
                Wrap(n, sb, ctx, "<table class=\"raavi-table\"><tbody>", "</tbody></table>");
                break;
            case "tableRow":
                Wrap(n, sb, ctx, "<tr>", "</tr>");
                break;
            case "tableCell":
            case "tableHeader":
                var tag = n["type"]!.GetValue<string>() == "tableHeader" ? "th" : "td";
                sb.Append('<').Append(tag);
                if (Attr(n, "colspan") is { } cs && cs != "1")
                {
                    sb.Append(" colspan=\"").Append(E(cs)).Append('"');
                }

                if (Attr(n, "rowspan") is { } rs && rs != "1")
                {
                    sb.Append(" rowspan=\"").Append(E(rs)).Append('"');
                }

                if (Attr(n, "align") is { } cellAlign)
                {
                    sb.Append(" style=\"text-align:").Append(E(cellAlign)).Append('"');
                }

                sb.Append('>');
                Children(n, sb, ctx);
                sb.Append("</").Append(tag).Append('>');
                break;
            case "panel":
                var panelType = Attr(n, "type") ?? "info";
                Wrap(n, sb, ctx, $"<div class=\"raavi-panel raavi-panel-{E(panelType)}\" role=\"note\" data-type=\"{E(panelType)}\">", "</div>");
                break;
            case "expand":
                Wrap(n, sb, ctx, $"<details class=\"raavi-expand\"><summary>{E(Attr(n, "title"))}</summary>", "</details>");
                break;
            case "status":
                sb.Append("<span class=\"raavi-status raavi-status-").Append(E(Attr(n, "color") ?? "neutral")).Append("\">").Append(E(Attr(n, "text"))).Append("</span>");
                break;
            case "issue":
                // Server-rendered HTML never calls a tracker (no token, no network); the viewer's browser resolves the reference.
                sb.Append("<span class=\"raavi-issue\" data-reference=\"").Append(E(Attr(n, "reference"))).Append("\">").Append(E(Attr(n, "reference"))).Append("</span>");
                break;
            case "toc":
                var max = int.Parse(Attr(n, "maxLevel") ?? "3", CultureInfo.InvariantCulture);
                sb.Append("<nav class=\"raavi-toc\"><ul>");
                foreach (var (lvl, hid, text) in ctx.Headings.Where(h => h.Level <= max))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<li class=\"raavi-toc-{lvl}\"><a href=\"#{hid}\">").Append(E(text)).Append("</a></li>");
                }

                sb.Append("</ul></nav>");
                break;
            case "children":
            case "contentByLabel":
                // Dynamic lists (child pages, pages by label) depend on who is looking; the static HTML only marks the place.
                sb.Append("<div class=\"raavi-dynamic\" data-kind=\"").Append(E(n["type"]?.GetValue<string>() ?? string.Empty)).Append("\"></div>");
                break;
            case "mermaid":
                sb.Append("<pre class=\"raavi-mermaid\" dir=\"ltr\">").Append(E(Attr(n, "source"))).Append("</pre>");
                break;
            case "mathInline":
                sb.Append("<span class=\"raavi-math\" dir=\"ltr\">").Append(E(Attr(n, "latex"))).Append("</span>");
                break;
            case "mathBlock":
                sb.Append("<div class=\"raavi-math-block\" dir=\"ltr\">").Append(E(Attr(n, "latex"))).Append("</div>");
                break;
            case "mention":
                var userId = Guid.TryParse(Attr(n, "id"), out var g) ? g : Guid.Empty;
                var name = ctx.Names.GetValueOrDefault(userId) ?? Attr(n, "label") ?? string.Empty;
                sb.Append("<span class=\"raavi-mention\" data-user-id=\"").Append(E(userId.ToString())).Append("\">@").Append(E(name)).Append("</span>");
                break;
            case "image":
                if (Guid.TryParse(Attr(n, "attachmentId"), out var imageId))
                {
                    sb.Append("<img src=\"/api/v1/attachments/").Append(imageId.ToString("D")).Append("\" alt=\"").Append(E(Attr(n, "alt"))).Append('"');
                    if (Attr(n, "width") is { } width)
                    {
                        sb.Append(" width=\"").Append(E(width)).Append('"');
                    }

                    sb.Append(" loading=\"lazy\">");
                }

                break;
            case "attachment":
                if (Guid.TryParse(Attr(n, "attachmentId"), out var fileId))
                {
                    sb.Append("<p class=\"raavi-attachment\"><a href=\"/api/v1/attachments/").Append(fileId.ToString("D")).Append("\" download>")
                        .Append(E(Attr(n, "fileName"))).Append("</a></p>");
                }

                break;
            case "unsupported":
                sb.Append("<div class=\"raavi-unsupported\"><pre dir=\"ltr\">").Append(E(Attr(n, "source"))).Append("</pre></div>");
                break;
            default:
                // Unknown node types never reach the renderer (validated on save); render nothing rather than guess.
                break;
        }
    }

    private static void Wrap(JsonObject n, StringBuilder sb, Context ctx, string open, string close)
    {
        sb.Append(open);
        Children(n, sb, ctx);
        sb.Append(close);
    }

    private static void Text(JsonObject n, StringBuilder sb)
    {
        var text = E(n["text"]!.GetValue<string>());
        if (n["marks"] is not JsonArray marks || marks.Count == 0)
        {
            sb.Append(text);
            return;
        }

        var open = new StringBuilder();
        var close = new List<string>();
        foreach (var m in marks.OfType<JsonObject>())
        {
            var (o, c) = m["type"]!.GetValue<string>() switch
            {
                "bold" => ("<strong>", "</strong>"),
                "italic" => ("<em>", "</em>"),
                "underline" => ("<u>", "</u>"),
                "strike" => ("<s>", "</s>"),
                "subscript" => ("<sub>", "</sub>"),
                "superscript" => ("<sup>", "</sup>"),
                "code" => ("<code>", "</code>"),
                "link" when Attr(m, "href") is { } href && EditorSchema.IsSafeHref(href) =>
                    LinkOpen(href),
                "textStyle" => TextStyleOpen(m),
                "highlight" => Attr(m, "color") is { } bg ? ($"<mark style=\"background-color:{E(bg)}\">", "</mark>") : ("<mark>", "</mark>"),
                _ => (string.Empty, string.Empty),
            };
            open.Append(o);
            close.Insert(0, c);
        }

        sb.Append(open).Append(text).Append(string.Concat(close));
    }

    private static (string Open, string Close) LinkOpen(string href)
    {
        var target = LinkPathRewrites.TryRewrite(href, out var absolute) ? absolute : href;
        var blank = target.StartsWith('/') || target.StartsWith('#') ? string.Empty : " target=\"_blank\"";
        return ($"<a href=\"{E(target)}\" rel=\"noopener noreferrer nofollow\"{blank}>", "</a>");
    }

    private static (string Open, string Close) TextStyleOpen(JsonObject mark)
    {
        var parts = new List<string>();
        if (Attr(mark, "color") is { } color)
        {
            parts.Add($"color:{E(color)}");
        }

        if (Attr(mark, "backgroundColor") is { } bg)
        {
            parts.Add($"background-color:{E(bg)}");
        }

        if (Attr(mark, "fontFamily") is { } family)
        {
            parts.Add($"font-family:{E(family)}");
        }

        if (Attr(mark, "fontSize") is { } size)
        {
            parts.Add($"font-size:{E(size)}");
        }

        if (Attr(mark, "lineHeight") is { } height)
        {
            parts.Add($"line-height:{E(height)}");
        }

        return parts.Count == 0 ? (string.Empty, string.Empty) : ($"<span style=\"{string.Join(';', parts)}\">", "</span>");
    }
}
