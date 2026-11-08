using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Raavi.Domain.Common;

namespace Raavi.Wiki.Domain;

public enum BoardShape
{
    Rect = 1,
    Ellipse = 2,
    Text = 3,
    Sticky = 4,
    Arrow = 5,
    Pen = 6,
}

/// <summary>
/// One element of a whiteboard. Position is the top-left corner (rotation turns the element around it); arrows and pen strokes keep their
/// coordinates in <see cref="Points"/> relative to (X, Y). List order is the stacking order (last = on top).
/// </summary>
public sealed record BoardElement(string Id, BoardShape Type, double X, double Y, double W, double H, double Rotation, string Stroke, string? Fill, int StrokeWidth, string Text, int FontSize, bool Rtl, double[] Points);

/// <summary>Element as sent by a client: every optional field falls back to a default.</summary>
public sealed record BoardElementInput(string? Id, BoardShape Type, double? X, double? Y, double? W, double? H, double? Rotation, string? Stroke, string? Fill, int? StrokeWidth, string? Text, int? FontSize, bool? Rtl, double[]? Points);

internal static class BoardJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };
}

/// <summary>
/// A whiteboard of a space (Phase 9e): a free canvas of shapes, text, sticky notes, arrows and pen strokes, stored as validated JSON and saved
/// as a whole with one ETag. Permissions come from the space (read = View; write = Edit on an active space).
/// </summary>
public sealed partial class Whiteboard : Entity, ITenantOwned
{
    public const int MaxTitleLength = 200;
    public const int MaxElements = 2000;
    public const int MaxTextLength = 2000;
    public const int MaxPointValues = 4000;
    public const double MaxCoordinate = 100_000;
    public const string DefaultStroke = "#1f2937";

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex ColorPattern();

    [GeneratedRegex("^[A-Za-z0-9_-]{1,40}$")]
    private static partial Regex IdPattern();

    private Whiteboard() { }

    public Guid TenantId { get; private set; }

    public Guid SpaceId { get; private set; }

    public string Title { get; private set; } = string.Empty;

    /// <summary>JSON array of <see cref="BoardElement"/>.</summary>
    public string ElementsJson { get; private set; } = "[]";

    public Guid CreatedBy { get; private set; }

    public DateTimeOffset CreatedAt { get; private set; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int Revision { get; private set; } = 1;

    public IReadOnlyList<BoardElement> Elements => JsonSerializer.Deserialize<List<BoardElement>>(ElementsJson, BoardJson.Options) ?? [];

    public static Whiteboard Create(Guid spaceId, string? title, IReadOnlyList<BoardElementInput>? elements, Guid userId, DateTimeOffset now)
    {
        var board = new Whiteboard { SpaceId = spaceId, CreatedBy = userId, CreatedAt = now, UpdatedAt = now };
        board.Set(title, elements ?? []);
        return board;
    }

    /// <summary>Replaces the title and the whole element list (order = stacking order).</summary>
    public void Update(string? title, IReadOnlyList<BoardElementInput>? elements, DateTimeOffset now)
    {
        Set(title, elements ?? []);
        UpdatedAt = now;
        Revision++;
    }

    private void Set(string? title, IReadOnlyList<BoardElementInput> input)
    {
        var t = (title ?? string.Empty).Trim();
        if (t.Length is 0 or > MaxTitleLength || t.Any(char.IsControl))
        {
            throw new DomainException("board.title_invalid", $"A whiteboard needs a title of 1–{MaxTitleLength} characters.");
        }

        if (input.Count > MaxElements)
        {
            throw new DomainException("board.too_many", $"A whiteboard can have at most {MaxElements} elements.");
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<BoardElement>(input.Count);
        foreach (var e in input)
        {
            result.Add(Normalise(e, seen));
        }

        Title = t;
        ElementsJson = JsonSerializer.Serialize(result, BoardJson.Options);
    }

    private static BoardElement Normalise(BoardElementInput e, HashSet<string> seen)
    {
        if (!Enum.IsDefined(e.Type))
        {
            throw Invalid("The element type is not supported.");
        }

        var id = (e.Id ?? string.Empty).Trim();
        if (!IdPattern().IsMatch(id) || !seen.Add(id))
        {
            throw Invalid("Element ids must be unique and use letters, digits, '-' or '_' (up to 40 characters).");
        }

        var x = Coordinate(e.X ?? 0, signed: true);
        var y = Coordinate(e.Y ?? 0, signed: true);
        var sized = e.Type is BoardShape.Rect or BoardShape.Ellipse or BoardShape.Sticky or BoardShape.Text;
        var w = sized ? Coordinate(e.W ?? 0, signed: false) : 0;
        var h = sized ? Coordinate(e.H ?? 0, signed: false) : 0;
        if (e.Type is BoardShape.Rect or BoardShape.Ellipse or BoardShape.Sticky && (w <= 0 || h <= 0))
        {
            throw Invalid("Shapes and sticky notes need a width and a height above zero.");
        }

        var rotation = e.Rotation ?? 0;
        if (!double.IsFinite(rotation) || Math.Abs(rotation) > 36_000)
        {
            throw Invalid("The rotation is not valid.");
        }

        rotation = Math.Round(rotation % 360, 2);
        var stroke = Color(e.Stroke) ?? DefaultStroke;
        var fill = string.IsNullOrWhiteSpace(e.Fill) ? null : Color(e.Fill) ?? throw Invalid("The fill colour must look like #rrggbb.");
        var width = e.StrokeWidth ?? 2;
        if (width is < 1 or > 24)
        {
            throw Invalid("The line width must be between 1 and 24.");
        }

        var font = e.FontSize ?? 20;
        if (font is < 8 or > 200)
        {
            throw Invalid("The font size must be between 8 and 200.");
        }

        var hasText = e.Type is BoardShape.Text or BoardShape.Sticky;
        var text = hasText ? Multiline(e.Text) : string.Empty;
        if (e.Type == BoardShape.Text && string.IsNullOrWhiteSpace(text))
        {
            throw Invalid("A text element cannot be empty.");
        }

        return new BoardElement(id, e.Type, x, y, w, h, rotation, stroke, fill, width, text, font, hasText && (e.Rtl ?? false), Points(e));
    }

    private static double[] Points(BoardElementInput e)
    {
        var points = e.Points ?? [];
        if (e.Type is not (BoardShape.Arrow or BoardShape.Pen))
        {
            return [];
        }

        var valid = e.Type == BoardShape.Arrow ? points.Length == 4 : points.Length is >= 4 and <= MaxPointValues && points.Length % 2 == 0;
        if (!valid || points.Any(p => !double.IsFinite(p) || Math.Abs(p) > MaxCoordinate))
        {
            throw Invalid(e.Type == BoardShape.Arrow ? "An arrow needs exactly two points." : $"A pen stroke needs 2–{MaxPointValues / 2} points.");
        }

        return points.Select(p => Math.Round(p, 2)).ToArray();
    }

    private static double Coordinate(double value, bool signed)
    {
        if (!double.IsFinite(value) || Math.Abs(value) > MaxCoordinate || (!signed && value < 0))
        {
            throw Invalid("A position or size is out of range.");
        }

        return Math.Round(value, 2);
    }

    private static string? Color(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return ColorPattern().IsMatch(value.Trim()) ? value.Trim().ToLower(CultureInfo.InvariantCulture) : throw Invalid("Colours must look like #rrggbb.");
    }

    private static string Multiline(string? value)
    {
        var text = (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim('\n');
        if (text.Length > MaxTextLength || text.Any(c => char.IsControl(c) && c is not ('\n' or '\t')))
        {
            throw Invalid($"Text is limited to {MaxTextLength} characters without control characters.");
        }

        return text;
    }

    private static DomainException Invalid(string message) => new("board.element_invalid", message);
}
