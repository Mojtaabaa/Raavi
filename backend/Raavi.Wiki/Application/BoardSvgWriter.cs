using System.Globalization;
using System.Text;
using Raavi.Wiki.Domain;

namespace Raavi.Wiki.Application;

/// <summary>
/// Renders a whiteboard as a standalone SVG. Every attribute value comes from validated numbers and #rrggbb colours and all text is
/// XML-escaped, so a board can never inject markup. Wrapped text (sticky notes, text with a width) is broken with an estimated glyph width,
/// so line breaks can differ slightly from the on-screen canvas; the PNG export of the canvas is the exact picture.
/// </summary>
internal static class BoardSvgWriter
{
    private const double Padding = 40;
    private const string Font = "Calibri, 'Segoe UI', Vazirmatn, Tahoma, sans-serif";
    private const string StickyDefault = "#fde68a";

    public static string Write(string title, IReadOnlyList<BoardElement> elements, string? background = null)
    {
        var (minX, minY, maxX, maxY) = Bounds(elements);
        var sb = new StringBuilder();
        var width = Math.Max(1, maxX - minX);
        var height = Math.Max(1, maxY - minY);
        var bg = string.IsNullOrWhiteSpace(background) || background == "transparent" ? "#ffffff" : background;
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{N(width)}\" height=\"{N(height)}\" viewBox=\"{N(minX)} {N(minY)} {N(width)} {N(height)}\">\n");
        sb.Append("<title>").Append(Esc(title)).Append("</title>\n");
        sb.Append(CultureInfo.InvariantCulture, $"<rect x=\"{N(minX)}\" y=\"{N(minY)}\" width=\"{N(width)}\" height=\"{N(height)}\" fill=\"{bg}\"/>\n");
        foreach (var e in elements.Where(x => !x.Hidden))
        {
            sb.Append(CultureInfo.InvariantCulture, $"<g transform=\"translate({N(e.X)} {N(e.Y)}) rotate({N(e.Rotation)})\">");
            Element(sb, e);
            sb.Append("</g>\n");
        }

        sb.Append("</svg>\n");
        return sb.ToString();
    }

    private static void Element(StringBuilder sb, BoardElement e)
    {
        var stroke = $"stroke=\"{e.Stroke}\" stroke-width=\"{e.StrokeWidth}\"";
        switch (e.Type)
        {
            case BoardShape.Rect:
                sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{N(e.W)}\" height=\"{N(e.H)}\" fill=\"{e.Fill ?? "none"}\" {stroke}/>");
                break;
            case BoardShape.RoundRect:
                sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{N(e.W)}\" height=\"{N(e.H)}\" rx=\"{N(e.CornerRadius)}\" ry=\"{N(e.CornerRadius)}\" fill=\"{e.Fill ?? "none"}\" {stroke}/>");
                break;
            case BoardShape.Ellipse:
                sb.Append(CultureInfo.InvariantCulture, $"<ellipse cx=\"{N(e.W / 2)}\" cy=\"{N(e.H / 2)}\" rx=\"{N(e.W / 2)}\" ry=\"{N(e.H / 2)}\" fill=\"{e.Fill ?? "none"}\" {stroke}/>");
                break;
            case BoardShape.Diamond:
                sb.Append(CultureInfo.InvariantCulture, $"<polygon points=\"{N(e.W / 2)},0 {N(e.W)},{N(e.H / 2)} {N(e.W / 2)},{N(e.H)} 0,{N(e.H / 2)}\" fill=\"{e.Fill ?? "none"}\" stroke-linejoin=\"round\" {stroke}/>");
                break;
            case BoardShape.Triangle:
                sb.Append(CultureInfo.InvariantCulture, $"<polygon points=\"{N(e.W / 2)},0 {N(e.W)},{N(e.H)} 0,{N(e.H)}\" fill=\"{e.Fill ?? "none"}\" stroke-linejoin=\"round\" {stroke}/>");
                break;
            case BoardShape.Star:
                sb.Append(CultureInfo.InvariantCulture, $"<polygon points=\"{StarPoints(e.W, e.H)}\" fill=\"{e.Fill ?? "none"}\" stroke-linejoin=\"round\" {stroke}/>");
                break;
            case BoardShape.Line:
                sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{N(e.Points[0])}\" y1=\"{N(e.Points[1])}\" x2=\"{N(e.Points[2])}\" y2=\"{N(e.Points[3])}\" stroke-linecap=\"round\" {stroke}/>");
                break;
            case BoardShape.Connector:
                if (e.Points.Length >= 4)
                {
                    sb.Append("<polyline fill=\"none\" stroke-linecap=\"round\" stroke-linejoin=\"round\" ").Append(stroke).Append(" points=\"");
                    for (var i = 0; i + 1 < e.Points.Length; i += 2)
                    {
                        sb.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? string.Empty : " ")}{N(e.Points[i])},{N(e.Points[i + 1])}");
                    }

                    sb.Append("\"/>");
                    if (!string.IsNullOrEmpty(e.Text))
                    {
                        TextBlock(sb, e, e.Points[0], e.Points[1], 0);
                    }
                }

                break;
            case BoardShape.Image:
                sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{N(e.W)}\" height=\"{N(e.H)}\" fill=\"#e5e7eb\" {stroke}/>");
                if (!string.IsNullOrEmpty(e.Alt))
                {
                    sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{N(e.W / 2)}\" y=\"{N(e.H / 2)}\" text-anchor=\"middle\" font-family=\"{Font}\" font-size=\"14\" fill=\"#6b7280\">{Esc(e.Alt)}</text>");
                }

                break;
            case BoardShape.Table:
                sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{N(e.W)}\" height=\"{N(e.H)}\" fill=\"{e.Fill ?? "#ffffff"}\" {stroke}/>");
                break;
            case BoardShape.Group:
                sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{N(e.W)}\" height=\"{N(e.H)}\" fill=\"none\" stroke-dasharray=\"4 4\" {stroke}/>");
                break;
            case BoardShape.Sticky:
                sb.Append(CultureInfo.InvariantCulture, $"<rect width=\"{N(e.W)}\" height=\"{N(e.H)}\" fill=\"{e.Fill ?? StickyDefault}\" stroke=\"none\"/>");
                TextBlock(sb, e, 10, 10, Math.Max(0, e.W - 20));
                break;
            case BoardShape.Text:
                TextBlock(sb, e, 0, 0, e.W);
                break;
            case BoardShape.Arrow:
                Arrow(sb, e, stroke);
                break;
            case BoardShape.Pen:
                sb.Append("<polyline fill=\"none\" stroke-linecap=\"round\" stroke-linejoin=\"round\" ").Append(stroke).Append(" points=\"");
                for (var i = 0; i + 1 < e.Points.Length; i += 2)
                {
                    sb.Append(CultureInfo.InvariantCulture, $"{(i == 0 ? string.Empty : " ")}{N(e.Points[i])},{N(e.Points[i + 1])}");
                }

                sb.Append("\"/>");
                break;
        }
    }

    private static string StarPoints(double w, double h)
    {
        var cx = w / 2;
        var cy = h / 2;
        var outerX = w / 2;
        var outerY = h / 2;
        var innerX = outerX * 0.4;
        var innerY = outerY * 0.4;
        var pts = new StringBuilder();
        for (var i = 0; i < 10; i++)
        {
            var angle = (-Math.PI / 2) + (i * Math.PI / 5);
            var rx = i % 2 == 0 ? outerX : innerX;
            var ry = i % 2 == 0 ? outerY : innerY;
            if (i > 0)
            {
                pts.Append(' ');
            }

            pts.Append(CultureInfo.InvariantCulture, $"{N(cx + (rx * Math.Cos(angle)))},{N(cy + (ry * Math.Sin(angle)))}");
        }

        return pts.ToString();
    }

    private static void Arrow(StringBuilder sb, BoardElement e, string stroke)
    {
        double x1 = e.Points[0], y1 = e.Points[1], x2 = e.Points[2], y2 = e.Points[3];
        sb.Append(CultureInfo.InvariantCulture, $"<line x1=\"{N(x1)}\" y1=\"{N(y1)}\" x2=\"{N(x2)}\" y2=\"{N(y2)}\" stroke-linecap=\"round\" {stroke}/>");
        var angle = Math.Atan2(y2 - y1, x2 - x1);
        var size = 8 + (e.StrokeWidth * 2);
        string P(double a) => $"{N(x2 - (size * Math.Cos(angle + a)))},{N(y2 - (size * Math.Sin(angle + a)))}";
        sb.Append(CultureInfo.InvariantCulture, $"<polygon points=\"{N(x2)},{N(y2)} {P(0.45)} {P(-0.45)}\" fill=\"{e.Stroke}\" stroke=\"none\"/>");
    }

    private static void TextBlock(StringBuilder sb, BoardElement e, double dx, double dy, double limit)
    {
        var lines = Wrap(e.Text, e.FontSize, limit);
        var anchorX = e.Rtl && limit > 0 ? dx + limit : dx;
        sb.Append(CultureInfo.InvariantCulture, $"<text x=\"{N(anchorX)}\" y=\"{N(dy + e.FontSize)}\" font-family=\"{Font}\" font-size=\"{e.FontSize}\" fill=\"{(e.Type == BoardShape.Sticky ? "#1f2937" : e.Stroke)}\" xml:space=\"preserve\"");
        if (e.Rtl)
        {
            sb.Append(" direction=\"rtl\" text-anchor=\"").Append(limit > 0 ? "end" : "start").Append('"');
        }

        sb.Append('>');
        for (var i = 0; i < lines.Count; i++)
        {
            sb.Append(CultureInfo.InvariantCulture, $"<tspan x=\"{N(anchorX)}\" dy=\"{(i == 0 ? "0" : N(e.FontSize * 1.2))}\">").Append(Esc(lines[i])).Append("</tspan>");
        }

        sb.Append("</text>");
    }

    /// <summary>Greedy word wrap with an estimated glyph width of 0.55 × font size; a limit of 0 means "do not wrap".</summary>
    private static List<string> Wrap(string text, int fontSize, double limit)
    {
        var result = new List<string>();
        var perLine = limit <= 0 ? int.MaxValue : Math.Max(1, (int)(limit / (fontSize * 0.55)));
        foreach (var paragraph in text.Split('\n'))
        {
            var line = new StringBuilder();
            foreach (var word in paragraph.Split(' '))
            {
                if (line.Length > 0 && line.Length + 1 + word.Length > perLine)
                {
                    result.Add(line.ToString());
                    line.Clear();
                }

                if (line.Length > 0)
                {
                    line.Append(' ');
                }

                line.Append(word);
            }

            result.Add(line.ToString());
        }

        return result;
    }

    private static (double MinX, double MinY, double MaxX, double MaxY) Bounds(IReadOnlyList<BoardElement> elements)
    {
        if (elements.Count == 0)
        {
            return (0, 0, 400, 300);
        }

        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var e in elements)
        {
            var corners = e.Type is BoardShape.Arrow or BoardShape.Pen or BoardShape.Line
                ? Enumerable.Range(0, e.Points.Length / 2).Select(i => (e.Points[2 * i], e.Points[(2 * i) + 1])).ToList()
                : [(0d, 0d), (e.W, 0d), (e.W, e.H), (0d, e.H)];
            if (e.Type == BoardShape.Text && e.W <= 0)
            {
                var lines = e.Text.Split('\n');
                corners = [(0d, 0d), (lines.Max(l => l.Length) * e.FontSize * 0.55, lines.Length * e.FontSize * 1.2)];
            }

            var (sin, cos) = Math.SinCos(e.Rotation * Math.PI / 180);
            var pad = (e.StrokeWidth / 2d) + 8;
            foreach (var (cx, cy) in corners)
            {
                var px = e.X + (cx * cos) - (cy * sin);
                var py = e.Y + (cx * sin) + (cy * cos);
                minX = Math.Min(minX, px - pad);
                minY = Math.Min(minY, py - pad);
                maxX = Math.Max(maxX, px + pad);
                maxY = Math.Max(maxY, py + pad);
            }
        }

        return (Math.Floor(minX - Padding), Math.Floor(minY - Padding), Math.Ceiling(maxX + Padding), Math.Ceiling(maxY + Padding));
    }

    private static string N(double value) => Math.Round(value, 2).ToString("0.##", CultureInfo.InvariantCulture);

    private static string Esc(string value) =>
        value.Replace("&", "&amp;", StringComparison.Ordinal).Replace("<", "&lt;", StringComparison.Ordinal).Replace(">", "&gt;", StringComparison.Ordinal).Replace("\"", "&quot;", StringComparison.Ordinal);
}
