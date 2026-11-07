using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Raavi.Wiki.Editor;

public enum DiffOp
{
    Equal = 1,
    Insert = 2,
    Delete = 3,
    Change = 4,
}

public sealed record DiffSegment(DiffOp Op, string Text);

/// <summary>One block of the comparison. <see cref="Segments"/> holds the word-level diff of a changed block.</summary>
public sealed record DiffBlock(DiffOp Op, string Type, string? Before, string? After, IReadOnlyList<DiffSegment>? Segments);

/// <summary>
/// Structural document comparison (ADR-019): documents are flattened into blocks (text blocks and atom blocks, in
/// document order, with their container path), blocks are aligned with an LCS over their signatures, and replaced
/// blocks of the same type are compared word by word. Both inputs are canonical documents of the current schema
/// (older versions are migrated before comparing).
/// </summary>
public static partial class DocumentDiff
{
    private const int MaxBlocks = 4000;
    private const int MaxTokens = 3000;

    private sealed record Block(string Type, string Signature, string Text);

    public static IReadOnlyList<DiffBlock> Compare(JsonObject before, JsonObject after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var a = Flatten(before);
        var b = Flatten(after);
        if (a.Count > MaxBlocks || b.Count > MaxBlocks)
        {
            // Too large for an alignment: report as one replacement rather than spending unbounded CPU.
            return [new DiffBlock(DiffOp.Change, "doc", string.Join('\n', a.Select(x => x.Text)), string.Join('\n', b.Select(x => x.Text)), null)];
        }

        var script = Lcs(a, b, (x, y) => x.Signature == y.Signature);
        var result = new List<DiffBlock>();
        var deletes = new List<Block>();
        var inserts = new List<Block>();

        void Flush()
        {
            var pairs = 0;
            foreach (var del in deletes.ToList())
            {
                var match = inserts.FirstOrDefault(i => i.Type == del.Type);
                if (match is null)
                {
                    continue;
                }

                result.Add(new DiffBlock(DiffOp.Change, del.Type, del.Text, match.Text, Words(del.Text, match.Text)));
                deletes.Remove(del);
                inserts.Remove(match);
                pairs++;
            }

            result.AddRange(deletes.Select(d => new DiffBlock(DiffOp.Delete, d.Type, d.Text, null, null)));
            result.AddRange(inserts.Select(i => new DiffBlock(DiffOp.Insert, i.Type, null, i.Text, null)));
            deletes.Clear();
            inserts.Clear();
        }

        foreach (var (op, x, y) in script)
        {
            switch (op)
            {
                case DiffOp.Equal:
                    Flush();
                    result.Add(new DiffBlock(DiffOp.Equal, x!.Type, x.Text, y!.Text, null));
                    break;
                case DiffOp.Delete:
                    deletes.Add(x!);
                    break;
                default:
                    inserts.Add(y!);
                    break;
            }
        }

        Flush();
        return result;
    }

    /// <summary>Word-level diff (tokens are words, whitespace and punctuation; works for Persian and English).</summary>
    public static IReadOnlyList<DiffSegment> Words(string before, string after)
    {
        var a = TokenPattern().Matches(before ?? string.Empty).Select(m => m.Value).ToList();
        var b = TokenPattern().Matches(after ?? string.Empty).Select(m => m.Value).ToList();
        if (a.Count > MaxTokens || b.Count > MaxTokens)
        {
            return [new DiffSegment(DiffOp.Delete, before ?? string.Empty), new DiffSegment(DiffOp.Insert, after ?? string.Empty)];
        }

        var segments = new List<DiffSegment>();
        foreach (var (op, x, y) in Lcs(a, b, string.Equals))
        {
            var text = op == DiffOp.Insert ? y! : x!;
            if (segments.Count > 0 && segments[^1].Op == op)
            {
                segments[^1] = segments[^1] with { Text = segments[^1].Text + text };
            }
            else
            {
                segments.Add(new DiffSegment(op, text));
            }
        }

        return segments;
    }

    private static List<(DiffOp Op, T? A, T? B)> Lcs<T>(IReadOnlyList<T> a, IReadOnlyList<T> b, Func<T, T, bool> equal)
        where T : class
    {
        var n = a.Count;
        var m = b.Count;
        var table = new int[n + 1, m + 1];
        for (var i = n - 1; i >= 0; i--)
        {
            for (var j = m - 1; j >= 0; j--)
            {
                table[i, j] = equal(a[i], b[j]) ? table[i + 1, j + 1] + 1 : Math.Max(table[i + 1, j], table[i, j + 1]);
            }
        }

        var script = new List<(DiffOp, T?, T?)>();
        int x = 0, y = 0;
        while (x < n && y < m)
        {
            if (equal(a[x], b[y]))
            {
                script.Add((DiffOp.Equal, a[x], b[y]));
                x++;
                y++;
            }
            else if (table[x + 1, y] >= table[x, y + 1])
            {
                script.Add((DiffOp.Delete, a[x++], null));
            }
            else
            {
                script.Add((DiffOp.Insert, null, b[y++]));
            }
        }

        while (x < n)
        {
            script.Add((DiffOp.Delete, a[x++], null));
        }

        while (y < m)
        {
            script.Add((DiffOp.Insert, null, b[y++]));
        }

        return script;
    }

    private static List<Block> Flatten(JsonObject doc)
    {
        var blocks = new List<Block>();
        Walk(doc, string.Empty, blocks);
        return blocks;
    }

    private static void Walk(JsonObject node, string path, List<Block> blocks)
    {
        var type = node["type"]?.GetValue<string>() ?? "?";
        var manifest = EditorSchema.Current.Node(type);
        var children = node["content"] as JsonArray;
        var isTextBlock = manifest?.Content is { } c && (c.Contains("inline", StringComparison.Ordinal) || c.Contains("text", StringComparison.Ordinal));
        if (type != "doc" && (isTextBlock || (manifest is { Inline: false } && manifest.Content is null)))
        {
            var text = EditorSchema.Current.PlainText(node);
            var label = isTextBlock || text.Length > 0 ? text : $"[{type}]";
            // The signature includes attributes and formatting so a change of level, panel type or marks is visible.
            var signature = path + "/" + type + "|" + (node["attrs"]?.ToJsonString() ?? string.Empty) + "|" + Marks(node) + "|" + label;
            blocks.Add(new Block(type, signature, label));
            return;
        }

        if (children is null)
        {
            return;
        }

        var childPath = type == "doc" ? string.Empty : path + "/" + type + (node["attrs"] is JsonObject a ? a.ToJsonString() : string.Empty);
        foreach (var child in children.OfType<JsonObject>())
        {
            Walk(child, childPath, blocks);
        }
    }

    private static string Marks(JsonObject node)
    {
        if (node["content"] is not JsonArray inline)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var n in inline.OfType<JsonObject>())
        {
            sb.Append(n["marks"]?.ToJsonString()).Append(n["attrs"]?.ToJsonString()).Append(';');
        }

        return sb.ToString();
    }

    [GeneratedRegex(@"\w+|\s+|[^\w\s]", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();
}
