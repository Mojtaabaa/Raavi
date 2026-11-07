using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Raavi.Domain.Common;

namespace Raavi.Wiki.Editor;

/// <summary>Attribute definition from an extension manifest (a small JSON-Schema subset).</summary>
public sealed record AttributeSpec(
    string Type,
    bool Nullable,
    JsonNode? Default,
    IReadOnlyList<JsonNode?>? Enum,
    int? MaxLength,
    long? Minimum,
    long? Maximum,
    string? Pattern,
    string? Format,
    AttributeSpec? Items,
    int? MaxItems);

/// <summary>One extension manifest (EDITOR_ARCHITECTURE.md §2), shared with the frontend registry.</summary>
public sealed record ExtensionManifest(
    string Id,
    string Name,
    int Version,
    string Kind,
    IReadOnlyList<string> Groups,
    string? Content,
    string? Marks,
    bool Inline,
    bool Atom,
    IReadOnlyList<KeyValuePair<string, AttributeSpec>> Attributes,
    string Search,
    bool Insertable)
{
    public bool IsMark => Kind == "mark";
}

/// <summary>Registry envelope settings (<c>schema/editor/registry.json</c>).</summary>
public sealed record RegistryInfo(string Schema, int SchemaVersion, int MaxBytes, int MaxDepth);

/// <summary>
/// The server-side Extension Registry: loads every manifest (embedded from <c>schema/editor</c>), compiles content
/// expressions, and validates + canonicalizes documents. Authoritative on every save (ADR-010). Process-wide and
/// immutable: manifests are a compile-time contract shared with the frontend.
/// </summary>
public sealed partial class EditorSchema
{
    private static readonly Lazy<EditorSchema> Instance = new(Load);
    private readonly FrozenDictionary<string, ExtensionManifest> _nodes;
    private readonly FrozenDictionary<string, ExtensionManifest> _marks;
    private readonly FrozenDictionary<string, char> _tokens;
    private readonly FrozenDictionary<string, Regex?> _content;

    private EditorSchema(RegistryInfo info, IReadOnlyList<ExtensionManifest> manifests)
    {
        Info = info;
        Manifests = manifests;
        var dupes = manifests.GroupBy(m => m.Id).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        if (dupes.Count > 0)
        {
            throw new InvalidOperationException("Duplicate editor extension ids: " + string.Join(", ", dupes));
        }

        _nodes = manifests.Where(m => !m.IsMark).ToFrozenDictionary(m => m.Name, StringComparer.Ordinal);
        _marks = manifests.Where(m => m.IsMark).ToFrozenDictionary(m => m.Name, StringComparer.Ordinal);
        var i = 0;
        _tokens = _nodes.Keys.Order(StringComparer.Ordinal).ToFrozenDictionary(n => n, _ => (char)(0xE000 + i++), StringComparer.Ordinal);
        _content = _nodes.Values.ToFrozenDictionary(m => m.Name, m => m.Content is null ? null : Compile(m.Content), StringComparer.Ordinal);
    }

    public static EditorSchema Current => Instance.Value;

    public RegistryInfo Info { get; }

    public IReadOnlyList<ExtensionManifest> Manifests { get; }

    public ExtensionManifest? Node(string name) => _nodes.GetValueOrDefault(name);

    public ExtensionManifest? Mark(string name) => _marks.GetValueOrDefault(name);

    // ── Loading ────────────────────────────────────────────────────────────────────────────────

    private static EditorSchema Load()
    {
        var assembly = typeof(EditorSchema).Assembly;
        RegistryInfo? info = null;
        var manifests = new List<ExtensionManifest>();
        foreach (var resource in assembly.GetManifestResourceNames().Where(n => n.StartsWith("editor/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var doc = JsonDocument.Parse(stream);
            var root = doc.RootElement;
            if (resource == "editor/registry.json")
            {
                info = new RegistryInfo(root.GetProperty("schema").GetString()!, root.GetProperty("schemaVersion").GetInt32(), root.GetProperty("maxBytes").GetInt32(), root.GetProperty("maxDepth").GetInt32());
            }
            else
            {
                manifests.Add(ParseManifest(root));
            }
        }

        return new EditorSchema(info ?? throw new InvalidOperationException("editor/registry.json is not embedded."), manifests);
    }

    private static ExtensionManifest ParseManifest(JsonElement m)
    {
        var attrs = new List<KeyValuePair<string, AttributeSpec>>();
        if (m.TryGetProperty("attributes", out var a))
        {
            foreach (var p in a.EnumerateObject())
            {
                attrs.Add(new(p.Name, ParseAttribute(p.Value)));
            }
        }

        return new ExtensionManifest(
            m.GetProperty("id").GetString()!,
            m.GetProperty("name").GetString()!,
            m.GetProperty("version").GetInt32(),
            m.GetProperty("kind").GetString()!,
            m.TryGetProperty("group", out var g) ? g.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [],
            m.TryGetProperty("content", out var c) ? c.GetString() : null,
            m.TryGetProperty("marks", out var mk) ? mk.GetString() : null,
            m.TryGetProperty("inline", out var il) && il.GetBoolean(),
            m.TryGetProperty("atom", out var at) && at.GetBoolean(),
            attrs,
            m.TryGetProperty("search", out var s) ? s.GetString()! : "none",
            !m.TryGetProperty("insertable", out var ins) || ins.GetBoolean());
    }

    private static AttributeSpec ParseAttribute(JsonElement a) => new(
        a.GetProperty("type").GetString()!,
        a.TryGetProperty("nullable", out var n) && n.GetBoolean(),
        a.TryGetProperty("default", out var d) ? JsonNode.Parse(d.GetRawText()) : null,
        a.TryGetProperty("enum", out var e) ? e.EnumerateArray().Select(x => JsonNode.Parse(x.GetRawText())).ToList() : null,
        a.TryGetProperty("maxLength", out var ml) ? ml.GetInt32() : null,
        a.TryGetProperty("minimum", out var mi) ? mi.GetInt64() : null,
        a.TryGetProperty("maximum", out var ma) ? ma.GetInt64() : null,
        a.TryGetProperty("pattern", out var pt) ? pt.GetString() : null,
        a.TryGetProperty("format", out var f) ? f.GetString() : null,
        a.TryGetProperty("items", out var it) ? ParseAttribute(it) : null,
        a.TryGetProperty("maxItems", out var mx) ? mx.GetInt32() : null);

    // ── Content expressions (ProseMirror syntax) → regular expressions over one token per node type ──

    private Regex Compile(string expression)
    {
        var tokens = TokenPattern().Matches(expression).Select(t => t.Value).ToList();
        var pos = 0;
        var sb = new StringBuilder("^(?:");
        ParseChoice(tokens, ref pos, sb);
        if (pos != tokens.Count)
        {
            throw new InvalidOperationException($"Invalid content expression '{expression}'.");
        }

        sb.Append(")$");
        return new Regex(sb.ToString(), RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(250));
    }

    private void ParseChoice(List<string> t, ref int pos, StringBuilder sb)
    {
        ParseSequence(t, ref pos, sb);
        while (pos < t.Count && t[pos] == "|")
        {
            pos++;
            sb.Append('|');
            ParseSequence(t, ref pos, sb);
        }
    }

    private void ParseSequence(List<string> t, ref int pos, StringBuilder sb)
    {
        while (pos < t.Count && t[pos] != "|" && t[pos] != ")")
        {
            sb.Append("(?:");
            if (t[pos] == "(")
            {
                pos++;
                ParseChoice(t, ref pos, sb);
                if (pos >= t.Count || t[pos] != ")")
                {
                    throw new InvalidOperationException("Unbalanced parentheses in content expression.");
                }

                pos++;
            }
            else
            {
                var name = t[pos++];
                var members = _nodes.Values.Where(n => n.Name == name || n.Groups.Contains(name)).Select(n => _tokens[n.Name]).ToList();
                if (members.Count == 0)
                {
                    throw new InvalidOperationException($"Content expression references unknown node or group '{name}'.");
                }

                sb.Append('[').Append(string.Concat(members)).Append(']');
            }

            sb.Append(')');
            if (pos < t.Count && t[pos] is "*" or "+" or "?")
            {
                sb.Append(t[pos++]);
            }
            else if (pos < t.Count && t[pos].StartsWith('{'))
            {
                sb.Append(t[pos++]);
            }
        }
    }

    [GeneratedRegex(@"\{\d+(,\d*)?\}|[A-Za-z_][A-Za-z0-9_]*|[()|*+?]", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    // ── Validation + canonicalization ─────────────────────────────────────────────────────────

    /// <summary>Validates <paramref name="doc"/> (the <c>doc</c> node) and returns its canonical form. Throws <see cref="DomainException"/> (<c>content.body_invalid</c>).</summary>
    public JsonObject Canonicalize(JsonElement doc)
    {
        var budget = new int[] { 0 };
        var node = Node(doc, "doc", depth: 0, budget);
        return node;
    }

    private JsonObject Node(JsonElement e, string? expected, int depth, int[] nodeCount)
    {
        if (depth > Info.MaxDepth)
        {
            throw Invalid("document is nested too deeply");
        }

        if (++nodeCount[0] > 500_000)
        {
            throw Invalid("too many nodes");
        }

        if (e.ValueKind != JsonValueKind.Object)
        {
            throw Invalid("node must be an object");
        }

        var type = e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : throw Invalid("node without type");
        if (expected is not null && type != expected)
        {
            throw Invalid($"expected '{expected}'");
        }

        var manifest = Node(type) ?? throw Invalid($"unknown node type '{type}'");
        foreach (var p in e.EnumerateObject())
        {
            if (p.Name is not ("type" or "attrs" or "content" or "marks" or "text"))
            {
                throw Invalid($"unexpected property '{p.Name}' on '{type}'");
            }
        }

        var result = new JsonObject { ["type"] = type };
        if (type == "text")
        {
            var text = e.TryGetProperty("text", out var tx) && tx.ValueKind == JsonValueKind.String ? tx.GetString()! : throw Invalid("text node without text");
            if (text.Length == 0 || text.Any(c => char.IsControl(c) && c is not ('\t' or '\n' or '\r')))
            {
                throw Invalid("invalid text");
            }

            if (e.TryGetProperty("marks", out var marks) && marks.ValueKind != JsonValueKind.Null)
            {
                var canonical = Marks(marks);
                if (canonical.Count > 0)
                {
                    result["marks"] = canonical;
                }
            }

            result["text"] = text;
            return result;
        }

        if (e.TryGetProperty("text", out _) || e.TryGetProperty("marks", out _))
        {
            throw Invalid($"'{type}' cannot carry text or marks");
        }

        var attrs = Attributes(manifest, e.TryGetProperty("attrs", out var at) ? at : default, type);
        if (attrs is not null)
        {
            result["attrs"] = attrs;
        }

        var children = new List<JsonObject>();
        if (e.TryGetProperty("content", out var content) && content.ValueKind != JsonValueKind.Null)
        {
            if (content.ValueKind != JsonValueKind.Array)
            {
                throw Invalid("content must be an array");
            }

            if (manifest.Content is null)
            {
                if (content.GetArrayLength() > 0)
                {
                    throw Invalid($"'{type}' cannot have content");
                }
            }
            else
            {
                foreach (var child in content.EnumerateArray())
                {
                    children.Add(Node(child, null, depth + 1, nodeCount));
                }
            }
        }

        if (_content[type] is { } rule)
        {
            var sequence = string.Concat(children.Select(c => _tokens[c["type"]!.GetValue<string>()]));
            if (!rule.IsMatch(sequence))
            {
                throw Invalid($"invalid content inside '{type}'");
            }

            // Marks allowed inside this node ("" = none). Text in code blocks must not carry marks.
            if (manifest.Marks == string.Empty && children.Any(c => c["marks"] is not null))
            {
                throw Invalid($"'{type}' does not allow formatting");
            }
        }

        if (children.Count > 0)
        {
            result["content"] = new JsonArray([.. children]);
        }

        return result;
    }

    private JsonArray Marks(JsonElement marks)
    {
        if (marks.ValueKind != JsonValueKind.Array || marks.GetArrayLength() > 16)
        {
            throw Invalid("marks must be an array");
        }

        var result = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var m in marks.EnumerateArray())
        {
            var type = m.ValueKind == JsonValueKind.Object && m.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString()! : throw Invalid("mark without type");
            var manifest = Mark(type) ?? throw Invalid($"unknown mark '{type}'");
            if (!seen.Add(type))
            {
                throw Invalid($"duplicate mark '{type}'");
            }

            foreach (var p in m.EnumerateObject())
            {
                if (p.Name is not ("type" or "attrs"))
                {
                    throw Invalid($"unexpected property '{p.Name}' on mark '{type}'");
                }
            }

            var mark = new JsonObject { ["type"] = type };
            var attrs = Attributes(manifest, m.TryGetProperty("attrs", out var a) ? a : default, type);
            if (attrs is not null)
            {
                mark["attrs"] = attrs;
            }

            result.Add(mark);
        }

        return result;
    }

    private static JsonObject? Attributes(ExtensionManifest manifest, JsonElement attrs, string owner)
    {
        var given = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (attrs.ValueKind is JsonValueKind.Object)
        {
            foreach (var p in attrs.EnumerateObject())
            {
                if (!manifest.Attributes.Any(a => a.Key == p.Name))
                {
                    throw Invalid($"unknown attribute '{p.Name}' on '{owner}'");
                }

                given[p.Name] = p.Value;
            }
        }
        else if (attrs.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null))
        {
            throw Invalid("attrs must be an object");
        }

        if (manifest.Attributes.Count == 0)
        {
            return null;
        }

        var result = new JsonObject();
        foreach (var (name, spec) in manifest.Attributes)
        {
            result[name] = given.TryGetValue(name, out var v) ? Value(spec, v, $"{owner}.{name}") : Fallback(spec, $"{owner}.{name}");
        }

        return result;
    }

    private static JsonNode? Fallback(AttributeSpec spec, string path) =>
        spec.Default is not null ? spec.Default.DeepClone() : spec.Nullable ? null : throw Invalid($"attribute '{path}' is required");

    private static JsonNode? Value(AttributeSpec spec, JsonElement v, string path)
    {
        if (v.ValueKind == JsonValueKind.Null)
        {
            return spec.Nullable ? null : Fallback(spec, path);
        }

        JsonNode? node = spec.Type switch
        {
            "string" when v.ValueKind == JsonValueKind.String => JsonValue.Create(CheckString(spec, v.GetString()!, path)),
            "integer" when v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) => JsonValue.Create(CheckRange(spec, l, path)),
            "boolean" when v.ValueKind is JsonValueKind.True or JsonValueKind.False => JsonValue.Create(v.GetBoolean()),
            "array" when v.ValueKind == JsonValueKind.Array => CheckArray(spec, v, path),
            _ => throw Invalid($"attribute '{path}' has the wrong type"),
        };

        if (spec.Enum is { } options && !options.Any(o => JsonNode.DeepEquals(o, node)))
        {
            throw Invalid($"attribute '{path}' has an unsupported value");
        }

        return node;
    }

    private static string CheckString(AttributeSpec spec, string s, string path)
    {
        if (spec.MaxLength is { } max && s.Length > max)
        {
            throw Invalid($"attribute '{path}' is too long");
        }

        if (s.Any(c => char.IsControl(c) && c is not ('\n' or '\t' or '\r')))
        {
            throw Invalid($"attribute '{path}' contains control characters");
        }

        if (spec.Pattern is { } pattern && !Regex.IsMatch(s, pattern, RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
        {
            throw Invalid($"attribute '{path}' has an invalid format");
        }

        switch (spec.Format)
        {
            case "guid" when !Guid.TryParseExact(s, "D", out _):
                throw Invalid($"attribute '{path}' must be an id");
            case "href" when !IsSafeHref(s):
                throw Invalid($"attribute '{path}' is not an allowed link");
        }

        return s;
    }

    private static long CheckRange(AttributeSpec spec, long value, string path) =>
        (spec.Minimum is { } min && value < min) || (spec.Maximum is { } max && value > max)
            ? throw Invalid($"attribute '{path}' is out of range")
            : value;

    private static JsonArray CheckArray(AttributeSpec spec, JsonElement v, string path)
    {
        if (spec.MaxItems is { } max && v.GetArrayLength() > max)
        {
            throw Invalid($"attribute '{path}' has too many items");
        }

        var item = spec.Items ?? throw Invalid($"attribute '{path}' has no item schema");
        return new JsonArray([.. v.EnumerateArray().Select(x => Value(item with { Nullable = false }, x, path))]);
    }

    /// <summary>Link targets: http(s), mailto, raavi: and same-site paths. Never javascript:, data:, vbscript: or protocol-relative URLs.</summary>
    public static bool IsSafeHref(string href)
    {
        if (string.IsNullOrWhiteSpace(href) || href.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) && c != ' ') || href != href.Trim())
        {
            return false;
        }

        if (href.StartsWith('/') )
        {
            return !href.StartsWith("//", StringComparison.Ordinal) && !href.Contains('\\', StringComparison.Ordinal);
        }

        if (href.StartsWith('#'))
        {
            return true;
        }

        return Uri.TryCreate(href, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https" or "mailto" or "raavi"
            && !href.Contains('\\', StringComparison.Ordinal);
    }

    // ── Plain text (search, AI) ────────────────────────────────────────────────────────────────

    public string PlainText(JsonObject doc)
    {
        var sb = new StringBuilder();
        AppendText(doc, sb);
        return sb.ToString().Trim();
    }

    private void AppendText(JsonObject node, StringBuilder sb)
    {
        var type = node["type"]!.GetValue<string>();
        var manifest = Node(type)!;
        if (type == "text")
        {
            sb.Append(node["text"]!.GetValue<string>());
            return;
        }

        switch (manifest.Search)
        {
            case "none":
                return;
            case "newline":
                sb.Append('\n');
                return;
            case var s when s.StartsWith("attr:", StringComparison.Ordinal):
                var value = node["attrs"]?[s[5..]]?.ToString();
                if (!string.IsNullOrEmpty(value))
                {
                    sb.Append(manifest.Inline ? value : "\n" + value + "\n");
                }

                return;
        }

        if (node["content"] is JsonArray children)
        {
            foreach (var child in children.OfType<JsonObject>())
            {
                AppendText(child, sb);
            }
        }

        if (!manifest.Inline && type != "doc" && sb.Length > 0 && sb[^1] != '\n')
        {
            sb.Append('\n');
        }
    }

    internal static DomainException Invalid(string detail) =>
        new("content.body_invalid", "The page body is not a valid document: " + detail + ".");

    /// <summary>For diagnostics/tests: the canonical integer representation of an attribute default.</summary>
    public static string Describe(AttributeSpec spec) => string.Create(CultureInfo.InvariantCulture, $"{spec.Type}{(spec.Nullable ? "?" : "")}");
}
