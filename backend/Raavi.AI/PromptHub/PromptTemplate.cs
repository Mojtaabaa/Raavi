using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Raavi.AI.PromptHub;

/// <summary>
/// Prompt template language (ADR-052): <c>{{name}}</c>, <c>{{name|default text}}</c>, <c>{{#if name}}…{{/if}}</c>, <c>{{#unless name}}…{{/unless}}</c>.
/// Deliberately tiny and data-only: names are looked up in a dictionary, nothing is evaluated, nothing can call code, read files or fetch anything.
/// A template is parsed once into a tree; rendering is a pure function of the tree and the values.
/// </summary>
public static partial class PromptTemplate
{
    public const int MaxDepth = 6;
    public const int MaxTags = 400;

    public abstract record Node;

    public sealed record Text(string Value) : Node;

    public sealed record Variable(string Name, string? Default) : Node;

    public sealed record Conditional(string Name, bool Negate, IReadOnlyList<Node> Body) : Node;

    public sealed record Parsed(IReadOnlyList<Node> Nodes, IReadOnlyList<string> Errors)
    {
        public IReadOnlySet<string> Names => Collect(Nodes, includeOptional: true);

        /// <summary>Variables that must have a value: used outside any condition and without a default.</summary>
        public IReadOnlySet<string> Required => Collect(Nodes, includeOptional: false);
    }

    [GeneratedRegex(@"\{\{\s*(.*?)\s*\}\}", RegexOptions.Singleline | RegexOptions.CultureInvariant)]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"^[a-zA-Z][a-zA-Z0-9_.\-]{0,60}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public static bool IsValidName(string name) => NamePattern().IsMatch(name);

    public static Parsed Parse(string? template)
    {
        var errors = new List<string>();
        var root = new List<Node>();
        var stack = new Stack<(string Kind, string Name, List<Node> Body, List<Node> Parent, bool Negate)>();
        var current = root;
        var last = 0;
        var tags = 0;
        var text = template ?? string.Empty;
        foreach (Match m in TagPattern().Matches(text))
        {
            if (m.Index > last)
            {
                current.Add(new Text(text[last..m.Index]));
            }

            last = m.Index + m.Length;
            if (++tags > MaxTags)
            {
                errors.Add("template.too_many_tags");
                break;
            }

            var inner = m.Groups[1].Value;
            if (inner.StartsWith("#if ", StringComparison.Ordinal) || inner.StartsWith("#unless ", StringComparison.Ordinal))
            {
                var negate = inner.StartsWith("#unless", StringComparison.Ordinal);
                var name = inner[(inner.IndexOf(' ', StringComparison.Ordinal) + 1)..].Trim();
                if (!IsValidName(name))
                {
                    errors.Add($"template.invalid_name:{name}");
                    continue;
                }

                if (stack.Count >= MaxDepth)
                {
                    errors.Add("template.too_deep");
                    continue;
                }

                var body = new List<Node>();
                stack.Push((negate ? "unless" : "if", name, body, current, negate));
                current = body;
            }
            else if (inner is "/if" or "/unless")
            {
                if (stack.Count == 0 || stack.Peek().Kind != inner[1..])
                {
                    errors.Add($"template.unexpected_close:{inner}");
                    continue;
                }

                var open = stack.Pop();
                open.Parent.Add(new Conditional(open.Name, open.Negate, open.Body));
                current = open.Parent;
            }
            else
            {
                var bar = inner.IndexOf('|', StringComparison.Ordinal);
                var name = (bar < 0 ? inner : inner[..bar]).Trim();
                if (!IsValidName(name))
                {
                    errors.Add($"template.invalid_name:{name}");
                    continue;
                }

                current.Add(new Variable(name, bar < 0 ? null : inner[(bar + 1)..].Trim()));
            }
        }

        if (last < text.Length)
        {
            current.Add(new Text(text[last..]));
        }

        while (stack.Count > 0)
        {
            var open = stack.Pop();
            errors.Add($"template.unclosed:{open.Kind} {open.Name}");
            open.Parent.Add(new Conditional(open.Name, open.Negate, open.Body));
        }

        return new Parsed(root, errors.Distinct().ToList());
    }

    private static HashSet<string> Collect(IReadOnlyList<Node> nodes, bool includeOptional)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        void Walk(IReadOnlyList<Node> list, bool optional)
        {
            foreach (var n in list)
            {
                switch (n)
                {
                    case Variable v when includeOptional || (!optional && v.Default is null):
                        set.Add(v.Name);
                        break;
                    case Conditional c:
                        if (includeOptional)
                        {
                            set.Add(c.Name);
                        }

                        Walk(c.Body, optional: true);
                        break;
                }
            }
        }

        Walk(nodes, optional: false);
        return set;
    }

    /// <summary>Renders the tree. <paramref name="wrap"/> lets the caller fence values (used to mark untrusted context before it goes to a model).</summary>
    public static string Render(Parsed parsed, IReadOnlyDictionary<string, string> values, Func<string, string, string>? wrap = null)
    {
        ArgumentNullException.ThrowIfNull(parsed);
        ArgumentNullException.ThrowIfNull(values);
        var sb = new StringBuilder();

        bool Truthy(string name) => values.TryGetValue(name, out var v) && !string.IsNullOrWhiteSpace(v) && !string.Equals(v, "false", StringComparison.OrdinalIgnoreCase);

        void Walk(IReadOnlyList<Node> list)
        {
            foreach (var n in list)
            {
                switch (n)
                {
                    case Text t:
                        sb.Append(t.Value);
                        break;
                    case Variable v:
                        var has = values.TryGetValue(v.Name, out var value) && !string.IsNullOrEmpty(value);
                        var shown = has ? value! : v.Default ?? string.Empty;
                        sb.Append(has && wrap is not null ? wrap(v.Name, shown) : shown);
                        break;
                    case Conditional c when Truthy(c.Name) != c.Negate:
                        Walk(c.Body);
                        break;
                }
            }
        }

        Walk(parsed.Nodes);
        return sb.ToString().Trim().ReplaceLineEndings("\n");
    }

    /// <summary>Rough size in model tokens: about one token per 3.5 characters (a conservative blend for English and Persian; the provider's count is exact and is recorded after a run).</summary>
    public static int EstimateTokens(string text) => (int)Math.Ceiling((text ?? string.Empty).Length / 3.5d);

    public static string Number(double d) => d.ToString("0.################", CultureInfo.InvariantCulture);
}
