using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Raavi.Domain.Common;
using Raavi.Wiki.Editor;

namespace Raavi.Wiki.Domain;

/// <summary>
/// Canonical page body (EDITOR_ARCHITECTURE.md §1): <c>{"schema":"raavi.doc","schemaVersion":N,"doc":{…}}</c>.
/// Input is untrusted: it is validated against the extension registry (<see cref="EditorSchema"/>) — unknown nodes,
/// marks or attributes, illegal nesting, unsafe links and oversized documents are rejected — and the stored JSON is a
/// canonical re-serialization (attribute defaults applied), never the client's bytes. Older schema versions are
/// upgraded in memory (lazy migration, §4).
/// </summary>
public sealed class RaaviDocument
{
    public const string SchemaName = "raavi.doc";

    private static readonly Dictionary<int, Func<JsonObject, JsonObject>> Migrations = new()
    {
        // v1 (Phase 3: paragraphs/text/hardBreak with an optional dir) is a subset of v2 except that v1 allowed an empty
        // document ("content": [], e.g. Phase 3 space home pages), which v2's "block+" rejects: an empty document becomes
        // one empty paragraph. The canonicalizer adds the new attribute defaults (textAlign).
        [1] = doc =>
        {
            if (doc["content"] is not JsonArray { Count: > 0 })
            {
                doc["content"] = new JsonArray(new JsonObject { ["type"] = "paragraph" });
            }

            return doc;
        },
        // v2 → v3: new optional textStyle attrs and sub/sup marks; existing trees stay valid.
        [2] = doc => doc,
        // v3 → v4: whiteboard embed node; existing trees stay valid.
        [3] = doc => doc,
        // v4 → v5: page include node; existing trees stay valid.
        [4] = doc => doc,
    };

    private RaaviDocument(JsonObject doc)
    {
        var schema = EditorSchema.Current;
        Doc = doc;
        Json = new JsonObject
        {
            ["schema"] = SchemaName,
            ["schemaVersion"] = schema.Info.SchemaVersion,
            ["doc"] = doc.DeepClone(),
        }.ToJsonString();
        if (Encoding.UTF8.GetByteCount(Json) > MaxBytes)
        {
            throw EditorSchema.Invalid("document is too large");
        }

        PlainText = schema.PlainText(doc);
        Hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Json)));
    }

    public static int CurrentSchemaVersion => EditorSchema.Current.Info.SchemaVersion;

    public static int MaxBytes => EditorSchema.Current.Info.MaxBytes;

    /// <summary>Canonical <c>doc</c> node (do not mutate).</summary>
    public JsonObject Doc { get; }

    public string Json { get; }

    public int SchemaVersion { get; } = CurrentSchemaVersion;

    public string Hash { get; }

    public string PlainText { get; }

    public static RaaviDocument Empty => new(new JsonObject { ["type"] = "doc", ["content"] = new JsonArray(new JsonObject { ["type"] = "paragraph", ["attrs"] = new JsonObject { ["textAlign"] = null, ["dir"] = null } }) });

    /// <summary>One paragraph per line of <paramref name="text"/>.</summary>
    public static RaaviDocument FromPlainText(string? text)
    {
        var lines = (text ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.All(l => l.Length == 0))
        {
            return Empty;
        }

        var content = new JsonArray();
        foreach (var line in lines)
        {
            var clean = new string(line.Where(c => !char.IsControl(c) || c == '\t').ToArray());
            var p = new JsonObject { ["type"] = "paragraph" };
            if (clean.Length > 0)
            {
                p["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = clean });
            }

            content.Add(p);
        }

        using var parsed = JsonDocument.Parse(new JsonObject { ["type"] = "doc", ["content"] = content }.ToJsonString());
        return new RaaviDocument(EditorSchema.Current.Canonicalize(parsed.RootElement));
    }

    public static RaaviDocument Parse(JsonElement element)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
        {
            return Empty;
        }

        if (Encoding.UTF8.GetByteCount(element.GetRawText()) > MaxBytes)
        {
            throw EditorSchema.Invalid("document is too large");
        }

        if (element.ValueKind != JsonValueKind.Object)
        {
            throw EditorSchema.Invalid("document must be an object");
        }

        foreach (var p in element.EnumerateObject())
        {
            if (p.Name is not ("schema" or "schemaVersion" or "doc"))
            {
                throw EditorSchema.Invalid($"unexpected property '{p.Name}'");
            }
        }

        if (!element.TryGetProperty("schema", out var s) || s.ValueKind != JsonValueKind.String || s.GetString() != SchemaName)
        {
            throw EditorSchema.Invalid("unknown schema");
        }

        if (!element.TryGetProperty("schemaVersion", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version)
            || version < 1 || version > CurrentSchemaVersion)
        {
            throw new DomainException("content.schema_unsupported", "This document schema version is not supported.");
        }

        if (!element.TryGetProperty("doc", out var doc) || doc.ValueKind != JsonValueKind.Object)
        {
            throw EditorSchema.Invalid("doc is required");
        }

        if (version < CurrentSchemaVersion)
        {
            var upgraded = JsonNode.Parse(doc.GetRawText())!.AsObject();
            for (var from = version; from < CurrentSchemaVersion; from++)
            {
                upgraded = Migrations[from](upgraded);
            }

            using var migrated = JsonDocument.Parse(upgraded.ToJsonString(), new JsonDocumentOptions { MaxDepth = 256 });
            return new RaaviDocument(EditorSchema.Current.Canonicalize(migrated.RootElement));
        }

        return new RaaviDocument(EditorSchema.Current.Canonicalize(doc));
    }

    public static RaaviDocument Parse(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        if (Encoding.UTF8.GetByteCount(json) > MaxBytes)
        {
            throw EditorSchema.Invalid("document is too large");
        }

        try
        {
            using var parsed = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
            return Parse(parsed.RootElement);
        }
        catch (JsonException)
        {
            throw EditorSchema.Invalid("malformed JSON");
        }
    }
}
