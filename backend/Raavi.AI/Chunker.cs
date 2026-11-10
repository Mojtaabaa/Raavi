using System.Text;

namespace Raavi.AI;

/// <summary>
/// Cuts a page into passages for retrieval (ADR-049): paragraphs are kept whole where possible and merged up to about <see cref="Target"/>
/// characters; a paragraph longer than <see cref="Max"/> is split at sentence ends (or spaces) with a small overlap, so a sentence that straddles
/// two passages is found in either. Never produces an empty passage, never cuts inside a surrogate pair.
/// </summary>
public static class Chunker
{
    public const int Target = 700;
    public const int Max = 1200;
    public const int Overlap = 120;
    public const int MaxChunksPerPage = 200;

    private static readonly System.Buffers.SearchValues<char> SentenceEnds = System.Buffers.SearchValues.Create(".!?؟。۔");

    public static IReadOnlyList<string> Split(string? text)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(text))
        {
            return result;
        }

        var current = new StringBuilder();
        void Flush()
        {
            var t = current.ToString().Trim();
            if (t.Length > 0)
            {
                result.Add(t);
            }

            current.Clear();
        }

        foreach (var raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var paragraph = raw.Trim();
            if (paragraph.Length == 0)
            {
                continue;
            }

            if (paragraph.Length > Max)
            {
                Flush();
                result.AddRange(SplitLong(paragraph));
                continue;
            }

            if (current.Length > 0 && current.Length + paragraph.Length + 1 > Target)
            {
                Flush();
            }

            if (current.Length > 0)
            {
                current.Append('\n');
            }

            current.Append(paragraph);
        }

        Flush();
        return result.Count > MaxChunksPerPage ? result.Take(MaxChunksPerPage).ToList() : result;
    }

    private static IEnumerable<string> SplitLong(string paragraph)
    {
        var start = 0;
        while (start < paragraph.Length)
        {
            var end = Math.Min(paragraph.Length, start + Target);
            if (end < paragraph.Length)
            {
                // prefer the end of a sentence in the last third of the window, else the last space
                var window = paragraph.AsSpan(start, end - start);
                var cut = window.LastIndexOfAny(SentenceEnds);
                if (cut < Target * 2 / 3)
                {
                    cut = window.LastIndexOf(' ');
                }

                if (cut > Target / 3)
                {
                    end = start + cut + 1;
                }

                if (end > start && char.IsHighSurrogate(paragraph[end - 1]))
                {
                    end--;
                }
            }

            var piece = paragraph[start..end].Trim();
            if (piece.Length > 0)
            {
                yield return piece;
            }

            if (end >= paragraph.Length)
            {
                yield break;
            }

            start = Math.Max(start + 1, end - Overlap);
            while (start < paragraph.Length && char.IsLowSurrogate(paragraph[start]))
            {
                start++;
            }
        }
    }
}

/// <summary>Plain vector helpers: embeddings are stored as little-endian float32, unit length, so cosine similarity is a dot product.</summary>
public static class Vectors
{
    public static byte[] Pack(float[] v)
    {
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        var bytes = new byte[v.Length * 4];
        for (var i = 0; i < v.Length; i++)
        {
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4, 4), norm > 0 ? v[i] / norm : 0f);
        }

        return bytes;
    }

    public static float Dot(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b)
    {
        if (a.Length != b.Length || a.Length % 4 != 0)
        {
            return float.NegativeInfinity; // different model/dimension: never similar
        }

        var sum = 0f;
        for (var i = 0; i < a.Length; i += 4)
        {
            sum += BitConverter.ToSingle(a[i..]) * BitConverter.ToSingle(b[i..]);
        }

        return sum;
    }
}
